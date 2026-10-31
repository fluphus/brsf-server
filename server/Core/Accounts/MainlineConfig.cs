using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record MainlineChapter(int QuestTypeId, int PartNo, int ChapterNo, int PrologueQuestId,
    string ReleaseDate, string OpenDate, string CloseDate, int ItemGroupId);
public sealed record MainlineQuest(int QuestId, int QuestTypeId, int StageNo, int BattleId, int BeforeStoryId,
    int AfterStoryId, int Stamina, string ConditionQuestIds, int ConditionExtraShopProductId, string ReleaseDate,
    int ClearGameCoin, int ClearUserExp, int ClearCardMasteryExp, int ClearEventPoint)
{
    public int[] Prerequisites => ConditionQuestIds.Length == 0 ? [] : ConditionQuestIds.Split(',').Select(s =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 &&
        s == n.ToString(CultureInfo.InvariantCulture) ? n : throw new InvalidDataException("Invalid quest prerequisite.")).ToArray();
}
public sealed record MainlineMission(int Id, int QuestId, int MissionNo, int MissionType, int MissionValue, int ItemType, int ItemId, int ItemQty);
public sealed record MainlineDrop(int Id, int QuestId, int Rarity, int DropFrameId, int DropItemId);
public sealed record MainlineFrame(int Id, int DropFrameId, int FrameCount, int Weight);
public sealed record MainlineDropItem(int Id, int QuestTypeId, int DropItemId, int ItemType, int ItemId, int ItemQty, int Weight);
public sealed record MainlineClearRate(int ClearRate, int DeadNum, int UserExpRate, int CardMasteryExpRate, int EventPointRate);
public sealed record MainlineRank(int Rank, int Exp, int StaminaMax, int FollowMax, int ItemType, int ItemId, int ItemQty);
public sealed record MainlineMasteryLevel(int MasteryLv, int MasteryExp);

