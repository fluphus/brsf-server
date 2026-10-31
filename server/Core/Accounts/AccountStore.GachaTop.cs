using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult GachaTop(GachaTopRequest request)
    {
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
            { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var now = Now;
            long id;
            string player;
            using (var session = Command(connection, transaction,
                "SELECT a.account_id,a.player_json FROM sessions s JOIN accounts a ON a.account_id=s.account_id WHERE s.token_hash=$hash AND s.expires_at>$now"))
            {
                session.Parameters.AddWithValue("$hash", Hash(request.AuthToken));
                session.Parameters.AddWithValue("$now", now);
                using var reader = session.ExecuteReader();
                if (!reader.Read()) return new(401);
                id = reader.GetInt64(0); player = reader.GetString(1);
            }
            var action = Snapshot(connection, transaction, id, player, nowOverride: now);
            // No historic expired gacha points exist in this local save. This is
            // the amount converted by this call, not a currency grant.
            action["get_converted_gacha_point"] = 0;
            transaction.Commit();
            return new(200, action);
        }
    }
}
