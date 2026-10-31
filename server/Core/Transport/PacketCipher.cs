using System.Security.Cryptography;

namespace Brs.Local.Transport;

// Daruma.NetworkEncryptor: a fresh 16-byte IV, then AES-128-CBC / PKCS7.
// This legacy packet format provides encryption, not message authentication.
public sealed class PacketCipher : IDisposable
{
    public const int MaximumPacketBytes = 1024 * 1024;
    private readonly byte[] key;

    public PacketCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16) throw new ArgumentException("AES-128 requires a 16-byte key.");
        this.key = key.ToArray();
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = RandomNumberGenerator.GetBytes(16);
        var ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
        var packet = new byte[checked(iv.Length + ciphertext.Length)];
        iv.CopyTo(packet, 0);
        ciphertext.CopyTo(packet, 16);
        return packet;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 32 || packet.Length > MaximumPacketBytes || packet.Length % 16 != 0)
            throw new InvalidDataException("Invalid encrypted packet length.");
        using var aes = Aes.Create();
        aes.Key = key;
        try { return aes.DecryptCbc(packet[16..], packet[..16], PaddingMode.PKCS7); }
        catch (CryptographicException) { throw new InvalidDataException("Invalid encrypted packet."); }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
