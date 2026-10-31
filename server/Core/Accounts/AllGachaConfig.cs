using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record AllGachaLot(int LotId, int LotType, int CostType, int CostItemId, int CostQty,
    int BeforeCostQty, int DrawCount, int DailyMaxCount, int TotalMaxCount);
public sealed record CommonGachaTicket(int GachaType, int LotType, int ItemId);
public sealed record AllGachaPool(int GachaId, int GachaType, int GachaGroupId, string OpenDate,
    string CloseDate, AllGachaLot[] Lots, GachaGroup[] Groups)
{
    internal static DateTime ParseDate(string value) => DateTime.ParseExact(value.Replace('-', '/'),
        ["yyyy/MM/dd HH:mm:ss", "yyyy/M/d HH:mm:ss", "yyyy/MM/d HH:mm:ss", "yyyy/M/dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None);
    public bool IsOpen(long now)
    {
        if (OpenDate == "0000-00-00 00:00:00" && CloseDate == OpenDate) return true;
        var local = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).DateTime;
        return local >= ParseDate(OpenDate) && local <= ParseDate(CloseDate);
    }

    // Local reconstruction: a positive guarantee distribution gives the last
    // result of a ten-lot its guarantee. Zero guarantee means all normal draws.
    public GachaContent[] Draw(AllGachaLot lot, Func<int, int>? nextBelow = null)
    {
        nextBelow ??= RandomNumberGenerator.GetInt32;
        return Enumerable.Range(0, lot.DrawCount).Select(index =>
        {
            var guarantee = lot.DrawCount == 10 && index == 9 && Groups.Any(g => g.GuaranteeWeight > 0);
            var group = GachaWeights.Pick(Groups, g => guarantee ? g.GuaranteeWeight : g.Weight, nextBelow);
            return GachaWeights.Pick(group.Contents, c => c.Weight, nextBelow);
        }).ToArray();
    }
}

public sealed record AllGachaConfig(string PolicyVersion, string ContentPolicy, int ResourceRevision, int MasterRevision,
    int SourceMasterRevision, string SourceMasterSha256, string SourceDateMasterSha256, string SourceNativeSha256,
    int DuplicateBoosterQty, int BoosterCap, int ConsumeCap, int PointCap, int DailyResetHour,
    AllGachaPool[] Pools, GachaCard[] Cards, GachaEquip[] Equips, int[] FreePropertyIds,
    int[] ConsumeItemIds, CommonGachaTicket[] CommonTickets)
{
    public const string ExpectedFingerprint = "8329112992d51f5f9aa5f4655868eb914b70f2ddf2c49745df206f0490611a7f";
    public static AllGachaConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 4 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid full gacha configuration file.");
        var bytes = File.ReadAllBytes(path);
        VerifiedClient.RequireHash(bytes, ExpectedFingerprint);
        var config = JsonSerializer.Deserialize<AllGachaConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing full gacha configuration.");
        config.Validate(); return config;
    }

    private void Validate()
    {
        if (PolicyVersion != "local-all-gacha-v1" || ContentPolicy != "local-content-v2-all-open" ||
            ResourceRevision != 144 || MasterRevision != 138 || SourceMasterRevision != 136 ||
            SourceMasterSha256 != Brs.Local.Content.LocalContentOverlay.SourceMasterSha256 ||
            SourceDateMasterSha256 != "12e337b5737ce588583294e4af6b6a4fb778a5051c144a4be088f0e149ea1f9b" ||
            SourceNativeSha256 != VerifiedClient.NativeSha256 || DuplicateBoosterQty != 1 || BoosterCap != 99999999 ||
            ConsumeCap != 99999999 || PointCap != int.MaxValue || DailyResetHour != 5 ||
            Pools.Length != 216 || Pools.Sum(p => p.Lots.Length) != 549 || Cards.Length != 71 || Equips.Length != 162)
            throw new InvalidDataException("Unsupported full gacha policy.");
        var cards = Cards.Select(c => c.CardId).ToHashSet(); var equips = Equips.Select(e => e.EquipId).ToHashSet();
        if (cards.Count != Cards.Length || equips.Count != Equips.Length || Pools.Select(p => p.GachaId).Distinct().Count() != Pools.Length ||
            Pools.SelectMany(p => p.Lots).Select(l => l.LotId).Distinct().Count() != 549 ||
            Cards.Any(c => c.CardId <= 0 || c.CharaId <= 0 || c.DefaultSkinId <= 0) ||
            ConsumeItemIds.Length != 67 || ConsumeItemIds.Distinct().Count() != 67 || ConsumeItemIds.Any(i => i <= 0) ||
            FreePropertyIds.Distinct().Count() != FreePropertyIds.Length ||
            !CommonTickets.SequenceEqual(new[] { new CommonGachaTicket(1, 1, 10001), new(1, 2, 20001), new(2, 1, 60001), new(2, 2, 70001) }))
            throw new InvalidDataException("Invalid full gacha references.");
        foreach (var pool in Pools)
        {
            var permanent = pool.OpenDate == "0000-00-00 00:00:00" && pool.CloseDate == pool.OpenDate;
            if (pool.GachaId <= 0 || pool.GachaId == 9999 || pool.GachaType is not (1 or 2 or 5) || pool.GachaGroupId < 0 ||
                !permanent && (AllGachaPool.ParseDate(pool.OpenDate) > AllGachaPool.ParseDate(pool.CloseDate) || pool.CloseDate != "2099/12/31 23:59:59") ||
                pool.Groups.Length == 0 || pool.Groups.Select(g => g.Id).Distinct().Count() != pool.Groups.Length ||
                pool.Groups.Sum(g => (long)g.Weight) is <= 0 or > int.MaxValue ||
                pool.Groups.Sum(g => (long)g.GuaranteeWeight) is < 0 or > int.MaxValue)
                throw new InvalidDataException($"Invalid full gacha pool {pool.GachaId}.");
            foreach (var lot in pool.Lots)
                if (lot.LotId <= 0 || lot.LotType is < 1 or > 3 || lot.CostType is not (1 or 2 or 6) || lot.CostQty < 0 ||
                    lot.BeforeCostQty < 0 || lot.DrawCount is not (1 or 10) || lot.DailyMaxCount < 0 || lot.TotalMaxCount < 0 ||
                    (lot.CostType == 6 ? !ConsumeItemIds.Contains(lot.CostItemId) : lot.CostItemId != 0))
                    throw new InvalidDataException("Invalid full gacha lot.");
            foreach (var group in pool.Groups)
                if (group.Id <= 0 || group.Weight < 0 || group.GuaranteeWeight < 0 || group.Contents.Length == 0 ||
                    group.Contents.Sum(c => (long)c.Weight) is <= 0 or > int.MaxValue ||
                    group.Contents.Any(c => c.Weight <= 0 || c.ItemQty != 1 ||
                        !(c.ItemType == 5 && cards.Contains(c.ItemId) || c.ItemType == 7 && equips.Contains(c.ItemId))))
                    throw new InvalidDataException("Invalid full gacha distribution.");
        }
        foreach (var equip in Equips)
            if (equip.EquipGroupId <= 0 || equip.SlotCount < 0 ||
                (equip.SlotCount == 0 ? equip.FreePropertyIds != "" : equip.FreePropertyIds.Length == 0 ||
                 equip.FreePropertyIds.Split(',').Length > equip.SlotCount ||
                 equip.FreePropertyIds.Split(',').Any(p => !int.TryParse(p, out var id) || !FreePropertyIds.Contains(id))))
                throw new InvalidDataException("Invalid full equipment properties.");
    }
}
