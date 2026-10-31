using System.Security.Cryptography;
using Brs.Local.Transport;

namespace Brs.SupportedClient.KeyMaterial;

public static class SupportedClientCipher
{
    public const int KeyLength = 16;
    public const string KeySha256 = VerifiedClient.KeySha256;

    // Runtime reads only the separately prepared tiny asset. It never opens native/metadata sources.
    public static PacketCipher Load(string keyFile)
    {
        using var input = new FileStream(keyFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != KeyLength) throw new InvalidDataException("Unsupported protocol key asset length.");
        Span<byte> key = stackalloc byte[KeyLength];
        try
        {
            input.ReadExactly(key);
            VerifiedClient.RequireHash(key, KeySha256);
            return new PacketCipher(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
