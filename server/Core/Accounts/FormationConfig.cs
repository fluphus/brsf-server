using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record FormationCard(int CardId, int CharaId, int Rarity, int EquipType, bool IsDeleted);
public sealed record FormationEquip(int EquipId, int EquipGroupId, int EquipType, bool IsDeleted);
public sealed record FormationAwakening(int Rarity, int AwakeningCount, bool Sub2Unlocked, bool IsDeleted);
public sealed record FormationConfig(string PolicyVersion, string SourceMasterSha256, string SourceRulesSha256,
    string SourceBackgroundSha256, int MaxDeckNum, int MaxSlots, int MaxEquips, int SupportSlotNo,
    int DefaultBackgroundId, string DefaultDeckNameFormat, int DeckNameMaxUtf16, string ResetPolicy,
    string TotalPowerPolicy, string HomePolicy, string ProfileCountPolicy,
    FormationCard[] Cards, FormationEquip[] Equips, FormationAwakening[] Awakening)
{
    public const string ExpectedFingerprint = "9b30df55c3a05bed9040ca066ce8d5e7f428bd7ac65d5535147973e24d5181c4";
    public static FormationConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 131072 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid formation configuration.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Formation snapshot fingerprint mismatch.");
        var value = JsonSerializer.Deserialize<FormationConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing formation snapshot.");
        if (value.PolicyVersion != "local-formation-home-v1" ||
            value.SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            value.SourceRulesSha256 != "d521f996dbb2b66c733798a94e21a7cfed4ddc5599bc456686333038a39aa86c" ||
            value.SourceBackgroundSha256 != "5ca01df4c088b05c662c95c06c77fc689cd505a55444c4fa218a8f3110532af5" ||
            value.MaxDeckNum != 10 || value.MaxSlots != 6 || value.MaxEquips != 3 || value.SupportSlotNo != 6 ||
            value.DefaultBackgroundId != 100101 || value.DefaultDeckNameFormat != "編成{0}" || value.DeckNameMaxUtf16 != 64 ||
            value.ResetPolicy != "preserve-name-clear-nonmain" || value.TotalPowerPolicy != "nonnegative-audit-only" ||
            value.HomePolicy != "owned1to6-unique-card-and-chara-ordered" || value.ProfileCountPolicy != "current-holdings-not-historical-acquisitions" ||
            value.Cards.Length != 76 || value.Equips.Length != 314 || value.Awakening.Length != 18 ||
            value.Cards.Select(card => card.CardId).Distinct().Count() != 76 || value.Equips.Select(equip => equip.EquipId).Distinct().Count() != 314 ||
            value.Awakening.Select(rule => (rule.Rarity, rule.AwakeningCount)).Distinct().Count() != 18 ||
            value.Cards.Any(card => card.CardId <= 0 || card.CharaId <= 0 || card.Rarity is < 3 or > 5 || card.EquipType <= 0) ||
            value.Equips.Any(equip => equip.EquipId <= 0 || equip.EquipGroupId <= 0 || equip.EquipType is not (101 or 102 or 111 or 112 or 113 or 201 or 301 or 302)) ||
            value.Awakening.Any(rule => rule.Rarity is < 3 or > 5 || rule.AwakeningCount < 0))
            throw new InvalidDataException("Unsupported formation snapshot.");
        return value;
    }
}
