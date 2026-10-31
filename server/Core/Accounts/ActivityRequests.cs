using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record ActivityTopRequest(string? AuthToken, int EventId)
{
    public static ActivityTopRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "event_id"); var id = VipRequestFields.Int(root, "event_id");
        if (id <= 0) throw new InvalidDataException("Invalid event identity.");
        return new(EconomyRequestFields.Auth(common), id);
    }
}
public sealed record ActivityListRequest(string? AuthToken)
{
    public static ActivityListRequest Read(JsonElement root) => new(EconomyRequestFields.Auth(EconomyRequestFields.Read(root)));
}
public sealed record ActivityReceiveRequest(string? AuthToken, string RetryToken, ActivityClaim[] Selection)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

    public static ActivityReceiveRequest Read(JsonElement root)
    {
        var names = new[] { "receive_mission_normal_ids", "receive_mission_daily_ids", "receive_mission_weekly_ids", "receive_mission_event_ids", "receive_mission_event_daily_ids" };
        var types = new[] { 1, 2, 3, 4, 7 };
        var common = EconomyRequestFields.Read(root, names); var result = new List<ActivityClaim>();
        for (var i = 0; i < names.Length; i++)
        {
            var ids = VipRequestFields.IntArray(root, names[i], 1357);
            if (ids.Any(id => id <= 0)) throw new InvalidDataException("Invalid mission identity.");
            result.AddRange(ids.Select(id => new ActivityClaim(types[i], id)));
        }
        if (result.Count is < 1 or > 1357 || result.Distinct().Count() != result.Count) throw new InvalidDataException("Empty or duplicate mission selection.");
        return new(EconomyRequestFields.Auth(common), EconomyRequestFields.Retry(common), result.ToArray()) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
    }
}
public sealed record ActivityQuestStoryRequest(string? AuthToken, int QuestId, int StoryId)
{
    public static ActivityQuestStoryRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "quest_id", "story_id");
        var quest = VipRequestFields.Int(root, "quest_id"); var story = VipRequestFields.Int(root, "story_id");
        if (quest <= 0 || story <= 0) throw new InvalidDataException("Invalid quest/story relation.");
        return new(EconomyRequestFields.Auth(common), quest, story);
    }
}
