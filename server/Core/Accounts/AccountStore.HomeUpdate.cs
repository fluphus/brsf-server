using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult UpdateHomeStep(HomeUpdateStepRequest request)
    {
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
            using var document = JsonDocument.Parse(player);
            var step = TutorialProgression.ReadStep(document.RootElement);
            if (step is not (9999 or 10000)) return new(409);
            if (step == 9999)
            {
                var previous = player;
                // Local compatibility policy only: original reward quantities
                // have not been recovered. The native enum value 10000 closes
                // the tutorial UI; it is not a ledger proving rewards granted.
                // A future reward implementation must track grants separately.
                player = TutorialProgression.SetStep(player, 10000, now);
                using var update = Command(connection, transaction,
                    "UPDATE accounts SET player_json=$player WHERE account_id=$id AND player_json=$previous");
                update.Parameters.AddWithValue("$player", player); update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$previous", previous);
                if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Home tutorial account changed.");
            }
            var action = TutorialProgression.Action(player);
            transaction.Commit();
            return new(200, action);
        }
    }
}
