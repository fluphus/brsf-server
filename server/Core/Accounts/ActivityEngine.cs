using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Brs.Local.Accounts.ActivitySchema;

namespace Brs.Local.Accounts;

public sealed record ActivityMissionState(ActivityMission Definition, int Score, bool IsReleased, bool IsReceived)
{
    public bool CanReceive => IsReleased && !IsReceived && Score >= Definition.ClearScore;
    public object Wire => new { mission_type = Definition.MissionType, mission_id = Definition.MissionId, score = Score, is_released = IsReleased, is_received = IsReceived };
    public object EventWire => new { event_id = Definition.EventId, mission_id = Definition.MissionId, score = Score, is_released = IsReleased ? 1 : 0, is_received = IsReceived ? 1 : 0 };
}
public sealed record ActivityClaim(int MissionType, int MissionId);
public sealed record ActivityGrantResult(string Player, object[] Rewards);
public sealed record ActivityReceiveResult(int Status, string Player, JsonElement? FrozenResult = null);
// The host supplies the proven inventory transaction. This is a real mission reward call, never a fabricated shop product.
public delegate ActivityGrantResult ActivityGrant(SqliteConnection c, SqliteTransaction t, long account, string player, long now, CommerceReward[] rewards);

public sealed class ActivityEngine(ActivityConfig config, MainlineConfig? mainline = null, EventQuestConfig? events = null, QuestModesConfig? modes = null)
{
    public const string ReceiveRoute = "mission/receive";
    public ActivityMissionState[] ReadMissions(SqliteConnection c, SqliteTransaction t, long account, string player, long now)
        =>ReadMissions(c,t,account,player,now,null);
    internal ActivityMissionState[] ReadMissions(SqliteConnection c,SqliteTransaction t,long account,string player,long now,ActivityReadSnapshot? shared)
    {
        var snapshot = shared??ActivityReadSnapshot.Load(config,c,t,account,player,now,mainline,events,modes);
        var states = new List<ActivityMissionState>();
        var scores = new Dictionary<(int Type, string Value, long Period, int ScopeEventId), long>();
        foreach (var m in config.Missions)
        {
            var released = ActivityDates.IsOpen(m.OpenDate, m.CloseDate, now) &&
                (m.EventId == 0 || config.Events.Any(e => e.EventId == m.EventId && ActivityDates.IsOpen(e.OpenDate, e.CloseDate, now)));
            if (m.MissionType == 2) released &= config.ActiveDailyIds.Contains(m.MissionId);
            if (m.MissionType == 3) released &= config.ActiveWeeklyIds.Contains(m.MissionId);
            foreach (var condition in m.ConditionMissionIds)
            {
                var previous = config.Missions.SingleOrDefault(p => p.MissionType == m.MissionType && p.MissionId == condition);
                released &= previous is not null && snapshot.Claims.Contains((previous.MissionType,previous.MissionId,ActivityDates.Period(previous.MissionType,now)));
            }
            var scoreKey = (m.ContentType, m.ContentValue, ActivityDates.Start(m.MissionType, now), m.ResolvedScopeEventId);
            if (!scores.TryGetValue(scoreKey, out var score)) scores[scoreKey] = score = snapshot.Score(config,m,now);
            states.Add(new(m, checked((int)Math.Min(int.MaxValue, score)), released, snapshot.Claims.Contains((m.MissionType,m.MissionId,ActivityDates.Period(m.MissionType,now)))));
        }
        return ResolveClearCounts(states.ToArray());
    }
    public static ActivityMissionState[] ResolveClearCounts(ActivityMissionState[] initial)
    {
        // Least fixed point: begin with NO410 completion, then let actual external clears unlock thresholds.
        var states = initial.Select(s => s.Definition.ContentType == 410 ? s with { Score = 0 } : s).ToArray();
        for (var round = 0; round <= states.Length; round++)
        {
            var next = states.Select(s => s.Definition.ContentType != 410 ? s : s with { Score = states.Count(other =>
                other.Definition.MissionType == s.Definition.MissionType && other.Definition.MissionId != s.Definition.MissionId &&
                (s.Definition.MissionType is not (4 or 7) || other.Definition.EventId == s.Definition.ResolvedScopeEventId) &&
                other.IsReleased && other.Score >= other.Definition.ClearScore) }).ToArray();
            if (next.Select(s => s.Score).SequenceEqual(states.Select(s => s.Score))) return next;
            states = next;
        }
        throw new InvalidDataException("Mission clear-count fixed point did not converge.");
    }
    public Dictionary<string, object?> ReadEventTop(SqliteConnection c, SqliteTransaction t, long account, string player, long now, int eventId, int eventType, ActivityMissionState[]? states = null)
    {
        if (eventType is not (1 or 2)) throw new ArgumentException("Invalid event route type.");
        var e = config.Events.SingleOrDefault(e => e.EventId == eventId && e.EventType == eventType);
        if (e is null || !ActivityDates.IsOpen(e.OpenDate, e.CloseDate, now) || !ActivityDates.IsOpen(e.OpenDate, e.ViewCloseDate, now))
            throw new InvalidDataException("Unknown or inaccessible event.");
        return new() { ["has_clear_event_mission"] = (states ?? ReadMissions(c, t, account, player, now)).Any(s => s.Definition.EventId == eventId && s.CanReceive) };
    }
    public bool PrepareQuestStory(int questId, int storyId, Func<int, bool> actualQuestAccess)
        => config.Stories.Any(s => s.StoryId == storyId) && config.QuestStories.Any(q => q.QuestId == questId && q.StoryIds.Contains(storyId)) && actualQuestAccess(questId);
    // Deliberately no story/read mutation route until its active client completion point is proven.
    public object[] TitleRows(SqliteConnection c, SqliteTransaction t, long account)
    {
        using var q = Command(c, t, "SELECT id,title_id FROM owned_titles WHERE account_id=$id ORDER BY id", ("$id", account));
        using var r = q.ExecuteReader(); var rows = new List<object>();
        while (r.Read()) rows.Add(new { id = r.GetInt32(0), title_id = r.GetInt32(1) }); return rows.ToArray();
    }
    public bool ObserveDailyClear(SqliteConnection c, SqliteTransaction t, long account, string player, long now, ActivityMissionState[]? states = null)
    {
        var daily = (states ?? ReadMissions(c, t, account, player, now)).Where(s => s.Definition.MissionType == 2 && s.Definition.ContentType != 410 && s.IsReleased).ToArray();
        if (daily.Length == 0 || daily.Any(s => s.Score < s.Definition.ClearScore)) return false;
        var day = ActivityDates.Start(2, now);
        using var q = Command(c, t, "INSERT OR IGNORE INTO mission_facts(account_id,source_kind,source_key,fact_type,target_key,occurred_at,business_day,qty) VALUES($id,'daily-clear',$day,601,'0',$now,$business,1)",
            ("$id", account), ("$day", day.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$now", now), ("$business", day)); return q.ExecuteNonQuery()==1;
    }
    public ActivityReceiveResult Receive(SqliteConnection c, SqliteTransaction t, long account, string player, long now,
        ActivityClaim[] selection, string retryToken, ActivityGrant grant)
    {
        if (string.IsNullOrWhiteSpace(retryToken) || retryToken.Length > 256 || selection.Length is < 1 or > 1357 || selection.Distinct().Count() != selection.Length || selection.Any(x => x.MissionType is not (1 or 2 or 3 or 4 or 7)))
            return new(400, player);
        var ordered = selection.OrderBy(s => s.MissionType).ThenBy(s => s.MissionId).ToArray();
        var semantic = "missions:" + string.Join(',', ordered.Select(s => $"{s.MissionType}:{s.MissionId}"));
        // Existing shared ledger has a 256-char semantic constraint; hash the canonical full batch rather than truncate.
        semantic = "missions:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(semantic)));
        var retry = SHA256.HashData(Encoding.UTF8.GetBytes(retryToken));
        using (var old = Command(c, t, "SELECT route,semantic,result_json FROM economy_transactions WHERE account_id=$id AND retry_hash=$retry", ("$id", account), ("$retry", retry)))
        using (var r = old.ExecuteReader())
            if (r.Read()) return r.GetString(0) == ReceiveRoute && r.GetString(1) == semantic
                ? new(200, player, JsonDocument.Parse(r.GetString(2)).RootElement.Clone()) : new(409, player);
        var states = ReadMissions(c, t, account, player, now);
        var selected = ordered.Select(s => states.SingleOrDefault(x => x.Definition.MissionType == s.MissionType && x.Definition.MissionId == s.MissionId)).ToArray();
        if (selected.Any(s => s is null)) return new(400, player);
        if (selected.Any(s => !s!.CanReceive)) return new(409, player);
        var rewards = selected.SelectMany(s => config.Rewards.Where(r => r.MissionType == s!.Definition.MissionType && r.ItemGroupId == s.Definition.ItemGroupId)).ToArray();
        if (selected.Any(s => !rewards.Any(r => r.MissionType == s!.Definition.MissionType && r.ItemGroupId == s.Definition.ItemGroupId)) || rewards.Any(r => config.UnsupportedRewardTypes.Contains(r.ItemType)))
            return new(501, player); // Pending original reward types must not silently disappear.
        // Grant adapter preflights the WHOLE batch, then applies in caller's transaction. Any exception must roll back caller transaction.
        var typed = rewards.Select(r => new CommerceReward(r.ItemType, r.ItemId, r.ItemQty)).ToArray();
        var granted = grant(c, t, account, player, now, typed);
        foreach (var state in selected)
        {
            var m = state!.Definition;
            using var claim = Command(c, t, "INSERT INTO mission_claims(account_id,mission_type,mission_id,period_key,claimed_at,retry_hash) VALUES($id,$type,$mission,$period,$now,$retry)",
                ("$id", account), ("$type", m.MissionType), ("$mission", m.MissionId), ("$period", ActivityDates.Period(m.MissionType, now)), ("$now", now), ("$retry", retry)); claim.ExecuteNonQuery();
        }
        var frozen = JsonSerializer.Serialize(new { source = "mission", missions = ordered, typed_rewards = typed, rewards = granted.Rewards, receive_expired_missions = Array.Empty<object>() });
        using var receipt = Command(c, t, "INSERT INTO economy_transactions(account_id,retry_hash,route,semantic,config_hash,result_json,cost_json,created_at) VALUES($id,$retry,'mission/receive',$semantic,$config,$result,'{}',$now)",
            ("$id", account), ("$retry", retry), ("$semantic", semantic), ("$config", ActivityConfig.ExpectedFingerprint), ("$result", frozen), ("$now", now)); receipt.ExecuteNonQuery();
        return new(200, granted.Player, JsonDocument.Parse(frozen).RootElement.Clone());
    }
}
