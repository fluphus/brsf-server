using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record ProfileConfig(string PolicyVersion, int NameMaximumLength, int MessageMaximumLength,
    string InputPolicy, string BirthdayPolicy, string TitlePolicy, string NgWordPolicy)
{
    public const string ExpectedFingerprint = "cfd16acadce0eb3ba9884d99ee4802f3c8fb1d10d76f17bdd3b0ea84b9e0701e";

    public static ProfileConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 4096 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid profile policy path.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Profile policy fingerprint mismatch.");
        var c = JsonSerializer.Deserialize<ProfileConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing profile policy.");
        if (c.PolicyVersion != "local-profile-v1" || c.NameMaximumLength != 64 || c.MessageMaximumLength != 256 ||
            c.InputPolicy != "bounded-bmp-no-marks-other-symbol-unassigned-control-preserve-spaces" ||
            c.BirthdayPolicy != "M/d-leap-year2000-set-once" || c.TitlePolicy != "zero-only-until-real-ownership-source" ||
            c.NgWordPolicy != "not-reconstructed") throw new InvalidDataException("Unsupported profile policy.");
        return c;
    }

    public bool ValidText(string text, bool name)
    {
        // Original validator refuses surrogates, Marks, OtherSymbol and
        // Unassigned, and the input fields reject LF. Font-glyph validation
        // is unavailable here. UTF16 limits and rejecting all controls are
        // explicit local bounds; neither is claimed as the old server rule.
        if (text.Length > (name ? NameMaximumLength : MessageMaximumLength) || name && string.IsNullOrWhiteSpace(text)) return false;
        return text.All(c => !char.IsSurrogate(c) && !char.IsControl(c) && char.GetUnicodeCategory(c) is not
            (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
             UnicodeCategory.OtherSymbol or UnicodeCategory.OtherNotAssigned));
    }
}
