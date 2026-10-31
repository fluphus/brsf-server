using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Brs.Local.Accounts;

public sealed record CreateAccountRequest(string Nonce, byte[] Fingerprint);

public static class AccountRequests
{
    public static CreateAccountRequest ReadCreate(JsonElement root)
    {
        ExactKnownProperties(root, "common", "nonce", "os_type", "ios_idfv", "ios_idfa", "android_aaid");
        var nonce = ReadString(root, "nonce", 43, required: true);
        if (!Regex.IsMatch(nonce, "\\A[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid nonce encoding.");
        if (!root.TryGetProperty("os_type", out var os) || os.ValueKind != JsonValueKind.Number || !os.TryGetInt32(out var osType) || osType != 2)
            throw new InvalidDataException("This local account policy requires Android os_type2.");
        // Normalize absent/null/empty advertising strings, store only the combined
        // semantic fingerprint. Neither raw identifiers nor request body persist.
        var semantic = new object[] { osType, ReadString(root, "ios_idfv", 256),
            ReadString(root, "ios_idfa", 256), ReadString(root, "android_aaid", 256) };
        return new CreateAccountRequest(nonce, SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(semantic)));
    }

    public static string ReadLogin(JsonElement root)
    {
        ExactKnownProperties(root, "common", "uuid");
        var value = ReadString(root, "uuid", 36, required: true);
        if (!Guid.TryParseExact(value, "D", out var uuid)) throw new InvalidDataException("Invalid UUID format.");
        return uuid.ToString("D");
    }

    private static string ReadString(JsonElement root, string key, int maximum, bool required = false)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new InvalidDataException("Missing account request property.");
            return "";
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length > maximum || (required && text.Length == 0))
            throw new InvalidDataException("Invalid account request property.");
        return text;
    }

    private static void ExactKnownProperties(JsonElement root, params string[] known)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name) || !known.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Duplicate or unsupported account request property.");
    }
}