public sealed record MainlineConfig(string PolicyVersion, string ContentPolicy, int ResourceRevision, int MasterRevision,
    string SourceMasterSha256, string SourceExcerptSha256, MainlineChapter[] Chapters, MainlineQuest[] Quests,
    MainlineMission[] Missions, MainlineDrop[] Drops, MainlineFrame[] Frames, MainlineDropItem[] DropItems,
    MainlineClearRate[] ClearRates, MainlineRank[] Ranks, MainlineMasteryLevel[] MasteryLevels,
    GachaEquip[] Equips, int[] EnhanceIds)
{
    public const string ExpectedFingerprint = "2fe341d16a7388f1d002ce0fb57126c12063a7367cba5740ad208c20d6a516ef";

    public static MainlineConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 2 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid mainline configuration path.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Mainline configuration fingerprint differs.");
        var config = JsonSerializer.Deserialize<MainlineConfig>(bytes, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Missing mainline configuration.");
        config.Validate(); return config;
    }

    private void Validate()
    {
        void Need(bool value) { if (!value) throw new InvalidDataException("Inconsistent original mainline references."); }
        Need(PolicyVersion == "local-normal-mainline-v1" && ContentPolicy == "local-content-v2-all-open" && ResourceRevision == 144 && MasterRevision == 138 &&
            SourceMasterSha256 == "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" && SourceExcerptSha256.Length == 64);
        Need(Chapters.Length == 11 && Quests.Length == 136 && Missions.Length == 315 && Drops.Length == 284 && Frames.Length == 570 && DropItems.Length == 537 &&
            Ranks.Length == 100 && MasteryLevels.Length == 5 && ClearRates.Length == 6 && Equips.Length == 23 && EnhanceIds.Distinct().Count() == EnhanceIds.Length);
        Need(Chapters.Select(q => q.QuestTypeId).Distinct().Count() == Chapters.Length && Quests.Select(q => q.QuestId).Distinct().Count() == Quests.Length &&
            Missions.Select(m => (m.QuestId, m.MissionNo)).Distinct().Count() == Missions.Length && Equips.Select(e => e.EquipId).Distinct().Count() == Equips.Length);
        foreach (var chapter in Chapters) Need(chapter.QuestTypeId > 0 && chapter.ItemGroupId == 0 && Quests.Any(q => q.QuestId == chapter.PrologueQuestId));
        foreach (var quest in Quests) Need(quest.QuestId > 0 && Chapters.Any(c => c.QuestTypeId == quest.QuestTypeId) && quest.Prerequisites.All(p => Quests.Any(q => q.QuestId == p)) &&
            quest.Stamina is >= 0 and <= 999 && quest.ClearGameCoin >= 0 && quest.ClearUserExp >= 0 && quest.ClearCardMasteryExp >= 0 && quest.ClearEventPoint == 0 &&
            quest.ConditionExtraShopProductId == 0 && (quest.BattleId != 0 || quest.BeforeStoryId > 0 && quest.Stamina == 0));
        foreach (var mission in Missions) Need(Quests.Any(q => q.QuestId == mission.QuestId) && mission.MissionNo > 0 && mission.MissionType is 1 or 2 or 3 or 7 or 8 &&
            mission.MissionValue >= 0 && mission.ItemQty > 0 && (mission.ItemType == 2 && mission.ItemId == 0 || mission.ItemType == 12 && EnhanceIds.Contains(mission.ItemId)));
        foreach (var drop in Drops)
        {
            Need(Quests.Any(q => q.QuestId == drop.QuestId) && drop.Rarity > 0);
            var frames = Frames.Where(f => f.DropFrameId == drop.DropFrameId).ToArray();
            var items = DropItems.Where(i => i.DropItemId == drop.DropItemId).ToArray();
            Need(frames.Length > 0 && items.Length > 0 && frames.All(f => f.FrameCount >= 0 && f.Weight >= 0) &&
                frames.Sum(f => (long)f.Weight) is > 0 and <= int.MaxValue && items.All(i => i.ItemQty > 0 && i.Weight >= 0) &&
                items.Sum(i => (long)i.Weight) is > 0 and <= int.MaxValue);
        }
        foreach (var item in DropItems) Need(item.ItemType == 12 && EnhanceIds.Contains(item.ItemId) || item.ItemType == 7 && Equips.Any(e => e.EquipId == item.ItemId));
        Need(Ranks.Select(r => r.Rank).SequenceEqual(Enumerable.Range(1, 100)) && Ranks.Zip(Ranks.Skip(1)).All(p => p.First.Exp < p.Second.Exp) &&
            Ranks.All(r => r.StaminaMax is > 0 and <= 999 && (r.ItemType == 0 && r.ItemQty == 0 || r.ItemType == 2 && r.ItemId == 0 && r.ItemQty > 0)));
        Need(MasteryLevels.Select(m => m.MasteryLv).SequenceEqual(Enumerable.Range(1, 5)) && MasteryLevels.All(m => m.MasteryExp > 0));
        Need(ClearRates.Select(r => r.ClearRate).Order().SequenceEqual(Enumerable.Range(1, 6)) && ClearRates.All(r => r.UserExpRate >= 0 && r.CardMasteryExpRate >= 0));
    }

    public MainlineQuest? Find(int id) => Quests.SingleOrDefault(q => q.QuestId == id);

    internal bool IsOpen(MainlineQuest quest, long now)
    {
        var chapter = Chapters.Single(c => c.QuestTypeId == quest.QuestTypeId);
        var date = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).DateTime;
        bool After(string text) => text.Length == 0 || date >= AllGachaPool.ParseDate(text);
        return After(quest.ReleaseDate) && After(chapter.ReleaseDate) && After(chapter.OpenDate) &&
            (chapter.CloseDate.Length == 0 || date <= AllGachaPool.ParseDate(chapter.CloseDate));
    }
}
