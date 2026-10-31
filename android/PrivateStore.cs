using Brs.Portable.Content;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Brs.Portable.Runtime;

namespace Brs.AndroidLocalHost;

// All methods execute off Android's main thread. User data remains outside runtime/.
internal static class PrivateStore
{
    public static string RuntimeRoot(string root) => Path.Combine(root, SourceRuntime.DirectoryName);
    public static string ContentRoot(string root) => Path.Combine(root, "content");
    public static string KeyFile(string root) => Path.Combine(root, "supported-client.key");
    public static string DbFile(string root) => Path.Combine(root, "accounts.sqlite3");
    public static void RequireRegular(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("private_reparse");
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("private_parent_reparse");
    }
    public static void AtomicWrite(string path, byte[] bytes, bool replaceExisting = false)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, replaceExisting);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void PrepareRuntime(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(root); RequireRegular(root);
        var contentRoot = ContentRoot(root);
        var materials = ResourcePackage.MaterialsRoot(contentRoot);
        if (!Directory.Exists(materials)) throw new InvalidDataException("setup_required_resource_package");
        RequireRegular(materials);
        ResourcePackage.Configure(contentRoot);
        var importedKey = ResourcePackage.KeyPath(contentRoot);
        PrepareRuntimeInputs(root, importedKey, () => SourceRuntime.Prepare(RuntimeRoot(root), materials), token);
    }

    internal static void PrepareRuntimeInputs(string root, string importedKey, Action prepareInputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireRegular(importedKey);
        if (new FileInfo(importedKey).Length != 16) throw new InvalidDataException("key_asset_length");
        var bytes = File.ReadAllBytes(importedKey);
        try
        {
            var keyPath = KeyFile(root);
            if (File.Exists(keyPath))
            {
                RequireRegular(keyPath);
                if (new FileInfo(keyPath).Length != 16) throw new InvalidDataException("existing_key_identity");
                var existing = File.ReadAllBytes(keyPath);
                try { if (!CryptographicOperations.FixedTimeEquals(existing, bytes)) throw new InvalidDataException("existing_key_identity"); }
                finally { CryptographicOperations.ZeroMemory(existing); }
            }
            token.ThrowIfCancellationRequested();
            prepareInputs();
            token.ThrowIfCancellationRequested();
            if (!File.Exists(keyPath)) AtomicWrite(keyPath, bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static string OwnerKey(string root)
    {
        var path = Path.Combine(root, "owner.key");
        if (!File.Exists(path))
        {
            var entropy = RandomNumberGenerator.GetBytes(32);
            try { AtomicWrite(path, Encoding.ASCII.GetBytes(Convert.ToBase64String(entropy).TrimEnd('=').Replace('+', '-').Replace('/', '_'))); }
            finally { CryptographicOperations.ZeroMemory(entropy); }
        }
        RequireRegular(path);
        if (new FileInfo(path).Length != 43) throw new InvalidDataException("owner_key_identity");
        var key = File.ReadAllText(path, Encoding.ASCII);
        if (key.Length != 43 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new InvalidDataException("owner_key_format");
        return key;
    }
    public static DomainMarker? ExistingDomain(string root)
    {
        var db = DbFile(root); var marker = Path.Combine(root, "store-domain.json");
        if (!File.Exists(db) && !File.Exists(marker)) return null;
        if (!File.Exists(db) || !File.Exists(marker)) throw new InvalidDataException("store_domain_marker_required");
        RequireRegular(marker); RequireRegular(db);
        if (new FileInfo(marker).Length > 1024) throw new InvalidDataException("store_domain_marker_invalid");
        var value = JsonSerializer.Deserialize<DomainMarker>(File.ReadAllBytes(marker)) ?? throw new InvalidDataException("store_domain_marker_invalid");
        using var identity = Android.Systems.Os.Stat(db) ?? throw new InvalidDataException("db_identity_unavailable");
        if (value.FormatVersion != 1 || !Guid.TryParseExact(value.DomainId, "D", out _) || value.Device != identity.StDev || value.Inode != identity.StIno)
            throw new InvalidDataException("store_domain_identity_mismatch");
        return value;
    }
    public static DomainMarker CreateDomain(string root)
    {
        RequireRegular(DbFile(root));
        using var identity = Android.Systems.Os.Stat(DbFile(root)) ?? throw new InvalidDataException("db_identity_unavailable");
        var value = new DomainMarker(1, Guid.NewGuid().ToString("D"), identity.StDev, identity.StIno);
        AtomicWrite(Path.Combine(root, "store-domain.json"), JsonSerializer.SerializeToUtf8Bytes(value));
        return value;
    }
}
internal sealed record DomainMarker(int FormatVersion, string DomainId, long Device, long Inode);
