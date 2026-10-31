using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
internal static class TakeoverSchema
{
 internal const string Fingerprint="4329d0e38bbeb0b4c73976ee087f6f69498ef8cf99d74bd4242113c944a15aa4";
 internal static readonly IReadOnlyDictionary<string,string> Objects=new Dictionary<string,string>
 {
  ["takeover_credentials"]="""
CREATE TABLE takeover_credentials (
 account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id),
 takeover_id TEXT NOT NULL UNIQUE COLLATE BINARY
   CHECK(length(takeover_id)=16 AND takeover_id NOT GLOB '*[^0-9A-Za-z]*'),
 credential_version INTEGER NOT NULL CHECK(credential_version BETWEEN 1 AND 2147483647),
 password_salt BLOB NOT NULL CHECK(typeof(password_salt)='blob' AND length(password_salt)=16),
 password_hash BLOB NOT NULL CHECK(typeof(password_hash)='blob' AND length(password_hash)=32),
 password_iterations INTEGER NOT NULL CHECK(password_iterations=600000),
 created_at INTEGER NOT NULL CHECK(created_at>0),
 updated_at INTEGER NOT NULL CHECK(updated_at>=created_at),
 revoked_at INTEGER NOT NULL DEFAULT 0 CHECK(revoked_at=0 OR revoked_at>=created_at),
 failed_attempts INTEGER NOT NULL DEFAULT 0 CHECK(failed_attempts BETWEEN 0 AND 2147483647),
 locked_until INTEGER NOT NULL DEFAULT 0 CHECK(locked_until>=0),
 last_transferred_at INTEGER NOT NULL DEFAULT 0 CHECK(last_transferred_at>=0)
) STRICT
""",
  ["takeover_grants"]="""
CREATE TABLE takeover_grants (
 token_hash BLOB NOT NULL PRIMARY KEY CHECK(typeof(token_hash)='blob' AND length(token_hash)=32),
 account_id INTEGER NOT NULL REFERENCES takeover_credentials(account_id),
 credential_version INTEGER NOT NULL CHECK(credential_version BETWEEN 1 AND 2147483647),
 created_at INTEGER NOT NULL CHECK(created_at>0),
 expires_at INTEGER NOT NULL CHECK(expires_at=created_at+600),
 state INTEGER NOT NULL CHECK(state IN(0,1,2)), --0pending1consumed2revoked
 consumed_at INTEGER NOT NULL DEFAULT 0 CHECK(consumed_at>=0),
 replay_until INTEGER NOT NULL DEFAULT 0 CHECK(replay_until>=0),
 consumed_retry_hash BLOB CHECK(consumed_retry_hash IS NULL OR (typeof(consumed_retry_hash)='blob' AND length(consumed_retry_hash)=32)),
 result_uuid_hash BLOB CHECK(result_uuid_hash IS NULL OR (typeof(result_uuid_hash)='blob' AND length(result_uuid_hash)=32)),
 CHECK((state IN(0,2) AND consumed_at=0 AND replay_until=0 AND consumed_retry_hash IS NULL AND result_uuid_hash IS NULL)
    OR (state=1 AND consumed_at>=created_at AND replay_until=consumed_at+600 AND consumed_retry_hash IS NOT NULL AND result_uuid_hash IS NOT NULL))
) STRICT
""",
 };
 internal static void Create(SqliteConnection c,SqliteTransaction t){foreach(var sql in Objects.Values){using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;q.ExecuteNonQuery();}}
}
