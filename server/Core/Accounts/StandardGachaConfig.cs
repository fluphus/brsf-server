using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record StandardGachaLot(int LotId, int LotType, int CostType, int CostItemId, int CostQty, int DrawCount);
public sealed record StandardGachaPool(int GachaId, string OpenDate, string CloseDate,
    StandardGachaLot[] Lots, GachaGroup[] Groups)
{
    public bool IsOpen(long now) => LocalPolicyDates.IsOpen(OpenDate, CloseDate, now);

    // Local reconstruction policy, not the historical server RNG: a multi lot
    // uses nine ordinary groups followed by one guarantee group, then chooses
    // a member against that group's actual integer sum.
    public GachaContent[] Draw(StandardGachaLot lot, Func<int, int>? nextBelow = null)
    {
        nextBelow ??= RandomNumberGenerator.GetInt32;
        var result = new GachaContent[lot.DrawCount];
        for (var i = 0; i < result.Length; i++)
        {
            var guarantee = lot.DrawCount == 10 && i == 9;
            var group = GachaWeights.Pick(Groups, g => guarantee ? g.GuaranteeWeight : g.Weight, nextBelow);
            result[i] = GachaWeights.Pick(group.Contents, c => c.Weight, nextBelow);
        }
        return result;
    }
}

public sealed record StandardGachaConfig(string PolicyVersion, int MasterRevision, string SourceMasterSha256,
    string SourceNativeSha256, string SourceExcerptSha256, int DuplicateBoosterQty, int BoosterCap,
    StandardGachaPool[] Pools, GachaCard[] Cards, GachaEquip[] Equips, int[] FreePropertyIds)
{
    public const string ExpectedFingerprint = "b347ef93f37c5b40372ee9c7d91b87debb03cdba04376aada3ae0d849f3cc635";
    public static StandardGachaConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 131072 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid standard gacha configuration file.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Standard gacha rule snapshot fingerprint mismatch.");
        var value = JsonSerializer.Deserialize<StandardGachaConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing standard gacha snapshot.");
        value.Validate();
        return value;
    }

    private void Validate()
    {
        if (PolicyVersion != "local-standard-gacha-v1" || MasterRevision != 136 ||
            SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            SourceNativeSha256 != VerifiedClient.NativeSha256 ||
            SourceExcerptSha256 != "4f5b96810bceca9568671fb35cbaa22f0cd6b7053d0e88e63cb4cc5394919b88" ||
            DuplicateBoosterQty != 1 || BoosterCap != 99999999 ||
            !Pools.Select(p => p.GachaId).SequenceEqual(new[] { 11012, 12012 }) || Cards.Length != 42 || Equips.Length != 125)
            throw new InvalidDataException("Unsupported standard gacha policy.");
        var cards = Cards.Select(c => c.CardId).ToHashSet();
        var equips = Equips.Select(e => e.EquipId).ToHashSet();
        if (cards.Count != 42 || equips.Count != 125 || Cards.Any(c => c.CardId <= 0 || c.CharaId <= 0 || c.DefaultSkinId <= 0) ||
            Cards.Select(c => c.DefaultSkinId).Distinct().Count() != 42 || FreePropertyIds.Distinct().Count() != FreePropertyIds.Length)
            throw new InvalidDataException("Duplicate or invalid standard inventory reference.");
        foreach (var pool in Pools)
        {
            if (pool.OpenDate != "2023/01/01 00:00:00" || pool.CloseDate != "2099/12/31 23:59:59" ||
                pool.Groups.Length != (pool.GachaId == 11012 ? 5 : 3) ||
                pool.Groups.Sum(g => g.Weight) != 10000 || pool.Groups.Sum(g => g.GuaranteeWeight) != 10000 ||
                pool.Groups.Select(g => g.Id).Distinct().Count() != pool.Groups.Length ||
                !pool.Lots.Select(l => l.LotId).SequenceEqual(new[] { pool.GachaId * 10 + 1, pool.GachaId * 10 + 2 }))
                throw new InvalidDataException("Invalid standard gacha pool.");
            foreach (var lot in pool.Lots)
                if (lot.LotType is not (1 or 2) || lot.CostType != 2 || lot.CostItemId != 0 ||
                    lot.CostQty != (lot.LotType == 1 ? 150 : 1500) || lot.DrawCount != (lot.LotType == 1 ? 1 : 10))
                    throw new InvalidDataException("Invalid standard gacha lot.");
            foreach (var group in pool.Groups)
                if (group.Weight < 0 || group.GuaranteeWeight < 0 || group.Contents.Length == 0 ||
                    group.Contents.Any(c => c.Weight <= 0 || c.ItemQty != 1 ||
                        !(c.ItemType == 5 && cards.Contains(c.ItemId) || c.ItemType == 7 && equips.Contains(c.ItemId))))
                    throw new InvalidDataException("Invalid standard gacha content.");
        }
        foreach (var equip in Equips)
        {
            var properties = equip.FreePropertyIds.Split(',');
            if (equip.EquipGroupId <= 0 || properties.Length > equip.SlotCount ||
                properties.Any(x => !int.TryParse(x, out var id) || !FreePropertyIds.Contains(id)))
                throw new InvalidDataException("Invalid standard equipment property reference.");
        }
    }
}
