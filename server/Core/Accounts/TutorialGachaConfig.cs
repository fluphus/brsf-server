using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record GachaContent(int ItemType, int ItemId, int ItemQty, int Weight);
public sealed record GachaGroup(int Id, int Weight, int GuaranteeWeight, GachaContent[] Contents);
public sealed record GachaCard(int CardId, int CharaId, int DefaultSkinId);
public sealed record GachaEquip(int EquipId, int EquipGroupId, string FreePropertyIds, int SlotCount);

// Exact small original-Master extraction. Hash-pinned before any DB is opened.
public sealed record TutorialGachaConfig(string PolicyVersion, int MasterRevision, string SourceMasterSha256,
    string SourceNativeSha256, int GachaId, int LotId, int NormalCount, int GuaranteeCount,
    int DuplicateBoosterQty, int BoosterCap, GachaGroup[] Groups, GachaCard[] Cards,
    GachaEquip[] Equips, int[] FreePropertyIds)
{
    public const string ExpectedFingerprint = "bf9e67467f5baa1944c4b0fa0b31d0968f277ca676f21815b7fd436da96859c4";
    public static TutorialGachaConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 65536 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid tutorial gacha configuration file.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Tutorial gacha rule snapshot fingerprint mismatch.");
        var value = JsonSerializer.Deserialize<TutorialGachaConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing tutorial gacha snapshot.");
        if (value.PolicyVersion != "local-tutorial-gacha-v1" || value.MasterRevision != 136 ||
            value.GachaId != 9999 || value.LotId != 99991 || value.NormalCount != 9 || value.GuaranteeCount != 1 ||
            value.DuplicateBoosterQty != 1 || value.BoosterCap != 99999999 || value.Cards.Length != 18 || value.Equips.Length != 84 ||
            value.Groups.Sum(g => g.Weight) != 10000 || value.Groups.Sum(g => g.GuaranteeWeight) != 10000)
            throw new InvalidDataException("Unsupported tutorial gacha policy.");
        var cards = value.Cards.Select(c => c.CardId).ToHashSet();
        var equips = value.Equips.Select(e => e.EquipId).ToHashSet();
        if (cards.Count != 18 || equips.Count != 84 || value.Groups.Select(g => g.Id).Distinct().Count() != 4)
            throw new InvalidDataException("Duplicate gacha references.");
        foreach (var g in value.Groups)
            if (g.Weight < 0 || g.GuaranteeWeight < 0 || g.Contents.Length == 0 ||
                g.Contents.Any(c => c.Weight <= 0 || c.ItemQty != 1 || !(c.ItemType == 5 && cards.Contains(c.ItemId) || c.ItemType == 7 && equips.Contains(c.ItemId))))
                throw new InvalidDataException("Invalid gacha content reference.");
        foreach (var e in value.Equips)
        {
            var parts = e.FreePropertyIds.Split(',');
            if (e.EquipGroupId <= 0 || parts.Length > e.SlotCount ||
                parts.Any(x => !int.TryParse(x, out var id) || !value.FreePropertyIds.Contains(id)))
                throw new InvalidDataException("Invalid equipment property reference.");
        }
        return value;
    }

    // Explicit reconstruction policy:9 ordinary draws +1 guarantee; integer RNG.
    public GachaContent[] Draw(Func<int, int>? nextBelow = null)
    {
        nextBelow ??= RandomNumberGenerator.GetInt32;
        var result = new GachaContent[10];
        for (var i = 0; i < result.Length; i++)
        {
            var group = GachaWeights.Pick(Groups, g => i < NormalCount ? g.Weight : g.GuaranteeWeight, nextBelow);
            result[i] = GachaWeights.Pick(group.Contents, c => c.Weight, nextBelow);
        }
        return result;
    }

}

public sealed record TutorialGachaRequest(string RetryToken, string? AuthToken)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

    public const string Route = "tutorial/gacha_result";
    public const string Semantic = "gacha:9999:99991:1:false";
    public static TutorialGachaRequest Read(JsonElement root, JsonElement common)
    {
        var allowed = new HashSet<string>(["common", "gacha_id", "lot_id", "execute_count", "is_use_gacha_ticket"], StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
            if (!allowed.Remove(p.Name)) throw new InvalidDataException("Duplicate or unknown gacha request field.");
        if (allowed.Count != 0) throw new InvalidDataException("Missing gacha request field.");
        foreach (var (name, expected) in new[] { ("gacha_id", 9999), ("lot_id", 99991), ("execute_count", 1) })
            if (root.GetProperty(name).ValueKind != JsonValueKind.Number || !root.GetProperty(name).TryGetInt32(out var number) || number != expected)
                throw new InvalidDataException("Unsupported tutorial draw.");
        if (root.GetProperty("is_use_gacha_ticket").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Tutorial draw cannot use tickets.");
        var retry = common.EnumerateObject().Where(p => p.NameEquals("retry_token")).ToArray();
        if (retry.Length != 1 || retry[0].Value.ValueKind != JsonValueKind.String ||
            retry[0].Value.GetString() is not { } token || string.IsNullOrWhiteSpace(token) || token.Length > 256)
            throw new InvalidDataException("Exactly one bounded gacha retry token required.");
        var auth = common.TryGetProperty("auth_token", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (auth?.Length > 256) throw new InvalidDataException("Invalid auth token size.");
        return new(token, auth) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
    }
}
