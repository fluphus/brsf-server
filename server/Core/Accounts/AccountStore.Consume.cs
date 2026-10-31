using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    // Local inventory scope is exactly the two original permanent guaranteed
    // tickets. The quantity cap is a local storage/Int32 compatibility policy.
    private const string OwnedConsumeSql = "CREATE TABLE owned_consumes (account_id INTEGER NOT NULL REFERENCES accounts(account_id), item_id INTEGER NOT NULL CHECK(item_id IN (80001,80002)), item_qty INTEGER NOT NULL CHECK(item_qty BETWEEN 0 AND 99999999), PRIMARY KEY(account_id,item_id)) STRICT";

    private static void MigrateConsumeSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "ALTER TABLE user_gachas RENAME TO user_gachas_v5;" + CurrentInventorySchema["user_gachas"] +
            "; INSERT INTO user_gachas SELECT * FROM user_gachas_v5;" +
            " INSERT INTO sqlite_sequence(name,seq) SELECT 'user_gachas',seq FROM sqlite_sequence WHERE name='user_gachas_v5' AND NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='user_gachas');" +
            " UPDATE sqlite_sequence SET seq=MAX(seq,COALESCE((SELECT seq FROM sqlite_sequence WHERE name='user_gachas_v5'),0)) WHERE name='user_gachas';" +
            " DROP TABLE user_gachas_v5;" + OwnedConsumeSql + "; PRAGMA user_version=6;");
    }
}
