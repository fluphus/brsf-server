using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult ResetVip(VipResetRequest request)
    {
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var now = DateTimeOffset.FromUnixTimeSeconds(Now);
            long id;
            string player;
            using (var session = Command(connection, transaction,
                "SELECT a.account_id,a.player_json FROM sessions s JOIN accounts a ON a.account_id=s.account_id WHERE s.token_hash=$hash AND s.expires_at>$now"))
            {
                session.Parameters.AddWithValue("$hash", Hash(request.AuthToken));
                session.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
                using var reader = session.ExecuteReader();
                if (!reader.Read()) return new(401);
                id = reader.GetInt64(0); player = reader.GetString(1);
            }
            using var document = JsonDocument.Parse(player);
            var changes = new List<string>();
            // VipPassMaster136 has exactly vip_pass_1 and vip_pass_2. Both enable
            // LoginBonus and DailyQuest; neither enables IsLoop. Product IDs
            // therefore never authorize changing vip_loop_end_date.
            if (request.ProductIds.Length > 0)
            {
                var localNow = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()).ToOffset(TimeSpan.FromHours(9)).DateTime;
                foreach (var field in new[] { "vip_login_bonus_end_date", "vip_daily_quest_end_date" })
                {
                    var values = document.RootElement.EnumerateObject().Where(p => p.NameEquals(field)).ToArray();
                    if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.String) return new(409);
                    var text = values[0].Value.GetString();
                    if (text == PlayerState.NoVipEndDate) continue;
                    if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var end)) return new(409);
                    if (HasVipExpired(end, localNow)) changes.Add(field);
                }
            }
            if (changes.Count > 0)
            {
                var updated = JsonNode.Parse(player)!.AsObject();
                foreach (var field in changes) updated[field] = PlayerState.NoVipEndDate;
                player = updated.ToJsonString();
                using var update = Command(connection, transaction, "UPDATE accounts SET player_json=$player WHERE account_id=$id");
                update.Parameters.AddWithValue("$player", player); update.Parameters.AddWithValue("$id", id);
                if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("VIP reset account changed.");
            }
            if (request.ProductIds.Length > 0 && !HasActiveVip(connection, transaction, id, now.ToUnixTimeSeconds()))
            {
                using var reset = Command(connection, transaction,
                    "UPDATE owned_shop_counts SET count=0 WHERE account_id=$id AND parent_product_id=4001 AND count<>0");
                reset.Parameters.AddWithValue("$id", id); reset.ExecuteNonQuery();
            }
            // UserDataPack.Update replaces User, so return its complete persisted
            // row. Reset grants no inventory, currency, welfare or progression.
            var action = vipPassConfig is null ? TutorialProgression.Action(player)
                : Snapshot(connection, transaction, id, player, nowOverride: now.ToUnixTimeSeconds());
            transaction.Commit();
            return new(200, action);
        }
    }

    // Native DateTimeUtility.IsPast includes the equality boundary; both wire
    // dates and common.datetime use whole seconds in the server's UTC+9 clock.
    private static bool HasVipExpired(DateTime end, DateTime now) => end != DateTime.MinValue && end <= now;
}
