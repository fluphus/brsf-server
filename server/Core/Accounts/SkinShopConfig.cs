using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record SkinShopProduct(int ContentsType, int ContentsId, int ParentProductId, string ProductId,
    int OsType, bool IsDeleted, int ConsumeItemType, int ConsumeItemId, int Price, int PurchaseLimit, int ResetType,
    string OpenDate, string CloseDate, int ItemType, int CardId, int CardSkinId, int ItemQty)
{
    public bool IsOpen(long now)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).DateTime;
        return !IsDeleted && (OpenDate.Length == 0 || local >= DateTime.ParseExact(OpenDate, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)) &&
            (CloseDate.Length == 0 || local <= DateTime.ParseExact(CloseDate, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture));
    }
}

// The pinned complete original type3 catalog is data, not per-product branches.
public sealed record SkinShopConfig(string PolicyVersion, string SourceMasterSha256, string SourceExcerptSha256,
    bool RequiresOwnedCard, SkinShopProduct[] Products)
{
    public const string ExpectedFingerprint = "060a7026d0950195a738d2de01ba32ba6314b1a35769e0fb5ee8ca045bf577c3";
    public static SkinShopConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 65536 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid skin shop configuration.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Skin shop fingerprint mismatch.");
        var value = JsonSerializer.Deserialize<SkinShopConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing skin shop snapshot.");
        if (value.PolicyVersion != "original-type3-skin-catalog-v2" ||
            value.SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            value.SourceExcerptSha256 != "029c00a97ac90da17f7185412466f48f8848a16c017aa6a7a1eba322a1459ea3" || value.RequiresOwnedCard ||
            value.Products.Length != 19 || value.Products.Select(p => p.ProductId).Distinct(StringComparer.Ordinal).Count() != value.Products.Length ||
            value.Products.Select(p => p.ParentProductId).Distinct().Count() != value.Products.Length ||
            value.Products.Select(p => p.CardSkinId).Distinct().Count() != value.Products.Length ||
            value.Products.Any(p => p.ContentsType != 3 || p.ContentsId <= 0 || p.ParentProductId <= 0 || p.ProductId.Length is < 1 or > 128 ||
                p.OsType != 0 || p.IsDeleted || p.ConsumeItemType != 1 || p.ConsumeItemId != 0 || p.Price <= 0 || p.PurchaseLimit <= 0 || p.ResetType != 0 ||
                p.ItemType != 8 || p.CardId <= 0 || p.CardSkinId <= 0 || p.ItemQty != 1 || !ValidDate(p.OpenDate) || !ValidDate(p.CloseDate)))
            throw new InvalidDataException("Unsupported skin shop catalog.");
        return value;
    }
    private static bool ValidDate(string date) => date.Length == 0 ||
        DateTime.TryParseExact(date, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
