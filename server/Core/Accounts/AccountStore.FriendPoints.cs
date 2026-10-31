using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult PickupFriendPoints(FriendPointPickupRequest request)
    {
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            // Local scope currently has no friend-use production events and thus
            // no pending friend point grants. SQLite enforces that this empty
            // pickup cannot alter a preexisting balance or any account state.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
            { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            string player;
            using (var session = Command(connection, transaction,
                "SELECT a.player_json FROM sessions s JOIN accounts a ON a.account_id=s.account_id WHERE s.token_hash=$hash AND s.expires_at>$now"))
            {
                session.Parameters.AddWithValue("$hash", Hash(request.AuthToken));
                session.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                using var reader = session.ExecuteReader();
                if (!reader.Read()) return new(401);
                player = reader.GetString(0);
            }
            // HomePickupFriendPtAction.GetFriendPt is the newly collected amount,
            // not the user's existing FriendPt balance. UserUpdater replaces the
            // complete row. Bootstrap.Response also sends the native-required
            // common.send_present_box_items empty array for this zero pickup.
            var action = TutorialProgression.Action(player);
            action["get_friend_pt"] = 0;
            transaction.Commit();
            return new(200, action);
        }
    }
}
