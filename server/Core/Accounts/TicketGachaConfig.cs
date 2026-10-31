using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record TicketGachaPool(int GachaId, int LotId, int TicketItemId, int DailyMaxCount, int TotalMaxCount,
    string OpenDate, string CloseDate, GachaGroup[] Groups)
{
    // Original paired-zero dates explicitly bypass the client's date filter.
    public bool IsOpen => OpenDate == "0000-00-00 00:00:00" && CloseDate == "0000-00-00 00:00:00";
    public GachaContent Draw(Func<int, int>? nextBelow = null)
    {
        nextBelow ??= RandomNumberGenerator.GetInt32;
        var group = GachaWeights.Pick(Groups, g => g.Weight, nextBelow);
        return GachaWeights.Pick(group.Contents, c => c.Weight, nextBelow);
    }
}

public sealed record TicketGachaConfig(string PolicyVersion, int MasterRevision, string SourceMasterSha256,
    string SourceTicketExcerptSha256, string SourceInventorySnapshotSha256, int DuplicateBoosterQty, int BoosterCap,
    int ConsumeCap, TicketGachaPool[] Pools, GachaCard[] Cards, GachaEquip[] Equips, int[] FreePropertyIds)
{
    public const string ExpectedFingerprint = "dc974a9352f17d729f34a523e9df88c7d69592357c1c1f644b8fd790d3e62bfa";
    public static TicketGachaConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 32768 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid guaranteed ticket configuration file.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Guaranteed ticket snapshot fingerprint mismatch.");
        var value = JsonSerializer.Deserialize<TicketGachaConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing guaranteed ticket snapshot.");
        if (value.PolicyVersion != "local-guaranteed-ticket-gacha-v1" || value.MasterRevision != 136 ||
            value.SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            value.SourceTicketExcerptSha256 != "cd8e9a50d7431366b2759d6b25f67329214be3abcf03ba57c850d01c944ebfcf" ||
            value.SourceInventorySnapshotSha256 != StandardGachaConfig.ExpectedFingerprint || value.DuplicateBoosterQty != 1 ||
            value.BoosterCap != 99999999 || value.ConsumeCap != 99999999 || value.Cards.Length != 18 || value.Equips.Length != 15 ||
            !value.Pools.Select(p => p.GachaId).SequenceEqual(new[] { 55003, 55004 }))
            throw new InvalidDataException("Unsupported guaranteed ticket policy.");
        var cards = value.Cards.Select(c => c.CardId).ToHashSet(); var equips = value.Equips.Select(e => e.EquipId).ToHashSet();
        if (cards.Count != 18 || equips.Count != 15 || value.Cards.Any(c => c.CardId <= 0 || c.CharaId <= 0 || c.DefaultSkinId <= 0))
            throw new InvalidDataException("Invalid guaranteed inventory reference.");
        foreach (var pool in value.Pools)
        {
            var isCard = pool.GachaId == 55003;
            if (!pool.IsOpen || pool.LotId != pool.GachaId * 10 + 1 || pool.TicketItemId != (isCard ? 80001 : 80002) ||
                pool.DailyMaxCount != 0 || pool.TotalMaxCount != (isCard ? 1 : 2) || pool.Groups.Length != 1)
                throw new InvalidDataException("Invalid original guaranteed ticket lot.");
            var group = pool.Groups[0];
            if (group.Id != pool.LotId || group.Weight != 10000 || group.GuaranteeWeight != 0 || group.Contents.Length != (isCard ? 18 : 15) ||
                group.Contents.Any(c => c.ItemType != (isCard ? 5 : 7) || c.ItemQty != 1 || c.Weight != 1 ||
                    !(isCard ? cards.Contains(c.ItemId) : equips.Contains(c.ItemId))) ||
                group.Contents.Select(c => c.ItemId).Distinct().Count() != group.Contents.Length)
                throw new InvalidDataException("Invalid guaranteed ticket weighted members.");
        }
        foreach (var equip in value.Equips)
        {
            var properties = equip.FreePropertyIds.Split(',');
            if (equip.EquipGroupId <= 0 || properties.Length > equip.SlotCount ||
                properties.Any(x => !int.TryParse(x, out var id) || !value.FreePropertyIds.Contains(id)))
                throw new InvalidDataException("Invalid guaranteed equipment property reference.");
        }
        return value;
    }
}
