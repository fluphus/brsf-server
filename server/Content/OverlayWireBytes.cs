using System.Security.Cryptography;
using System.Text.Json;
using Brs.Local.Content;
using Brs.Local.Transport;

namespace Brs.Portable.Content;

internal sealed class OverlayWireBytes
{
    private readonly Dictionary<string, byte[]> bytes = new(StringComparer.Ordinal);
    public OverlayWireBytes(LocalContentOverlay overlay, string manifestPath)
    {
        if (Path.GetFileName(manifestPath) != "manifest.json") throw new InvalidDataException("Invalid overlay manifest name.");
        var manifestBytes = StoreSafety.ReadSmall(manifestPath, 65536);
        // The logical policy fingerprint stays stable for existing battle audits;
        // sanitized metadata has its own independently validated byte identity.
        VerifiedClient.RequireHash(manifestBytes, overlay.ManifestSha256);
        using var manifest = JsonDocument.Parse(manifestBytes);
        foreach (var (name, key, expectedLength, expectedWireHash, wireSha) in new[]
        {
            ("catalog.encoded.bin", LocalContentOverlay.CatalogStorageKey, overlay.CatalogLength, overlay.CatalogHash, true),
            ("master.encoded.bin", LocalContentOverlay.MasterStorageKey, overlay.MasterLength, overlay.MasterHash, false)
        })
        {
            var data = StoreSafety.ReadSmall(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, name), 16 * 1024 * 1024);
            var spec = manifest.RootElement.GetProperty("files").GetProperty(name);
            var sha = Convert.ToHexStringLower(SHA256.HashData(data)); var md5 = Convert.ToHexStringLower(MD5.HashData(data));
            if (data.LongLength != spec.GetProperty("size").GetInt64() || data.LongLength != expectedLength || sha != spec.GetProperty("sha256").GetString() ||
                md5 != spec.GetProperty("md5").GetString() || (wireSha ? sha : md5) != expectedWireHash)
                throw new InvalidDataException("Overlay wire identity differs from validated Core overlay.");
            bytes.Add(key, data);
        }
    }
    public bool TryGet(string key, out byte[] value) => bytes.TryGetValue(key, out value!);
}
