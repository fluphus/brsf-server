using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult Home(HomeRequest request)
    {
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            // Completed-tutorial welcome gifts share this authenticated Home TX.
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
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
                using (var document = JsonDocument.Parse(player))
                    if (TutorialProgression.ReadStep(document.RootElement) is not (9999 or 10000)) return new(409);
                if (!TryGrantNewAccountGifts(connection, transaction, id, now)) return new(503);
                var bonuses=ObserveBeginnerLogin(connection,transaction,id,player,now,"home");
                ObserveAwakeningVoiceEntitlements(connection,transaction,id,now,"home");
                var action = HomeProjection.Build(Snapshot(connection, transaction, id, player, nowOverride: now));
                if (action is null) return new(409);
                if(beginnerConfig is not null)action["login_bonus"]=bonuses.Notices;
                int? badge = vipPassConfig is not null ? VipPendingCount(connection, transaction, id) : null;
                transaction.Commit();
                return new(200, action, SendPresentBoxItems:bonuses.NotificationItems,IsSendPresentBox:bonuses.IsSendPresentBox,PresentItemBadge: badge);
            }
            catch (GachaCapacityException) { return new(503); }
            catch(Exception ex)when(ex is SqliteException or OverflowException or InvalidDataException){return new(503);}
        }
    }
}
