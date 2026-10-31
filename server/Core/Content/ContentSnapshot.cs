using System.IO.Compression;
using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Content;

public sealed record ContentItem(string ZipMember, long Length, string Hash, bool HasResourceQuery);

public sealed class ContentSnapshot : IDisposable
{
    public const string ArchiveSha256 = "a720ec6b2dbbdb11facd5c70015a07aa96f7cd161209bb283b8bbbe87c63079e";
    public const string CatalogSha256 = "e32eec1daf2019aaa459f3507480f94a50311eedeb32125eb7dcb582a79249c3";
    public const string CatalogName = "gb9e3bd4239a4d18dabe2b4740336fde";
    public const string MasterName = "ra7843688bc3abfa77b4331970f135680";
    public const string FilesRoot = "jp.goodsmile.brsfragment/files/";
    private readonly FileStream archiveFile;
    private readonly ZipArchive archive;
    private readonly SemaphoreSlim reader = new(1, 1);
    private readonly Dictionary<string, ContentItem> byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
    private readonly LocalContentOverlay? overlay;
    public int ResourceCount { get; }
    public int ResourceRevision => overlay?.ResourceRevision ?? 142;
    public int MasterRevision => overlay?.MasterRevision ?? 136;

    public ContentSnapshot(string archivePath, string decodedCatalogPath, LocalContentOverlay? overlay = null)
    {
        this.overlay = overlay;
        using var catalogSource = new FileStream(decodedCatalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (catalogSource.Length > 16 * 1024 * 1024) throw new InvalidDataException("Catalog exceeds size limit.");
        var bytes = new byte[checked((int)catalogSource.Length)];
        catalogSource.ReadExactly(bytes);
        VerifiedClient.RequireHash(bytes, CatalogSha256);
        using var catalog = JsonDocument.Parse(bytes);
        var root = catalog.RootElement;
        if (root.GetProperty("ResourceVersion").ToString() != "142" || root.GetProperty("MasterVersion").ToString() != "136")
            throw new InvalidDataException("Unsupported resource revision.");

        // Keep the source open with read sharing only, so it cannot change while served.
        archiveFile = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            VerifiedClient.RequireHash(archiveFile, ArchiveSha256);
            archiveFile.Position = 0;
            archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                if (!entries.TryAdd(entry.FullName, entry))
                    throw new InvalidDataException("Ambiguous duplicate ZIP member.");
            }
            foreach (var item in root.GetProperty("Items").EnumerateArray())
            {
                var isMaster = item.GetProperty("LoadPath").GetString() == "MasterData";
                var name = isMaster ? MasterName : item.GetProperty("FileName").GetString()!;
                if (string.IsNullOrEmpty(name) || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                    throw new InvalidDataException("Unexpected catalog filename.");
                var length = item.GetProperty("Size").GetInt64();
                var hash = item.GetProperty("Hash").GetString()!;
                var member = FilesRoot + "asset/" + name;
                if (!entries.TryGetValue(member, out var entry)) throw new InvalidDataException("Missing catalog resource.");
                if (entry.Length != length) throw new InvalidDataException("Catalog size mismatch.");
                var path = isMaster ? "/Android/MasterData/136/" + name : "/Android/Asset/" + name;
                byPath.Add(path, new ContentItem(member, length, hash, !isMaster));
            }
            ResourceCount = byPath.Count;
            if (ResourceCount != 14374) throw new InvalidDataException("Incomplete snapshot.");
            var catalogMember = FilesRoot + CatalogName;
            if (!entries.TryGetValue(catalogMember, out var catalogEntry)) throw new InvalidDataException("Missing catalog.");
            byPath.Add("/Android/142/" + CatalogName, new ContentItem(catalogMember, catalogEntry.Length,
                "58eaa07189651fc52a7e8732389edf42e9db7fe1901435215cd2655183582b00", true));
            if (overlay is not null)
            {
                byPath.Add(overlay.CatalogPath, new ContentItem(LocalContentOverlay.CatalogStorageKey,
                    overlay.CatalogLength, overlay.CatalogHash, true));
                byPath.Add(overlay.MasterPath, new ContentItem(LocalContentOverlay.MasterStorageKey,
                    overlay.MasterLength, overlay.MasterHash, false));
            }
        }
        catch
        {
            archive?.Dispose();
            archiveFile.Dispose();
            reader.Dispose();
            throw;
        }
    }

    public bool TryResolve(string path, IReadOnlyDictionary<string, IReadOnlyList<string>> query, out ContentItem? item)
    {
        if (!byPath.TryGetValue(path, out item)) return false;
        var valid = ValidResourceQuery(path, item.HasResourceQuery, query, overlay is not null, overlay?.ResourceRevision ?? 143);
        if (!valid) item = null;
        return valid;
    }

    internal static bool ValidResourceQuery(string path, bool hasResourceQuery, IReadOnlyDictionary<string, IReadOnlyList<string>> query, bool hasOverlay, int overlayRevision = 143)
    {
        if (!hasResourceQuery) return query.Count == 0;
        if (query.Count != 1 || !query.TryGetValue("v", out var version) || version.Count != 1) return false;
        if (path == "/Android/142/" + CatalogName) return version[0] == "142";
        if (hasOverlay && path == $"/Android/{overlayRevision}/" + CatalogName) return version[0] == overlayRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return version[0] == "142" || hasOverlay && version[0] == overlayRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task CopyToAsync(ContentItem item, Stream output, CancellationToken cancel)
    {
        if (overlay is not null && overlay.TryGetContent(item.ZipMember, out var localBytes))
        {
            await output.WriteAsync(localBytes, cancel);
            return;
        }
        // ZipArchive uses one seekable source. Hold the gate until the entry stream closes.
        await reader.WaitAsync(cancel);
        try
        {
            using var input = entries[item.ZipMember].Open();
            await input.CopyToAsync(output, cancel);
        }
        finally { reader.Release(); }
    }

    public void Dispose()
    {
        archive.Dispose();
        archiveFile.Dispose();
        reader.Dispose();
    }
}
