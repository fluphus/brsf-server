using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    // Response-only metadata: use the server-produced user row, never request
    // identity. This badge is a fresh read-only view after the action transaction.
    public AccountResult ProjectVipBadge(AccountResult result)
    {
        if (vipPassConfig is null || result.Status != 200 || result.Action is null ||
            !result.Action.TryGetValue("u_user", out var value) || value is not JsonElement[] { Length: 1 } users) return result;
        var id = users[0].GetProperty("user_id").GetInt32();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var count = presentCatalog is null ? (Item:VipPendingCount(connection,transaction,id),Equip:0) : PresentPendingCounts(connection,transaction,id,Now); transaction.Commit();
        return result with { PresentItemBadge = count.Item, PresentEquipBadge = count.Equip };
    }

    private static int VipPendingCount(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var query = Command(connection, transaction, "SELECT COUNT(*) FROM vip_present_items WHERE account_id=$id AND received_at=0");
        query.Parameters.AddWithValue("$id", id); return checked((int)(long)query.ExecuteScalar()!);
    }

    public AccountResult ListVipPresents(VipPresentListRequest request)
    {
        if (vipPassConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
            var now = Now;
            if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
            if (!HasFinishedTutorial(player)) return new(409);
            var all = VipPresentRows(connection, transaction, id);
            // All localVIP gifts are identical no-expiry currency. Receipt-date,
            // rarity and autosale therefore tie; persistent IDs break ties.
            var filtered = request.Expiration != 1 && (request.ItemTypes.Length == 0 || request.ItemTypes.Contains(2)) ? all : [];
            var ordered = request.Order == 1 ? filtered.Reverse() : filtered.AsEnumerable();
            var offset = checked(((long)request.Page - 1) * 50);
            var page = offset > int.MaxValue ? [] : ordered.Skip((int)offset).Take(50).ToArray();
            var action = Snapshot(connection, transaction, id, player, nowOverride: now);
            action["u_present_item"] = page; action["unread_present_qty"] = filtered.Length;
            action["has_warning_receive_present"] = false;
            transaction.Commit(); return new(200, action, PresentItemBadge: all.Length);
        }
    }

    public AccountResult ReceiveVipPresents(VipPresentReceiveRequest request)
    {
        if (vipPassConfig is null) return new(501);
        if (request.PresentIds.Length is < 1 or > 50 || request.PresentIds.Any(id => id <= 0) || request.PresentIds.Distinct().Count() != request.PresentIds.Length) return new(400);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var now = Now;
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                var retryHash = Hash(request.RetryToken); var old = FindEconomyReceipt(connection, transaction, id, retryHash);
                if (old is not null)
                    return old.Route == VipPresentReceiveRequest.Route && old.Semantic == request.Semantic
                        ? VipReceiveAction(connection, transaction, id, player, old.Result, now) : new(409);
                if (!HasFinishedTutorial(player)) return new(409);
                var pending = new List<int>(); var notReceived = new List<int>();
                foreach (var present in request.PresentIds.Order())
                {
                    using var query = Command(connection, transaction,
                        "SELECT received_at FROM vip_present_items WHERE account_id=$id AND user_present_item_id=$present");
                    query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$present", present);
                    if (query.ExecuteScalar() is not long received) return new(409); // Unknown/foreign IDs have the same rejection.
                    if (received == 0) pending.Add(present); else notReceived.Add(present);
                }
                var amount = checked(pending.Count * vipPassConfig.DailyFreeQty);
                var updated = CreditCurrency(player, 0, amount, out var before, out var after);
                if (pending.Count > 0)
                {
                    SavePlayer(connection, transaction, id, updated);
                    foreach (var present in pending)
                    {
                        using var receive = Command(connection, transaction,
                            "UPDATE vip_present_items SET received_at=$now WHERE account_id=$id AND user_present_item_id=$present AND received_at=0");
                        receive.Parameters.AddWithValue("$now", now); receive.Parameters.AddWithValue("$id", id); receive.Parameters.AddWithValue("$present", present);
                        if (receive.ExecuteNonQuery() != 1) throw new GachaCapacityException();
                    }
                }
                object[] rewards = pending.Count == 0 ? [] : [new { item_type = 2, item_id = 0, item_qty = amount }];
                var frozen = JsonSerializer.Serialize(new { source = "vip-present-receive", received_ids = pending,
                    not_receive_user_present_item_ids = notReceived, rewards, before_balance = before, after_balance = after });
                InsertEconomyReceipt(connection, transaction, id, retryHash, VipPresentReceiveRequest.Route, request.Semantic,
                    VipPassConfig.ExpectedFingerprint, frozen, "{\"source\":\"vip-present-receive\",\"external_payment\":false,\"amount\":0}", now);
                var result = VipReceiveAction(connection, transaction, id, updated, frozen, now); transaction.Commit(); return result;
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private AccountResult VipReceiveAction(SqliteConnection connection, SqliteTransaction transaction, long id, string player, string frozen, long now)
    {
        using var result = JsonDocument.Parse(frozen);
        var action = Snapshot(connection, transaction, id, player, nowOverride: now);
        action["not_receive_user_present_item_ids"] = result.RootElement.GetProperty("not_receive_user_present_item_ids").Clone();
        action["convert_items"] = Array.Empty<object>();
        var rewards = result.RootElement.GetProperty("rewards").EnumerateArray().Select(value => (object)value.Clone()).ToArray();
        return new(200, action, rewards, VipPendingCount(connection, transaction, id));
    }

    public AccountResult ListVipLoginBonus(VipLoginBonusListRequest request)
    {
        if (vipPassConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
            var now = Now;
            if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
            if (!HasFinishedTutorial(player)) return new(409);
            var action = Snapshot(connection, transaction, id, player, nowOverride: now); var days = new List<int>();
            if (request.LoginBonusIds.Contains(9001))
            {
                using var query = Command(connection, transaction,
                    "SELECT day_count FROM vip_daily_deliveries WHERE account_id=$id AND activation_id=(SELECT activation_id FROM vip_entitlements WHERE account_id=$id) ORDER BY day_count");
                query.Parameters.AddWithValue("$id", id); using var reader = query.ExecuteReader();
                while (reader.Read()) days.Add(reader.GetInt32(0));
            }
            using var chara = Command(connection, transaction, "SELECT chara_id FROM owned_cards WHERE account_id=$id ORDER BY user_card_id LIMIT 1");
            chara.Parameters.AddWithValue("$id", id); var charaId = chara.ExecuteScalar() is long ownedChara ? checked((int)ownedChara) : 0;
            action["login_bonus"] = days.Count == 0 ? Array.Empty<object>() : [new { login_bonus_id = 9001,
                receive_days = string.Join(',', days.Select(day => day.ToString(CultureInfo.InvariantCulture))), view_chara_id = charaId }];
            var badge = VipPendingCount(connection, transaction, id); transaction.Commit();
            return new(200, action, PresentItemBadge: badge);
        }
    }
}
