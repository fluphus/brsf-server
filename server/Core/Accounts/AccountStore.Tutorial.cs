using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public const int MaximumTutorialReceiptsPerAccount = 256;
    private const string LegacyTutorialReceiptSql = "CREATE TABLE tutorial_receipts (account_id INTEGER NOT NULL REFERENCES accounts(account_id), retry_hash BLOB NOT NULL CHECK(typeof(retry_hash) = 'blob' AND length(retry_hash) = 32), route TEXT NOT NULL CHECK(route IN ('tutorial/update_step','tutorial/skip')), semantic TEXT NOT NULL CHECK(semantic IN ('update_step','skip:false','skip:true')), result_step INTEGER NOT NULL CHECK(result_step BETWEEN 0 AND 9999), created_at INTEGER NOT NULL CHECK(created_at > 0), PRIMARY KEY(account_id,retry_hash)) STRICT";
    private const string TutorialReceiptSql = "CREATE TABLE tutorial_receipts (account_id INTEGER NOT NULL REFERENCES accounts(account_id), retry_hash BLOB NOT NULL CHECK(typeof(retry_hash) = 'blob' AND length(retry_hash) = 32), route TEXT NOT NULL CHECK(route IN ('tutorial/update_step','tutorial/skip','tutorial/gacha_result')), semantic TEXT NOT NULL CHECK(semantic IN ('update_step','skip:false','skip:true','gacha:9999:99991:1:false')), result_step INTEGER NOT NULL CHECK(result_step BETWEEN 0 AND 9999), created_at INTEGER NOT NULL CHECK(created_at > 0), PRIMARY KEY(account_id,retry_hash)) STRICT";

    public AccountResult ProgressTutorial(TutorialRequest request)
    {
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long id;
            string player;
            using (var session = Command(connection, transaction, "SELECT a.account_id,a.player_json FROM sessions s JOIN accounts a ON a.account_id=s.account_id WHERE s.token_hash=$hash AND s.expires_at>$now"))
            {
                session.Parameters.AddWithValue("$hash", Hash(request.AuthToken));
                session.Parameters.AddWithValue("$now", now);
                using var reader = session.ExecuteReader();
                if (!reader.Read()) return new(401);
                id = reader.GetInt64(0); player = reader.GetString(1);
            }
            var retryHash = Hash(request.RetryToken);
            using (var receipt = Command(connection, transaction, "SELECT route,semantic FROM tutorial_receipts WHERE account_id=$id AND retry_hash=$hash"))
            {
                receipt.Parameters.AddWithValue("$id", id); receipt.Parameters.AddWithValue("$hash", retryHash);
                using var reader = receipt.ExecuteReader();
                if (reader.Read())
                {
                    var match = reader.GetString(0) == request.Route && reader.GetString(1) == request.Semantic;
                    reader.Close();
                    return match ? new(200, Snapshot(connection, transaction, id, player)) : new(409);
                }
            }
            using var document = JsonDocument.Parse(player);
            var next = TutorialProgression.Next(TutorialProgression.ReadStep(document.RootElement), request);
            if (!next.HasValue) return new(409);
            if(FindEconomyReceipt(connection,transaction,id,retryHash)is not null)return new(409);
            try { EnsureCompleteTutorialStarter(connection,transaction,id,player,now, grantContext); if(request.Route=="tutorial/skip"&&!EnsurePhoneSkipStarterFirstSlot(connection,transaction,id,now))return new(409); }
            catch (GachaCapacityException) { return new(503); }
            using (var count = Command(connection, transaction, "SELECT COUNT(*) FROM tutorial_receipts WHERE account_id=$id"))
            {
                count.Parameters.AddWithValue("$id", id);
                if ((long)count.ExecuteScalar()! >= MaximumTutorialReceiptsPerAccount) return new(503);
            }
            player = TutorialProgression.SetStep(player, next.Value, now);
            using (var update = Command(connection, transaction, "UPDATE accounts SET player_json=$player WHERE account_id=$id"))
            {
                update.Parameters.AddWithValue("$player", player); update.Parameters.AddWithValue("$id", id);
                if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Tutorial account changed.");
            }
            using (var insert = Command(connection, transaction, "INSERT INTO tutorial_receipts(account_id,retry_hash,route,semantic,result_step,created_at) VALUES($id,$hash,$route,$semantic,$step,$now)"))
            {
                insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$hash", retryHash);
                insert.Parameters.AddWithValue("$route", request.Route); insert.Parameters.AddWithValue("$semantic", request.Semantic);
                insert.Parameters.AddWithValue("$step", next.Value); insert.Parameters.AddWithValue("$now", now);
                insert.ExecuteNonQuery();
            }
            var action = Snapshot(connection, transaction, id, player);
            transaction.Commit();
            return new(200, action);
        }
    }
}
