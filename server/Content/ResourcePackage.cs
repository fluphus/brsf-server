using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Brs.Local.Content;

namespace Brs.Portable.Content;

public sealed record ResourceMaterial(string Path, long Length, string Sha256);
public sealed record IncompleteResourceImport(string Path, long Bytes);

// Standard uncompressed TAR: pinned material members followed by the unchanged
// supported content.zip. TarReader processes non-seekable Android URI streams.
public static class ResourcePackage
{
    private static readonly byte[] manifest = ReadManifest();
    public static IReadOnlyList<ResourceMaterial> Materials { get; } = ReadMaterials();
    public static string MaterialsRoot(string storeRoot) => Path.Combine(Path.GetFullPath(storeRoot), ArchiveImporter.GenerationName, "materials");
    public static string KeyPath(string storeRoot) => Path.Combine(MaterialsRoot(storeRoot), "protocol-key.bin");

    public static void Configure(string storeRoot)
    {
        VerifyMaterials(MaterialsRoot(storeRoot));
        RuntimeMaterialFiles.Configure(MaterialsRoot(storeRoot));
    }

    public static async Task<ArchiveImportReceipt> ImportAsync(Stream package, string storeRoot,
        CancellationToken token = default, IProgress<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(package); token.ThrowIfCancellationRequested();
        var root = StoreSafety.DirectoryPath(storeRoot, create: true);
        var stage = Path.Combine(root, ".package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var materialRoot = Path.Combine(stage, "materials"); Directory.CreateDirectory(materialRoot);
        try
        {
            using var tar = new TarReader(package, leaveOpen: true);
            var first = await tar.GetNextEntryAsync(copyData: false, cancellationToken: token).ConfigureAwait(false);
            RequireEntry(first, "resource-package-manifest.json", manifest.Length);
            var suppliedManifest = new byte[manifest.Length];
            await first!.DataStream!.ReadExactlyAsync(suppliedManifest, token).ConfigureAwait(false);
            if (!suppliedManifest.AsSpan().SequenceEqual(manifest)) throw new InvalidDataException("Unsupported resource package inventory.");
            foreach (var material in Materials)
            {
                token.ThrowIfCancellationRequested();
                var entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken: token).ConfigureAwait(false);
                RequireEntry(entry, "materials/" + material.Path, material.Length);
                var target = Path.Combine(materialRoot, material.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; long count = 0; int read;
                while ((read = await entry!.DataStream!.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    count = checked(count + read); if (count > material.Length) throw new InvalidDataException("Oversized resource material.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (count != material.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != material.Sha256)
                    throw new InvalidDataException("Resource material identity differs.");
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            var content = await tar.GetNextEntryAsync(copyData: false, cancellationToken: token).ConfigureAwait(false);
            RequireEntry(content, "content.zip", ArchiveImporter.SupportedArchiveLength);
            ArchiveImportReceipt receipt;
            var generation = Path.Combine(root, ArchiveImporter.GenerationName);
            if (ArchiveImporter.HasPublishedGeneration(root))
            {
                // Existing accounts and content stay in place. Read/verify the one selected
                // package, then atomically attach its material directory to this generation.
                receipt = ArchiveImporter.LoadReceipt(Path.Combine(generation, ArchiveImporter.ReceiptName));
                var archive = Path.Combine(generation, ArchiveImporter.ArchiveName); StoreSafety.CheckParents(archive);
                if (new FileInfo(archive).Length != receipt.ArchiveLength || File.GetLastWriteTimeUtc(archive).Ticks != receipt.LastWriteUtcTicks ||
                    (File.GetAttributes(archive) & FileAttributes.ReadOnly) == 0) throw new InvalidDataException("Existing content generation differs.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[131072]; long count = 0, reported = 0; int read;
                while ((read = await content!.DataStream!.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    count = checked(count + read); hash.AppendData(buffer, 0, read);
                    if (count - reported >= 8 * 1024 * 1024) { progress?.Report(count); reported = count; }
                }
                if (count != ArchiveImporter.SupportedArchiveLength || Convert.ToHexStringLower(hash.GetHashAndReset()) != ContentSnapshot.ArchiveSha256)
                    throw new InvalidDataException("Unsupported content archive identity.");
            }
            else receipt = await ArchiveImporter.ImportAsync(content!.DataStream!, stage, token, progress).ConfigureAwait(false);
            if (await tar.GetNextEntryAsync(copyData: false, cancellationToken: token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("Unexpected resource package member.");
            token.ThrowIfCancellationRequested(); StoreSafety.CheckParents(root);
            if (Directory.Exists(generation))
            {
                if (Directory.Exists(MaterialsRoot(root))) VerifyMaterials(MaterialsRoot(root));
                else Directory.Move(materialRoot, MaterialsRoot(root));
            }
            else
            {
                var prepared = Path.Combine(stage, ArchiveImporter.GenerationName);
                Directory.Move(materialRoot, Path.Combine(prepared, "materials"));
                Directory.Move(prepared, generation);
            }
            progress?.Report(ArchiveImporter.SupportedArchiveLength);
            return receipt;
        }
        finally { DeleteStage(stage); }
    }

    public static IReadOnlyList<IncompleteResourceImport> ScanIncompleteImports(string storeRoot)
    {
        if (!Directory.Exists(storeRoot)) return [];
        var root = StoreSafety.DirectoryPath(storeRoot); var found = new List<IncompleteResourceImport>();
        foreach (var path in Directory.EnumerateDirectories(root, ".package-*", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileName(path)[9..], "N", out _)) continue;
            var files = StageFiles(path);
            found.Add(new(path, files.Sum(p => new FileInfo(p).Length)));
        }
        return found;
    }

    public static void DeleteIncompleteImports(string storeRoot, IReadOnlyList<IncompleteResourceImport> selected, CancellationToken token = default)
    {
        var actual = ScanIncompleteImports(storeRoot).ToDictionary(i => i.Path, StringComparer.Ordinal);
        foreach (var item in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(item.Path, out var current) || current.Bytes != item.Bytes) throw new IOException("Incomplete import changed; scan again.");
            DeleteStage(item.Path);
        }
    }

    private static void RequireEntry(TarEntry? entry, string name, long length)
    {
        if (entry is null || entry.Name != name || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
            entry.Length != length || entry.DataStream is null || !string.IsNullOrEmpty(entry.LinkName))
            throw new InvalidDataException("Missing or unsupported resource package member: " + name);
    }

    private static byte[] ReadManifest()
    {
        using var resource = typeof(ResourcePackage).Assembly.GetManifestResourceStream("Brs.Portable.Content.ResourcePackageManifest.json")!;
        using var output = new MemoryStream(); resource.CopyTo(output); return output.ToArray();
    }
    private static IReadOnlyList<ResourceMaterial> ReadMaterials()
    {
        using var json = JsonDocument.Parse(manifest);
        return Array.AsReadOnly(json.RootElement.GetProperty("files").EnumerateArray().Select(e => new ResourceMaterial(
            e.GetProperty("path").GetString()!, e.GetProperty("length").GetInt64(), e.GetProperty("sha256").GetString()!)).ToArray());
    }
    private static void VerifyMaterials(string root)
    {
        foreach (var material in Materials)
        {
            var path = Path.Combine(root, material.Path.Replace('/', Path.DirectorySeparatorChar)); StoreSafety.CheckParents(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != material.Length || Convert.ToHexStringLower(SHA256.HashData(input)) != material.Sha256)
                throw new InvalidDataException("Imported resource material identity differs: " + material.Path);
        }
    }
    private static string[] StageFiles(string root)
    {
        StoreSafety.CheckParents(root);
        var permitted = Materials.Select(m => "materials/" + m.Path).ToHashSet(StringComparer.Ordinal);
        permitted.UnionWith(["generation-v1/content.zip", "generation-v1/receipt.json"]);
        // ArchiveImporter staging is always a direct child containing its fixed two files.
        var result = new List<string>();
        void Walk(string directory)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                StoreSafety.CheckParents(child);
                if (Directory.Exists(child)) { Walk(child); continue; }
                var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
                var parts = relative.Split('/');
                var importerFile = parts.Length == 2 && parts[0].StartsWith(".import-", StringComparison.Ordinal) &&
                    Guid.TryParseExact(parts[0][8..], "N", out _) && parts[1] is "content.zip" or "receipt.json";
                if (!permitted.Contains(relative) && !importerFile) throw new InvalidDataException("Unexpected import staging file.");
                result.Add(child);
            }
        }
        Walk(root); return result.ToArray();
    }
    private static void DeleteStage(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in StageFiles(root)) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
            Directory.Delete(directory, recursive: false);
        Directory.Delete(root, recursive: false);
    }
}
