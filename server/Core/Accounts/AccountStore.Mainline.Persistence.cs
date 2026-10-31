using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private static readonly Dictionary<string, string> MainlineSchema = new(StringComparer.Ordinal)
    {
        ["quest_runs"] = "CREATE TABLE quest_runs (id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 2147483647), kind INTEGER NOT NULL CHECK(kind IN (0,1)), state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 4), config_hash TEXT NOT NULL CHECK(length(config_hash)=64), payload_json TEXT NOT NULL CHECK(json_valid(payload_json) AND json_type(payload_json)='object'), result_json TEXT NOT NULL CHECK(json_valid(result_json) AND json_type(result_json)='object'), stamina_spent INTEGER NOT NULL CHECK(stamina_spent BETWEEN 0 AND 999), created_at INTEGER NOT NULL CHECK(created_at>0), finished_at INTEGER NOT NULL CHECK(finished_at>=0), CHECK((state IN (0,1) AND finished_at=0) OR (state IN (2,3,4) AND finished_at>0))) STRICT",
        ["user_quests"] = "CREATE TABLE user_quests (id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 2147483647), clear_rate INTEGER NOT NULL CHECK(clear_rate BETWEEN 1 AND 6), clear_count INTEGER NOT NULL CHECK(clear_count BETWEEN 1 AND 2147483647), special_reward_received_at INTEGER NOT NULL CHECK(special_reward_received_at>=0), last_clear_at INTEGER NOT NULL CHECK(last_clear_at>0), UNIQUE(account_id,quest_id)) STRICT",
        ["quest_mission_claims"] = "CREATE TABLE quest_mission_claims (account_id INTEGER NOT NULL, quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 2147483647), mission_no INTEGER NOT NULL CHECK(mission_no BETWEEN 1 AND 2147483647), reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'), claimed_at INTEGER NOT NULL CHECK(claimed_at>0), PRIMARY KEY(account_id,quest_id,mission_no), FOREIGN KEY(account_id,quest_id) REFERENCES user_quests(account_id,quest_id)) STRICT",
        ["quest_power_reports"] = "CREATE TABLE quest_power_reports (account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id), last_total_power INTEGER NOT NULL CHECK(last_total_power BETWEEN 0 AND 2147483647), maximum_total_power INTEGER NOT NULL CHECK(maximum_total_power BETWEEN last_total_power AND 2147483647), reported_at INTEGER NOT NULL CHECK(reported_at>0)) STRICT"
    };
    private const string ActiveQuestRunIndexSql = "CREATE UNIQUE INDEX ix_quest_runs_active_account ON quest_runs(account_id) WHERE state IN (0,1)";

    private sealed record MainlineRun(int Id, int QuestId, int Kind, int State, string ConfigHash, string Payload, string Result, int StaminaSpent);

    private static MainlineRun? ActiveMainlineRun(SqliteConnection c, SqliteTransaction t, long account)
    {
        using var q = Command(c, t, "SELECT id,quest_id,kind,state,config_hash,payload_json,result_json,stamina_spent FROM quest_runs WHERE account_id=$account AND state IN (0,1)");
        q.Parameters.AddWithValue("$account", account); using var reader = q.ExecuteReader();
        return reader.Read() ? new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7)) : null;
    }

    private static bool MainlineCleared(SqliteConnection c, SqliteTransaction t, long account, int quest)
    {
        using var q = Command(c, t, "SELECT EXISTS(SELECT 1 FROM user_quests WHERE account_id=$account AND quest_id=$quest)");
        q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$quest", quest);
        return (long)q.ExecuteScalar()! == 1;
    }

    private static JsonElement[] MainlineQuestRows(SqliteConnection c, SqliteTransaction t, long account)
    {
        // SQL NULL is required by the shared IQuest consumer when no mission number exists.
        using var q = Command(c, t, "SELECT json_object('id',q.id,'user_id',q.account_id,'quest_id',q.quest_id,'clear_rate',q.clear_rate,'clear_mission_nos',(SELECT group_concat(mission_no,',') FROM (SELECT mission_no FROM quest_mission_claims m WHERE m.account_id=q.account_id AND m.quest_id=q.quest_id ORDER BY mission_no)),'special_reward_received_date',CASE WHEN q.special_reward_received_at=0 THEN '' ELSE strftime('%Y-%m-%d %H:%M:%S',q.special_reward_received_at,'unixepoch','+9 hours') END) FROM user_quests q WHERE q.account_id=$account ORDER BY q.id");
        q.Parameters.AddWithValue("$account", account); using var r = q.ExecuteReader(); var rows = new List<JsonElement>();
        while (r.Read()) { using var doc = JsonDocument.Parse(r.GetString(0)); rows.Add(doc.RootElement.Clone()); }
        return rows.ToArray();
    }
}
