using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
internal static class Schema18
{
 internal static readonly Dictionary<string,string> Objects=new(StringComparer.Ordinal)
 {
 ["quest_loop_sessions"]=@"CREATE TABLE quest_loop_sessions (
 id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647),
 account_id INTEGER NOT NULL REFERENCES accounts(account_id),
 quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 2147483647),
 max_loop_count INTEGER NOT NULL CHECK(max_loop_count BETWEEN 1 AND 10),
 next_loop_count INTEGER NOT NULL CHECK(next_loop_count BETWEEN 1 AND 11),
 status INTEGER NOT NULL CHECK(status IN(0,1,2)), -- 0 open,1 ended awaiting summary,2 finalized
 active_run_id INTEGER REFERENCES quest_runs(id),
 last_run_id INTEGER REFERENCES quest_runs(id),
 config_hash TEXT NOT NULL CHECK(length(config_hash)=64),
 frozen_json TEXT NOT NULL CHECK(json_valid(frozen_json) AND json_type(frozen_json)='object'),
 summary_json TEXT NOT NULL CHECK(json_valid(summary_json) AND json_type(summary_json)='object'),
 created_at INTEGER NOT NULL CHECK(created_at>0),
 ended_at INTEGER NOT NULL CHECK(ended_at>=0),
 finalized_at INTEGER NOT NULL CHECK(finalized_at>=0),
 CHECK(next_loop_count<=max_loop_count+1),
 CHECK((status=0 AND ended_at=0 AND finalized_at=0) OR
       (status=1 AND active_run_id IS NULL AND ended_at>0 AND finalized_at=0) OR
       (status=2 AND active_run_id IS NULL AND ended_at>0 AND finalized_at>=ended_at))
) STRICT",
 ["quest_loop_one_unfinalized"]=@"CREATE UNIQUE INDEX quest_loop_one_unfinalized ON quest_loop_sessions(account_id) WHERE status IN(0,1)",
  ["present_items"]=@"CREATE TABLE present_items (
 user_present_item_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(user_present_item_id BETWEEN 1 AND 2147483647),
 account_id INTEGER NOT NULL REFERENCES accounts(account_id),
 item_type INTEGER NOT NULL CHECK(item_type IN(2,3,4,5,6,10,11,12)),
 item_id INTEGER NOT NULL CHECK(item_id>=0),
 item_qty INTEGER NOT NULL CHECK(item_qty BETWEEN 1 AND 999999999),
 receive_place INTEGER NOT NULL CHECK(receive_place>0),
 condition_value INTEGER NOT NULL CHECK(condition_value>=0),
 is_over_qty_limit INTEGER NOT NULL CHECK(is_over_qty_limit IN(0,1)),
 expire_timestamp INTEGER NOT NULL CHECK(expire_timestamp BETWEEN 0 AND 2147483647),
 created_at INTEGER NOT NULL CHECK(created_at>0),
 state INTEGER NOT NULL CHECK(state IN(0,1,2)),
 terminal_at INTEGER NOT NULL CHECK(terminal_at>=0),
 producer_key TEXT NOT NULL CHECK(length(producer_key) BETWEEN 1 AND 256),
 producer_config_hash TEXT NOT NULL CHECK(length(producer_config_hash)=64),
 frozen_reward_json TEXT NOT NULL CHECK(json_valid(frozen_reward_json) AND json_type(frozen_reward_json)='object'),
 payload_hash TEXT NOT NULL CHECK(length(payload_hash)=64),
 source_retry_hash BLOB NOT NULL CHECK(typeof(source_retry_hash)='blob' AND length(source_retry_hash)=32),
 terminal_retry_hash BLOB CHECK(terminal_retry_hash IS NULL OR (typeof(terminal_retry_hash)='blob' AND length(terminal_retry_hash)=32)),
 UNIQUE(account_id,producer_key),
 FOREIGN KEY(account_id,source_retry_hash) REFERENCES economy_transactions(account_id,retry_hash) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(account_id,terminal_retry_hash) REFERENCES economy_transactions(account_id,retry_hash) DEFERRABLE INITIALLY DEFERRED,
 CHECK(expire_timestamp=0 OR expire_timestamp>created_at),
 CHECK((state=0 AND terminal_at=0 AND terminal_retry_hash IS NULL) OR
       (state=1 AND terminal_at>=created_at AND terminal_retry_hash IS NOT NULL AND (expire_timestamp=0 OR terminal_at<expire_timestamp)) OR
       (state=2 AND expire_timestamp>0 AND terminal_at=expire_timestamp AND terminal_retry_hash IS NOT NULL))
) STRICT",
  ["ix_present_items_account_state"]=@"CREATE INDEX ix_present_items_account_state ON present_items(account_id,state,user_present_item_id)",
  ["present_equips"]=@"CREATE TABLE present_equips (
 user_present_equip_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(user_present_equip_id BETWEEN 1 AND 2147483647),
 account_id INTEGER NOT NULL REFERENCES accounts(account_id),
 equip_id INTEGER NOT NULL CHECK(equip_id>0),
 equip_type INTEGER NOT NULL CHECK(equip_type IN(101,102,111,112,113,201,301,302)),
 rarity INTEGER NOT NULL CHECK(rarity BETWEEN 1 AND 5),
 effect_type INTEGER NOT NULL CHECK(effect_type>=0),
 qty INTEGER NOT NULL CHECK(qty BETWEEN 1 AND 99999999),
 receive_place INTEGER NOT NULL CHECK(receive_place>0),
 condition_value INTEGER NOT NULL CHECK(condition_value>=0),
 is_over_qty_limit INTEGER NOT NULL CHECK(is_over_qty_limit IN(0,1)),
 expire_timestamp INTEGER NOT NULL CHECK(expire_timestamp BETWEEN 0 AND 2147483647),
 created_at INTEGER NOT NULL CHECK(created_at>0),
 state INTEGER NOT NULL CHECK(state IN(0,1,2,3)),
 terminal_at INTEGER NOT NULL CHECK(terminal_at>=0),
 producer_key TEXT NOT NULL CHECK(length(producer_key) BETWEEN 1 AND 256),
 producer_config_hash TEXT NOT NULL CHECK(length(producer_config_hash)=64),
 frozen_reward_json TEXT NOT NULL CHECK(json_valid(frozen_reward_json) AND json_type(frozen_reward_json)='object'),
 payload_hash TEXT NOT NULL CHECK(length(payload_hash)=64),
 source_retry_hash BLOB NOT NULL CHECK(typeof(source_retry_hash)='blob' AND length(source_retry_hash)=32),
 terminal_retry_hash BLOB CHECK(terminal_retry_hash IS NULL OR (typeof(terminal_retry_hash)='blob' AND length(terminal_retry_hash)=32)),
 UNIQUE(account_id,producer_key),
 FOREIGN KEY(account_id,source_retry_hash) REFERENCES economy_transactions(account_id,retry_hash) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(account_id,terminal_retry_hash) REFERENCES economy_transactions(account_id,retry_hash) DEFERRABLE INITIALLY DEFERRED,
 CHECK(expire_timestamp=0 OR expire_timestamp>created_at),
 CHECK((state=0 AND terminal_at=0 AND terminal_retry_hash IS NULL) OR
       (state IN(1,3) AND terminal_at>=created_at AND terminal_retry_hash IS NOT NULL AND (expire_timestamp=0 OR terminal_at<expire_timestamp)) OR
       (state=2 AND expire_timestamp>0 AND terminal_at=expire_timestamp AND terminal_retry_hash IS NOT NULL))
) STRICT",
  ["ix_present_equips_account_state"]=@"CREATE INDEX ix_present_equips_account_state ON present_equips(account_id,state,user_present_equip_id)",
  ["beginner_claims"]=@"CREATE TABLE beginner_claims (
 account_id INTEGER NOT NULL REFERENCES accounts(account_id),
 campaign_id INTEGER NOT NULL CHECK(campaign_id>0),
 entry_kind INTEGER NOT NULL CHECK(entry_kind IN(0,1)), --0mission/1step
 entry_id INTEGER NOT NULL CHECK(entry_id>0),
 score_at_claim INTEGER NOT NULL CHECK(score_at_claim BETWEEN 0 AND 2147483647),
 claimed_at INTEGER NOT NULL CHECK(claimed_at>0),
 receipt_hash BLOB NOT NULL CHECK(typeof(receipt_hash)='blob'AND length(receipt_hash)=32),
 PRIMARY KEY(account_id,campaign_id,entry_kind,entry_id),
 FOREIGN KEY(account_id,receipt_hash)REFERENCES economy_transactions(account_id,retry_hash) DEFERRABLE INITIALLY DEFERRED
)STRICT",
  ["login_bonus_progress"]=@"CREATE TABLE login_bonus_progress (
 account_id INTEGER NOT NULL REFERENCES accounts(account_id),
 login_bonus_id INTEGER NOT NULL CHECK(login_bonus_id>0),
 epoch_seconds INTEGER NOT NULL CHECK(epoch_seconds>0),
 login_count INTEGER NOT NULL CHECK(login_count BETWEEN 0 AND 2147483647),
 last_business_day INTEGER NOT NULL CHECK(last_business_day>=0),
 cycle_index INTEGER NOT NULL CHECK(cycle_index BETWEEN 0 AND 2147483647),
 view_chara_id INTEGER NOT NULL CHECK(view_chara_id>=0),
 PRIMARY KEY(account_id,login_bonus_id)
)STRICT",
  ["login_bonus_deliveries"]=@"CREATE TABLE login_bonus_deliveries (
 account_id INTEGER NOT NULL,
 login_bonus_id INTEGER NOT NULL,
 cycle_index INTEGER NOT NULL CHECK(cycle_index>=0),
 day_count INTEGER NOT NULL CHECK(day_count>0),
 business_day INTEGER NOT NULL CHECK(business_day>0),
 created_at INTEGER NOT NULL CHECK(created_at>0),
 producer_key TEXT NOT NULL CHECK(length(producer_key)BETWEEN 1 AND 256),
 receipt_hash BLOB NOT NULL CHECK(typeof(receipt_hash)='blob'AND length(receipt_hash)=32),
 PRIMARY KEY(account_id,login_bonus_id,cycle_index,day_count),
 UNIQUE(account_id,login_bonus_id,business_day),
 UNIQUE(account_id,producer_key),
 FOREIGN KEY(account_id,login_bonus_id)REFERENCES login_bonus_progress(account_id,login_bonus_id),
 FOREIGN KEY(account_id,receipt_hash)REFERENCES economy_transactions(account_id,retry_hash) DEFERRABLE INITIALLY DEFERRED
)STRICT",
  ["local_bot_relations"]=@"CREATE TABLE local_bot_relations (
 account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id),
 is_following INTEGER NOT NULL CHECK(is_following IN(0,1)),
 is_follower INTEGER NOT NULL CHECK(is_follower IN(0,1)),
 new_follower INTEGER NOT NULL CHECK(new_follower IN(0,1) AND new_follower<=is_follower),
 followed_at INTEGER NOT NULL CHECK(followed_at>=0),
 follower_at INTEGER NOT NULL CHECK(follower_at>=0),
 updated_at INTEGER NOT NULL CHECK(updated_at>0),
 CHECK((is_following=0 AND followed_at=0) OR (is_following=1 AND followed_at>0)),
 CHECK((is_follower=0 AND follower_at=0) OR (is_follower=1 AND follower_at>0))
) STRICT",
 };
 internal static void Create(SqliteConnection c,SqliteTransaction t)
 {using var q=c.CreateCommand();q.Transaction=t;q.CommandText=string.Join(';',Objects.Values)+";";q.ExecuteNonQuery();}
}
