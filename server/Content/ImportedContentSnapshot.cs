using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Brs.Local.Content;
using Brs.Local.Transport;

namespace Brs.Portable.Content;

public sealed partial class ImportedContentSnapshot : IDisposable
{
    public const string CatalogName = ContentSnapshot.CatalogName;
    public const string MasterName = ContentSnapshot.MasterName;
    public const string FilesRoot = ContentSnapshot.FilesRoot;
    private readonly FileStream archiveFile;
    private readonly CountingReadStream counted;
    private readonly ZipArchive archive;
    private readonly SemaphoreSlim reader = new(1, 1);
    private readonly Dictionary<string, ContentItem> byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
    private readonly LocalContentOverlay? overlay;
    private readonly OverlayWireBytes? overlayBytes;
    private readonly HashSet<ContentItem> allowed;
    private int disposed;
    public int ResourceCount { get; private set; }
    public int ResourceRevision => overlay?.CaptureDynamicContent()?.ResourceRevision ?? overlay?.ResourceRevision ?? 142;
    public int MasterRevision => overlay?.CaptureDynamicContent()?.MasterRevision ?? overlay?.MasterRevision ?? 136;
    public long ArchiveBytesReadOnOpen { get; }
    // Read-only diagnostic: HEAD must not advance this after cold metadata opening.
    public long ArchiveBytesRead => counted.BytesRead;

    public static ImportedContentSnapshot Open(string storeRoot, string decodedCatalogPath, LocalContentOverlay? overlay = null, string? overlayManifestPath = null) =>
        new(storeRoot, decodedCatalogPath, overlay, overlayManifestPath);

    private ImportedContentSnapshot(string storeRoot, string decodedCatalogPath, LocalContentOverlay? overlay, string? overlayManifestPath)
    {
        var rootPath = StoreSafety.DirectoryPath(storeRoot);
        var generation = StoreSafety.DirectoryPath(Path.Combine(rootPath, ArchiveImporter.GenerationName));
        var receiptPath = Path.Combine(generation, ArchiveImporter.ReceiptName);
        var archivePath = Path.Combine(generation, ArchiveImporter.ArchiveName);
        var receipt = ArchiveImporter.LoadReceipt(receiptPath); StoreSafety.CheckParents(archivePath);
        if ((File.GetAttributes(receiptPath) & FileAttributes.ReadOnly) == 0 || (File.GetAttributes(archivePath) & FileAttributes.ReadOnly) == 0 ||
            File.GetLastWriteTimeUtc(archivePath).Ticks != receipt.LastWriteUtcTicks) throw new InvalidDataException("Content generation is not immutable.");
        var bytes = StoreSafety.ReadSmall(decodedCatalogPath, 16 * 1024 * 1024);
        VerifiedClient.RequireHash(bytes, ContentSnapshot.CatalogSha256);
        using var catalog = JsonDocument.Parse(bytes); var root = catalog.RootElement;
        if (root.GetProperty("ResourceVersion").ToString() != "142" || root.GetProperty("MasterVersion").ToString() != "136") throw new InvalidDataException("Unsupported source catalog revisions.");
        this.overlay = overlay;
        if (overlay is not null) overlayBytes = new OverlayWireBytes(overlay, overlayManifestPath ?? throw new InvalidDataException("Explicit overlay manifest path required."));
        else if (overlayManifestPath is not null) throw new InvalidDataException("Manifest supplied without validated overlay.");
        archiveFile = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        counted = new CountingReadStream(archiveFile);
        try
        {
            if (archiveFile.Length != receipt.ArchiveLength) throw new InvalidDataException("Imported archive length changed.");
            archive = new ZipArchive(counted, ZipArchiveMode.Read, leaveOpen: true);
            ArchiveInventory.Validate(archive);
            foreach (var entry in archive.Entries) if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Duplicate ZIP member.");
            BuildRoutes(root); allowed = byPath.Values.ToHashSet(); ArchiveBytesReadOnOpen = counted.BytesRead;
        }
        catch { archive?.Dispose(); archiveFile.Dispose(); reader.Dispose(); throw; }
    }

    public async Task<Stream> OpenVerifiedBodyAsync(ContentItem item, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (ExpertPeriodContentSnapshot.TryOpenBody(item, out var dynamicBody)) return dynamicBody!;
        if (!allowed.Contains(item)) throw new InvalidDataException("Content item does not belong to this snapshot.");
        if (overlayBytes is not null && overlayBytes.TryGet(item.ZipMember, out var local)) return new MemoryStream(local, writable: false);
        await reader.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var entry = entries[item.ZipMember];
            using (var verification = entry.Open()) await VerifyEntryAsync(verification, item, token).ConfigureAwait(false);
            // The same held archive handle and lease are retained until this second stream is disposed.
            return new ReaderLeaseStream(entry.Open(), reader);
        }
        catch { reader.Release(); throw; }
    }

    internal static async Task VerifyEntryAsync(Stream input, ContentItem item, CancellationToken token)
    {
        var algorithm = item.Hash.Length == 64 ? HashAlgorithmName.SHA256 : item.Hash.Length == 32 ? HashAlgorithmName.MD5 : throw new InvalidDataException("Unsupported wire hash.");
        using var hash = IncrementalHash.CreateHash(algorithm); var buffer = new byte[65536]; long count = 0; int n;
        while ((n = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { count = checked(count + n); if (count > item.Length) throw new InvalidDataException("Resource length exceeded."); hash.AppendData(buffer, 0, n); }
        if (count != item.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != item.Hash) throw new InvalidDataException("Resource hash or length mismatch.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Composition stops/disposes response bodies before disposing the snapshot.
        reader.Wait();
        try { archive.Dispose(); archiveFile.Dispose(); }
        finally { reader.Release(); }
    }

    private sealed class ReaderLeaseStream(Stream input, SemaphoreSlim gate) : Stream
    {
        private int disposed;
        public override bool CanRead => input.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => input.Length; public override long Position { get => input.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => input.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => input.ReadAsync(buffer, offset, count, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) try { input.Dispose(); } finally { gate.Release(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) == 0) try { await input.DisposeAsync().ConfigureAwait(false); } finally { gate.Release(); } GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingReadStream(Stream input) : Stream
    {
        private long bytesRead;
        public long BytesRead => Interlocked.Read(ref bytesRead);
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false; public override long Length => input.Length; public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { var n = input.Read(buffer, offset, count); Interlocked.Add(ref bytesRead, n); return n; }
        public override int Read(Span<byte> buffer) { var n = input.Read(buffer); Interlocked.Add(ref bytesRead, n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); Interlocked.Add(ref bytesRead, n); return n; }
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin); public override void Flush() => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
