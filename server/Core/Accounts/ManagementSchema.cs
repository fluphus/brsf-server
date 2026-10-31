using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
internal static class ManagementSchema
{
 internal static readonly Dictionary<string,string> Tables=new(StringComparer.Ordinal)
 {
  ["management_receipts"]="CREATE TABLE management_receipts (operation_id TEXT NOT NULL PRIMARY KEY CHECK(length(operation_id) = 36 AND operation_id = lower(operation_id)), created_at INTEGER NOT NULL CHECK(created_at > 0)) STRICT",
  ["management_operations"]="CREATE TABLE management_operations (operation_id TEXT NOT NULL PRIMARY KEY REFERENCES management_receipts(operation_id), account_id INTEGER NOT NULL REFERENCES accounts(account_id), kind TEXT NOT NULL CHECK(length(kind) BETWEEN 1 AND 64), request_hash BLOB NOT NULL CHECK(typeof(request_hash) = 'blob' AND length(request_hash) = 32), base_revision TEXT NOT NULL CHECK(length(base_revision) IN (0,64)), result_json TEXT NOT NULL CHECK(json_valid(result_json) AND json_type(result_json) = 'object'), created_at INTEGER NOT NULL CHECK(created_at > 0)) STRICT"
 };
 internal static void Create(SqliteConnection c,SqliteTransaction t){using var q=c.CreateCommand();q.Transaction=t;q.CommandText=string.Join(';',Tables.Values)+";";q.ExecuteNonQuery();}
}
