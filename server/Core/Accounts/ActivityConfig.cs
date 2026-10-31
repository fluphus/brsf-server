using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record ActivityEvent(int EventId, int EventType, string OpenDate, string CloseDate, string ViewCloseDate);
public sealed record ActivityMission(int MissionType, int MissionId, int EventId, int ContentType, string ContentValue,
    int ClearScore, int[] ConditionMissionIds, int TakeoverMissionId, int ItemGroupId, int ScopeEventId, string OpenDate, string CloseDate)
{
    // Only original aggregate410/EP503 interpret PlaceConditionValue as an event scope.
    public int ResolvedScopeEventId => ContentType is 410 or 503 && ScopeEventId > 0 ? ScopeEventId : EventId;
}
public sealed record ActivityReward(int MissionType, int ItemGroupId, int ItemType, int ItemId, int ItemQty);
public sealed record ActivityProduct(string ProductId, int ContentsType, int ContentsId, CommerceReward[] Rewards);
public sealed record ActivityStory(int StoryId);
public sealed record ActivityQuestStory(int QuestId, int[] StoryIds);
public sealed record ActivityConfig(string PolicyVersion, string ContentPolicy, int ResourceRevision, int MasterRevision,
    string SourceMasterSha256, string SourceDateMasterSha256, string GachaConfigSha256, string CommerceConfigSha256,
    ActivityEvent[] Events, int[] ActiveDailyIds, int[] ActiveWeeklyIds, ActivityMission[] Missions, ActivityReward[] Rewards, Dictionary<int, int> ItemCategories,
    Dictionary<int, int> EquipGroups, GachaEquip[] SupplementalEquips, ActivityProduct[] Products, ActivityStory[] Stories, int[] TitleIds, ActivityQuestStory[] QuestStories,
    int[] UnsupportedRewardTypes)
{
    public const string ExpectedFingerprint = "56eaf1a587ea7968a97cbd9a47958fc8ac0be19ac3fe4e1552ba7abde26edd97";
    public static ActivityConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 2 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid activity configuration.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint) throw new InvalidDataException("Activity fingerprint mismatch.");
        var config = JsonSerializer.Deserialize<ActivityConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing activity configuration.");
        if (config.PolicyVersion != "local-activity-v2" || config.ContentPolicy != "local-content-v2-all-open" ||
            config.ResourceRevision != 144 || config.MasterRevision != 138 || config.Missions.Length != 1357 ||
            config.Rewards.Length != 2159 || config.Events.Length != 30 || config.ActiveDailyIds.Length != 8 || config.ActiveWeeklyIds.Length != 15 ||
            config.Missions.Select(x => (x.MissionType, x.MissionId)).Distinct().Count() != config.Missions.Length ||
            config.Missions.Any(m => m.ClearScore <= 0 || m.MissionId <= 0 || m.MissionType is not (1 or 2 or 3 or 4 or 7)))
            throw new InvalidDataException("Unsupported activity policy.");
        return config;
    }
}

public static class ActivityDates
{
    public static bool IsOpen(string open, string close, long now)
    {
        var wall = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).DateTime;
        bool None(string s) => s.Length == 0 || s.StartsWith("0000", StringComparison.Ordinal);
        return (None(open) || wall >= DateTime.Parse(open.Replace('-', '/'), CultureInfo.InvariantCulture)) &&
            (None(close) || wall < DateTime.Parse(close.Replace('-', '/'), CultureInfo.InvariantCulture));
    }
    public static long Start(int type, long now)
    {
        if (type is not (2 or 3)) return 0;
        var day = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).AddHours(-5).Date;
        if (type == 3) day = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        return new DateTimeOffset(day.AddHours(5), TimeSpan.FromHours(9)).ToUnixTimeSeconds();
    }
    public static string Period(int type, long now) => Start(type, now).ToString(CultureInfo.InvariantCulture);
}
