using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Brs.Portable.Content;

internal static class ArchiveInventory
{
    // Actual central directory: 14,501 files plus 22 explicit directory entries.
    // Fixed digest covers every ordered name/length/compressed-length, including directories.
    public const int EntryCount = 14523;
    public const string ExpectedSha256 = "265ee4db43f335b6636987cac50185d675fbbe05509062b4a24fc1cc5d8ecd74";
    public static string Validate(ZipArchive archive)
    {
        if (archive.Entries.Count != EntryCount) throw new InvalidDataException("Unsupported ZIP inventory count.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var names = new HashSet<string>(StringComparer.Ordinal); var files = 0; var directories = 0;
        Span<byte> number = stackalloc byte[8];
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!SafeName(name) || !names.Add(name) || entry.Length < 0 || entry.CompressedLength < 0)
                throw new InvalidDataException("Unsafe or duplicate ZIP member.");
            if (name.EndsWith('/')) { directories++; if (entry.Length != 0) throw new InvalidDataException("Invalid directory entry."); }
            else files++;
            var bytes = Encoding.UTF8.GetBytes(name);
            BinaryPrimitives.WriteInt32LittleEndian(number, bytes.Length); hash.AppendData(number[..4]); hash.AppendData(bytes);
            BinaryPrimitives.WriteInt64LittleEndian(number, entry.Length); hash.AppendData(number);
            BinaryPrimitives.WriteInt64LittleEndian(number, entry.CompressedLength); hash.AppendData(number);
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (files != 14501 || directories != 22 || digest != ExpectedSha256) throw new InvalidDataException("Unsupported ZIP inventory identity.");
        return digest;
    }
    internal static bool SafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '/' || name.Contains('\\') || name.Contains(':') || name.Any(c => c < ' ' || c == 127)) return false;
        var path = name.EndsWith('/') ? name[..^1] : name;
        return path.Length > 0 && !path.Split('/').Any(segment => segment is "" or "." or "..");
    }
}
