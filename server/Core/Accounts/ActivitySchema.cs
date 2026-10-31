using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public static class ActivitySchema
{
 public static readonly IReadOnlyDictionary<string,string> Tables=new Dictionary<string,string>
 {
  ["owned_titles"]="CREATE TABLE owned_titles (id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647),account_id INTEGER NOT NULL REFERENCES accounts(account_id),title_id INTEGER NOT NULL CHECK(title_id>0),UNIQUE(account_id,title_id)) STRICT",
  ["mission_epochs"]="CREATE TABLE mission_epochs (account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id),epoch_seconds INTEGER NOT NULL CHECK(epoch_seconds>0),policy_version TEXT NOT NULL CHECK(policy_version='local-activity-v2')) STRICT",
  ["mission_epoch_excluded_receipts"]="CREATE TABLE mission_epoch_excluded_receipts (account_id INTEGER NOT NULL REFERENCES mission_epochs(account_id),retry_hash BLOB NOT NULL CHECK(typeof(retry_hash)='blob' AND length(retry_hash)=32),PRIMARY KEY(account_id,retry_hash)) STRICT",
  ["mission_claims"]="CREATE TABLE mission_claims (account_id INTEGER NOT NULL REFERENCES accounts(account_id),mission_type INTEGER NOT NULL CHECK(mission_type IN(1,2,3,4,7)),mission_id INTEGER NOT NULL CHECK(mission_id>0),period_key TEXT NOT NULL,claimed_at INTEGER NOT NULL CHECK(claimed_at>0),retry_hash BLOB NOT NULL CHECK(typeof(retry_hash)='blob' AND length(retry_hash)=32),PRIMARY KEY(account_id,mission_type,mission_id,period_key)) STRICT",
  ["mission_facts"]="CREATE TABLE mission_facts (account_id INTEGER NOT NULL REFERENCES accounts(account_id),source_kind TEXT NOT NULL,source_key TEXT NOT NULL,fact_type INTEGER NOT NULL CHECK(fact_type IN(414,601)),target_key TEXT NOT NULL CHECK(target_key='0'),occurred_at INTEGER NOT NULL CHECK(occurred_at>0),business_day INTEGER NOT NULL CHECK(business_day>0),qty INTEGER NOT NULL CHECK(qty=1),PRIMARY KEY(account_id,source_kind,source_key,fact_type,target_key)) STRICT",
  ["event_quests"]="CREATE TABLE event_quests (account_id INTEGER NOT NULL REFERENCES accounts(account_id),event_id INTEGER NOT NULL CHECK(event_id>0),quest_id INTEGER NOT NULL CHECK(quest_id>0),clear_rate INTEGER NOT NULL CHECK(clear_rate BETWEEN 0 AND 6),last_update_at INTEGER NOT NULL CHECK(last_update_at>0),PRIMARY KEY(account_id,event_id,quest_id)) STRICT",
  ["event_quest_mission_claims"]="CREATE TABLE event_quest_mission_claims (account_id INTEGER NOT NULL,event_id INTEGER NOT NULL,quest_id INTEGER NOT NULL,mission_no INTEGER NOT NULL CHECK(mission_no>0),reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),claimed_at INTEGER NOT NULL CHECK(claimed_at>0),PRIMARY KEY(account_id,event_id,quest_id,mission_no),FOREIGN KEY(account_id,event_id,quest_id) REFERENCES event_quests(account_id,event_id,quest_id)) STRICT",
  ["event_drop_claims"]="CREATE TABLE event_drop_claims (account_id INTEGER NOT NULL,event_id INTEGER NOT NULL,quest_id INTEGER NOT NULL,run_id INTEGER NOT NULL REFERENCES quest_runs(id),drop_place_id INTEGER NOT NULL CHECK(drop_place_id>0),business_day INTEGER NOT NULL CHECK(business_day>0),reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),claimed_at INTEGER NOT NULL CHECK(claimed_at>0),PRIMARY KEY(account_id,event_id,quest_id,drop_place_id),FOREIGN KEY(account_id,event_id,quest_id) REFERENCES event_quests(account_id,event_id,quest_id)) STRICT",
  ["event_points"]="CREATE TABLE event_points (account_id INTEGER NOT NULL REFERENCES accounts(account_id),event_id INTEGER NOT NULL CHECK(event_id>0),point INTEGER NOT NULL CHECK(point BETWEEN 0 AND 2147483647),PRIMARY KEY(account_id,event_id)) STRICT",
  ["event_point_reward_claims"]="CREATE TABLE event_point_reward_claims (account_id INTEGER NOT NULL,event_id INTEGER NOT NULL,reward_id INTEGER NOT NULL CHECK(reward_id>0),reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='array'),claimed_at INTEGER NOT NULL CHECK(claimed_at>0),PRIMARY KEY(account_id,event_id,reward_id),FOREIGN KEY(account_id,event_id) REFERENCES event_points(account_id,event_id)) STRICT",
 };
    public static SqliteCommand Command(SqliteConnection c, SqliteTransaction t, string sql, params (string Key, object Value)[] values)
    {
        var q = c.CreateCommand(); q.Transaction = t; q.CommandText = sql;
        foreach (var (key, value) in values) q.Parameters.AddWithValue(key, value);
        return q;
    }
    public static void CreateAndAnchor(SqliteConnection c, SqliteTransaction t, long now)
    {
        foreach (var sql in Tables.Values) { using var q = Command(c, t, sql); q.ExecuteNonQuery(); }
        // No schema-version mutation here. The root-reviewed integration owns PRAGMA user_version=15.
        using var accounts = Command(c, t, "SELECT account_id FROM accounts ORDER BY account_id");
        var ids = new List<long>(); using (var r = accounts.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
        foreach (var id in ids) Anchor(c, t, id, now);
    }
    public static void Anchor(SqliteConnection c, SqliteTransaction t, long account, long now)
    {
        using var insert = Command(c, t, "INSERT OR IGNORE INTO mission_epochs(account_id,epoch_seconds,policy_version) VALUES($id,$now,'local-activity-v2')", ("$id", account), ("$now", now));
        if (insert.ExecuteNonQuery() == 0) return;
        using var excluded = Command(c, t, "INSERT INTO mission_epoch_excluded_receipts(account_id,retry_hash) SELECT account_id,retry_hash FROM economy_transactions WHERE account_id=$id AND created_at=$now", ("$id", account), ("$now", now));
        excluded.ExecuteNonQuery();
    }
    public static void RecordSuccessfulLogin(SqliteConnection c, SqliteTransaction t, long account, long now)
    {
        var day = ActivityDates.Start(2, now);
        // Caller is successful authenticated login transaction, never nonce issuance or home/query.
        using var q = Command(c, t, "INSERT OR IGNORE INTO mission_facts(account_id,source_kind,source_key,fact_type,target_key,occurred_at,business_day,qty) SELECT $id,'login',$day,414,'0',$now,$business,1 WHERE EXISTS(SELECT 1 FROM mission_epochs WHERE account_id=$id AND epoch_seconds<=$now)",
            ("$id", account), ("$day", day.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$now", now), ("$business", day));
        q.ExecuteNonQuery();
    }
}
