using System.Security.Cryptography;

namespace Brs.Local.Transport;

public static class VerifiedClient
{
    public const string NativeSha256 = "53c8fc3fa25163d6865395698845a0473a746a0828866b27ed7c048720e30b65";
    public const string MetadataSha256 = "4201b32ba2d3220940cd04259fa49f10aad48bec8fdb98a31d6ffd1d43954c76";
    public const string KeySha256 = "2ddd1c793f03c901236abf29e8feaaa669e0de502196f39c8362b3690ae8f644";

    public static PacketCipher LoadCipher(string nativePath, string metadataPath)
    {
        using (var native = File.OpenRead(nativePath)) RequireHash(native, NativeSha256);
        using var metadata = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        RequireHash(metadata, MetadataSha256);
        metadata.Position = 496569;
        Span<byte> key = stackalloc byte[16];
        try
        {
            metadata.ReadExactly(key);
            RequireHash(key, KeySha256);
            return new PacketCipher(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static void RequireHash(Stream source, string expected)
    {
        if (!Convert.ToHexStringLower(SHA256.HashData(source)).Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException("Source SHA256 does not match the supported snapshot.");
    }

    public static void RequireHash(ReadOnlySpan<byte> source, string expected)
    {
        if (!Convert.ToHexStringLower(SHA256.HashData(source)).Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException("Source SHA256 does not match the supported snapshot.");
    }
}
