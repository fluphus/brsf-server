using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    // Wire balances are UInt64, but the original reactive total and ItemData.Num
    // use unchecked Int32 arithmetic. This is a local compatibility limit.
    public const ulong MaximumLocalGemBalance = int.MaxValue;
    // The account/retry namespace is shared by every local economy route. A
    // frozen receipt cannot be reused for a different route or semantic.
    private const string EconomyTransactionSql = "CREATE TABLE economy_transactions (account_id INTEGER NOT NULL REFERENCES accounts(account_id), retry_hash BLOB NOT NULL CHECK(typeof(retry_hash) = 'blob' AND length(retry_hash) = 32), route TEXT NOT NULL CHECK(length(route) BETWEEN 1 AND 128), semantic TEXT NOT NULL CHECK(length(semantic) BETWEEN 1 AND 256), config_hash TEXT NOT NULL CHECK(length(config_hash) = 64), result_json TEXT NOT NULL CHECK(json_valid(result_json) AND json_type(result_json) = 'object'), cost_json TEXT NOT NULL CHECK(json_valid(cost_json) AND json_type(cost_json) = 'object'), created_at INTEGER NOT NULL CHECK(created_at > 0), PRIMARY KEY(account_id,retry_hash)) STRICT";
    private static Dictionary<string, string> EconomyInventorySchema => new(InventorySchema, StringComparer.Ordinal)
    {
        ["user_gachas"] = InventorySchema["user_gachas"].Replace("CHECK(gacha_id = 9999)", "CHECK(gacha_id IN (9999,11012,12012))", StringComparison.Ordinal)
            .Replace("CHECK(last_multi_date > 0)", "CHECK(last_multi_date >= 0)", StringComparison.Ordinal)
    };
    private static Dictionary<string, string> LegacyCurrentInventorySchema => new(EconomyInventorySchema, StringComparer.Ordinal)
    {
        ["user_gachas"] = EconomyInventorySchema["user_gachas"].Replace("CHECK(gacha_id IN (9999,11012,12012))",
            "CHECK(gacha_id IN (9999,11012,12012,55003,55004))", StringComparison.Ordinal)
    };

    private static void MigrateEconomySchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "ALTER TABLE user_gachas RENAME TO user_gachas_v4;" + EconomyInventorySchema["user_gachas"] +
            "; INSERT INTO user_gachas SELECT * FROM user_gachas_v4;" +
            " INSERT INTO sqlite_sequence(name,seq) SELECT 'user_gachas',seq FROM sqlite_sequence WHERE name='user_gachas_v4' AND NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='user_gachas');" +
            " UPDATE sqlite_sequence SET seq=MAX(seq,COALESCE((SELECT seq FROM sqlite_sequence WHERE name='user_gachas_v4'),0)) WHERE name='user_gachas';" +
            " DROP TABLE user_gachas_v4;" + EconomyTransactionSql + ";" + LegacyOwnedShopCountSql + "; PRAGMA user_version=5;");
    }

    private bool TryReadAuthenticatedAccount(SqliteConnection connection, SqliteTransaction transaction,
        string authToken, long now, out long id, out string player)
    {
        using var session = Command(connection, transaction,
            "SELECT a.account_id,a.player_json FROM sessions s JOIN accounts a ON a.account_id=s.account_id WHERE s.token_hash=$hash AND s.expires_at>$now");
        session.Parameters.AddWithValue("$hash", Hash(authToken)); session.Parameters.AddWithValue("$now", now);
        using var reader = session.ExecuteReader();
        if (!reader.Read()) { id = 0; player = ""; return false; }
        id = reader.GetInt64(0); player = reader.GetString(1);
        if(IsManagementLeased(id)){id=0;player="";return false;}
        return true;
    }

    private static ulong ReadCurrency(JsonElement player, string name)
    {
        var properties = player.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
        if (properties.Length != 1 || properties[0].Value.ValueKind != JsonValueKind.Number ||
            !properties[0].Value.TryGetUInt64(out var value)) throw new GachaCapacityException();
        return value;
    }

    private static string? DebitCurrency(string player, int cost, out ulong freeSpent, out ulong paidSpent)
    {
        using var document = JsonDocument.Parse(player);
        var free = ReadCurrency(document.RootElement, "free_paid_coin");
        var paid = ReadCurrency(document.RootElement, "paid_coin");
        if (free > MaximumLocalGemBalance || paid > MaximumLocalGemBalance || free > MaximumLocalGemBalance - paid)
            throw new GachaCapacityException();
        freeSpent = Math.Min(free, checked((ulong)cost)); paidSpent = checked((ulong)cost) - freeSpent;
        if (paid < paidSpent) return null;
        var updated = JsonNode.Parse(player)!.AsObject();
        updated["free_paid_coin"] = free - freeSpent; updated["paid_coin"] = paid - paidSpent;
        return updated.ToJsonString();
    }

    private static void SavePlayer(SqliteConnection connection, SqliteTransaction transaction, long id, string player)
    {
        using var update = Command(connection, transaction, "UPDATE accounts SET player_json=$player WHERE account_id=$id");
        update.Parameters.AddWithValue("$player", player); update.Parameters.AddWithValue("$id", id);
        if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Economy account changed.");
    }

    private static bool HasFinishedTutorial(string player)
    {
        using var document = JsonDocument.Parse(player);
        return TutorialProgression.ReadStep(document.RootElement) is 9999 or 10000;
    }

    private sealed record EconomyReceipt(string Route, string Semantic, string Result);
    private static EconomyReceipt? FindEconomyReceipt(SqliteConnection connection, SqliteTransaction transaction, long id, byte[] retryHash)
    {
        using var receipt = Command(connection, transaction,
            "SELECT route,semantic,result_json FROM economy_transactions WHERE account_id=$id AND retry_hash=$hash");
        receipt.Parameters.AddWithValue("$id", id); receipt.Parameters.AddWithValue("$hash", retryHash);
        using var reader = receipt.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static void InsertEconomyReceipt(SqliteConnection connection, SqliteTransaction transaction, long id, byte[] retryHash,
        string route, string semantic, string configHash, string result, string cost, long now)
    {
        using var receipt = Command(connection, transaction,
            "INSERT INTO economy_transactions(account_id,retry_hash,route,semantic,config_hash,result_json,cost_json,created_at) VALUES($id,$hash,$route,$semantic,$config,$result,$cost,$now)");
        receipt.Parameters.AddWithValue("$id", id); receipt.Parameters.AddWithValue("$hash", retryHash);
        receipt.Parameters.AddWithValue("$route", route); receipt.Parameters.AddWithValue("$semantic", semantic);
        receipt.Parameters.AddWithValue("$config", configHash); receipt.Parameters.AddWithValue("$result", result);
        receipt.Parameters.AddWithValue("$cost", cost); receipt.Parameters.AddWithValue("$now", now); receipt.ExecuteNonQuery();
    }
}
