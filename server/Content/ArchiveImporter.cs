using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brs.Local.Content;

namespace Brs.Portable.Content;

public sealed record ArchiveImportReceipt(int FormatVersion, string ArchiveSha256, long ArchiveLength,
    string InventorySha256, int EntryCount, long LastWriteUtcTicks);

public static class ArchiveImporter
{
    public const long SupportedArchiveLength = 8157596864L;
    public const string GenerationName = "generation-v1";
    public const string ArchiveName = "content.zip";
    public const string ReceiptName = "receipt.json";
    internal static readonly JsonSerializerOptions ReceiptJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static bool HasPublishedGeneration(string storeRoot)
    {
        var full = Path.GetFullPath(storeRoot); StoreSafety.CheckParents(full);
        if (!Directory.Exists(full))
        { if (File.Exists(full)) throw new InvalidDataException("Content store is not a directory."); return false; }
        var root = StoreSafety.DirectoryPath(full); var generation = Path.Combine(root, GenerationName);
        StoreSafety.CheckParents(generation);
        // A corrupt fixed generation path also counts as published: Open must fail closed.
        return Directory.Exists(generation) || File.Exists(generation);
    }

    public static async Task<ArchiveImportReceipt> ImportAsync(Stream source, string storeRoot,
        CancellationToken token = default, IProgress<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source); token.ThrowIfCancellationRequested();
        if (!source.CanRead) throw new ArgumentException("Readable archive stream required.", nameof(source));
        var root = StoreSafety.DirectoryPath(storeRoot, create: true);
        var generation = Path.Combine(root, GenerationName);
        StoreSafety.CheckParents(generation);
        if (Directory.Exists(generation) || File.Exists(generation)) throw new IOException("Content generation already exists; no replacement allowed.");
        var stage = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var archivePath = Path.Combine(stage, ArchiveName); var receiptPath = Path.Combine(stage, ReceiptName);
        var published = false;
        try
        {
            long copied = 0, reported = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[131072]; int n;
                while ((n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, SupportedArchiveLength - copied + 1)), token).ConfigureAwait(false)) != 0)
                {
                    copied = checked(copied + n);
                    if (copied > SupportedArchiveLength) throw new InvalidDataException("Archive exceeds supported length.");
                    hash.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                    if (copied - reported >= 8 * 1024 * 1024) { progress?.Report(copied); reported = copied; }
                }
                if (copied != SupportedArchiveLength || Convert.ToHexStringLower(hash.GetHashAndReset()) != ContentSnapshot.ArchiveSha256)
                    throw new InvalidDataException("Unsupported archive length or SHA256.");
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            string inventory;
            using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read)) inventory = ArchiveInventory.Validate(zip);
            File.SetAttributes(archivePath, File.GetAttributes(archivePath) | FileAttributes.ReadOnly);
            var receipt = new ArchiveImportReceipt(1, ContentSnapshot.ArchiveSha256, copied, inventory, ArchiveInventory.EntryCount, File.GetLastWriteTimeUtc(archivePath).Ticks);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, ReceiptJson);
            await using (var output = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            File.SetAttributes(receiptPath, File.GetAttributes(receiptPath) | FileAttributes.ReadOnly);
            token.ThrowIfCancellationRequested(); StoreSafety.CheckParents(root); StoreSafety.CheckParents(generation);
            // Atomic same-filesystem publish. Never overwrite a successful or concurrently published generation.
            Directory.Move(stage, generation); published = true; progress?.Report(copied); return receipt;
        }
        finally
        {
            if (!published)
            {
                // Delete only our two fixed staging children, never recurse through a supplied path.
                StoreSafety.CheckParents(stage);
                foreach (var path in new[] { receiptPath, archivePath })
                    if (File.Exists(path)) { StoreSafety.CheckParents(path); File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: false);
            }
        }
    }

    internal static ArchiveImportReceipt LoadReceipt(string path)
    {
        var bytes = StoreSafety.ReadSmall(path, 4096);
        using var json = JsonDocument.Parse(bytes); var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        string[] required = ["formatVersion", "archiveSha256", "archiveLength", "inventorySha256", "entryCount", "lastWriteUtcTicks"];
        if (names.Length != required.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(required)) throw new InvalidDataException("Invalid import receipt schema.");
        var receipt = JsonSerializer.Deserialize<ArchiveImportReceipt>(bytes, ReceiptJson) ?? throw new InvalidDataException("Import receipt missing.");
        if (receipt.FormatVersion != 1 || receipt.ArchiveSha256 != ContentSnapshot.ArchiveSha256 || receipt.ArchiveLength != SupportedArchiveLength ||
            receipt.InventorySha256 != ArchiveInventory.ExpectedSha256 || receipt.EntryCount != ArchiveInventory.EntryCount || receipt.LastWriteUtcTicks < 1)
            throw new InvalidDataException("Unsupported import receipt identity.");
        return receipt;
    }
}
