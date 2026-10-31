using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record CommerceReward(int ItemType, int ItemId, int ItemQty);
public sealed record CommerceReference(int ItemType, int ItemId, int OwnerId);
public sealed record CommerceExtraProduct(int ContentsId, int ProductId, int ConsumeItemType, int ConsumeItemId,
    int ConsumeItemQty, int RewardType, int ConditionType, int[] ConditionQuestIds, int PurchaseLimit,
    string OpenDate, string CloseDate, bool IsDeleted);
public sealed record CommerceProduct(int ContentsType, int ContentsId, string ProductId, int ParentProductId,
    int ProductGroup, int GroupPriority, int OsType, bool IsDeleted, int ConsumeItemType, int ConsumeItemId,
    int Price, int ResetType, int PurchaseLimit, string OpenDate, string CloseDate, int GachaGroupId, CommerceReward[] Rewards)
{
    public bool IsOpen(long now) => !IsDeleted && OsType is 0 or 2 && CommerceDates.IsOpen(OpenDate, CloseDate, now);
}

public sealed record CommerceConfig(string PolicyVersion, string ContentPolicy, int ResourceRevision, int MasterRevision,
    string SourceMasterSha256, string SourceDateMasterSha256, string SourceExcerptSha256, bool LocalExternalMode,
    string LocalReceiptMarker, int BackgroundDuplicateFreeQty, CommerceProduct[] Products,
    CommerceExtraProduct[] ExtraProducts, CommerceReference[] ItemReferences, GachaEquip[] SupplementalEquips)
{
    public const string ExpectedFingerprint = "ceba7217829cb7b6776c9284b6cb8ce1c980757e54c462b4698027aaf2afd805";
    public static CommerceConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 2 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid commerce configuration file.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Commerce fingerprint mismatch.");
        var result = JsonSerializer.Deserialize<CommerceConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing commerce configuration.");
        result.Validate(); return result;
    }

    private void Validate()
    {
        if (PolicyVersion != "local-commerce-v1" || ContentPolicy != "local-content-v2-all-open" || ResourceRevision != 144 || MasterRevision != 138 ||
            SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            SourceDateMasterSha256 != "69420175764faeaad012c92e94c06cddb51aaeebb8e6a105a28f9abb9742ef28" ||
            SourceExcerptSha256 != "ea5da9a4d52913073b016b933a729c80f27fb22271fba384fec54a628d74b406" ||
            !LocalExternalMode || LocalReceiptMarker != "daruma_dummy_receipt" || BackgroundDuplicateFreeQty != 100 ||
            Products.Length != 1396 || Products.Count(p => p.ContentsType == 7) != 315 || ExtraProducts.Length != 101 ||
            SupplementalEquips.Length != 26 || SupplementalEquips.Select(e => e.EquipId).Distinct().Count() != 26 ||
            Products.Select(p => p.ProductId).Distinct(StringComparer.Ordinal).Count() != Products.Length ||
            ItemReferences.Select(r => (r.ItemType, r.ItemId)).Distinct().Count() != ItemReferences.Length)
            throw new InvalidDataException("Unsupported commerce policy.");
        bool Reference(int type, int id) => ItemReferences.Any(r => r.ItemType == type && r.ItemId == id);
        foreach (var p in Products)
        {
            if (p.ContentsType is < 1 or > 8 or 4 || p.ContentsId <= 0 || p.ParentProductId <= 0 ||
                p.ProductGroup <= 0 || p.GroupPriority < 1 || p.ProductId.Length is < 1 or > 128 ||
                p.OsType is < 0 or > 2 || p.Price <= 0 || p.PurchaseLimit < 0 || p.ResetType is < 0 or > 3 ||
                !CommerceDates.Valid(p.OpenDate) || !CommerceDates.Valid(p.CloseDate) || p.Rewards.Length == 0 ||
                p.ConsumeItemType is not (1 or 2 or 3 or 6 or 11 or 14 or 98 or 99) ||
                (p.ConsumeItemType is 1 or 2 or 3 or 99 ? p.ConsumeItemId != 0 :
                 p.ConsumeItemType == 98 ? p.ContentsType != 7 || p.GachaGroupId <= 0 || p.ConsumeItemId != p.GachaGroupId : !Reference(p.ConsumeItemType, p.ConsumeItemId)))
                throw new InvalidDataException("Invalid commerce product.");
            foreach (var reward in p.Rewards)
                if (reward.ItemQty <= 0 || reward.ItemType is not (1 or 2 or 4 or 5 or 6 or 7 or 8 or 9 or 11 or 12 or 14) ||
                    (reward.ItemType is 1 or 2 or 4 ? reward.ItemId != 0 : !Reference(reward.ItemType, reward.ItemId)))
                    throw new InvalidDataException("Unknown commerce reward.");
        }
        foreach (var parent in Products.GroupBy(p => p.ParentProductId))
            if (parent.Select(p => (p.PurchaseLimit, p.ResetType)).Distinct().Count() != 1)
                throw new InvalidDataException("Inconsistent shared-parent limit.");
        foreach (var p in ExtraProducts)
            if (p.ContentsId <= 0 || p.ProductId <= 0 || p.ConsumeItemType != 6 || !Reference(6, p.ConsumeItemId) ||
                p.ConsumeItemQty <= 0 || p.RewardType is not (1 or 2) || p.ConditionType != 1 ||
                p.ConditionQuestIds.Length == 0 || p.ConditionQuestIds.Any(id => id <= 0) || p.PurchaseLimit is not (1 or 5) ||
                !CommerceDates.Valid(p.OpenDate) || !CommerceDates.Valid(p.CloseDate))
                throw new InvalidDataException("Invalid extra-shop product.");
        foreach (var e in SupplementalEquips)
        {
            var parts = e.FreePropertyIds.Length == 0 ? [] : e.FreePropertyIds.Split(',');
            if (e.EquipId <= 0 || e.EquipGroupId <= 0 || e.SlotCount < 0 || parts.Length > e.SlotCount ||
                e.SlotCount == 0 && parts.Length != 0 || e.SlotCount > 0 && parts.Length == 0 ||
                parts.Any(p => !int.TryParse(p, out var id) || id <= 0)) throw new InvalidDataException("Invalid supplemental equipment.");
        }
    }

    public CommerceProduct? Find(string productId) => Products.SingleOrDefault(p => p.ProductId == productId);
    public int Owner(int type, int itemId) => ItemReferences.Single(r => r.ItemType == type && r.ItemId == itemId).OwnerId;
}

internal static class CommerceDates
{
    internal static bool Valid(string value) => value.Length == 0 ||
        DateTime.TryParse(value.Replace('-', '/'), CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    internal static bool IsOpen(string open, string close, long now)
    {
        var wall = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).DateTime;
        return (open.Length == 0 || wall >= DateTime.Parse(open.Replace('-', '/'), CultureInfo.InvariantCulture)) &&
            (close.Length == 0 || wall < DateTime.Parse(close.Replace('-', '/'), CultureInfo.InvariantCulture));
    }
    // Explicit local policy; original server ResetTimestamp semantics are unavailable.
    internal static int PeriodStart(int resetType, long now)
    {
        if (resetType == 0) return 0;
        var business = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).AddHours(-5);
        var day = business.Date;
        day = resetType switch
        {
            1 => day,
            2 => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            3 => new DateTime(day.Year, day.Month, 1),
            _ => throw new InvalidDataException("Unknown commerce reset type.")
        };
        return checked((int)new DateTimeOffset(day.AddHours(5), TimeSpan.FromHours(9)).ToUnixTimeSeconds());
    }
}
