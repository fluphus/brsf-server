using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;

internal static class DeleteSchema
{
 internal const string Sql="""
CREATE TABLE account_deletion_journal (
 uuid_hash BLOB NOT NULL PRIMARY KEY CHECK(typeof(uuid_hash)='blob' AND length(uuid_hash)=32),
 deleted_at INTEGER NOT NULL CHECK(deleted_at>0)
) STRICT
""";
 internal static void Create(SqliteConnection c,SqliteTransaction t)
 {using var q=c.CreateCommand();q.Transaction=t;q.CommandText=Sql;q.ExecuteNonQuery();}
}
