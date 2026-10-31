using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record CardSkinCard(int CardId, int DefaultSkinId, bool IsDeleted);
public sealed record CardSkinMapping(int SkinId, int CardId, bool IsDeleted);
public sealed record CardSkinConfig(string PolicyVersion, string SourceMasterSha256, string SourceMappingSha256,
    string AuthorizationPolicy, CardSkinCard[] Cards, CardSkinMapping[] Skins)
{
    public const string ExpectedFingerprint = "2bd989f5857713f11a24802fc51291df23a5dce15e46c8d79af8e59b5dc8bdea";
    public static CardSkinConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 65536 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid card skin configuration.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Card skin fingerprint mismatch.");
        var value = JsonSerializer.Deserialize<CardSkinConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing card skin snapshot.");
        if (value.PolicyVersion != "original-owned-card-skin-selection-v1" ||
            value.SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            value.SourceMappingSha256 != "eec9ad168fcf9f3bef97e2a2fd799b4d6e8a59b044db3b30bf928c62f2c2f7a4" ||
            value.AuthorizationPolicy != "owned-card-default-implicit-nondefault-owned-no-awakening-grant" ||
            value.Cards.Length != 76 || value.Skins.Length != 164 || value.Cards.Select(c => c.CardId).Distinct().Count() != 76 ||
            value.Skins.Select(s => s.SkinId).Distinct().Count() != 164 ||
            value.Cards.Any(c => c.CardId <= 0 || c.DefaultSkinId <= 0 || c.IsDeleted ||
                !value.Skins.Any(s => s.SkinId == c.DefaultSkinId && s.CardId == c.CardId)) ||
            value.Skins.Any(s => s.SkinId <= 0 || s.IsDeleted || !value.Cards.Any(c => c.CardId == s.CardId)))
            throw new InvalidDataException("Unsupported card skin snapshot.");
        return value;
    }

    public bool CanUseSkin(int cardId, int skinId, bool owned)
    {
        var card = Cards.SingleOrDefault(c => c.CardId == cardId && !c.IsDeleted);
        var skin = Skins.SingleOrDefault(s => s.SkinId == skinId && s.CardId == cardId && !s.IsDeleted);
        return card is not null && skin is not null && (card.DefaultSkinId == skinId || owned);
    }
}
