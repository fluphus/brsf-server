using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult TicketGacha(TicketGachaRequest request)
    {
        if (ticketGachaConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                var retryHash = Hash(request.RetryToken); var receipt = FindEconomyReceipt(connection, transaction, id, retryHash);
                if (receipt is not null)
                    return receipt.Route == TicketGachaRequest.Route && receipt.Semantic == request.Semantic
                        ? new(200, EconomyDrawAction(connection, transaction, id, player, receipt.Result)) : new(409);
                if (!HasFinishedTutorial(player)) return new(409);
                var pool = ticketGachaConfig.Pools.SingleOrDefault(p => p.GachaId == request.GachaId && p.LotId == request.LotId);
                if (pool is null) return new(400);
                if (!pool.IsOpen || !TicketDrawWithinLimits(connection, transaction, id, pool, now)) return new(409);
                long quantity;
                using (var owned = Command(connection, transaction, "SELECT item_qty FROM owned_consumes WHERE account_id=$id AND item_id=$item"))
                {
                    owned.Parameters.AddWithValue("$id", id); owned.Parameters.AddWithValue("$item", pool.TicketItemId);
                    if (owned.ExecuteScalar() is not long stock || stock < 1) return new(409);
                    quantity = stock;
                }
                var selection = pool.Draw(nextGachaBelow);
                if (selection.ItemType == 5) EnsureInstanceCapacity(connection, transaction, "owned_cards", 1);
                else EnsureEquipmentCapacity(connection, transaction, id, [(ticketGachaConfig.Equips.Single(e => e.EquipId == selection.ItemId), 1)], grantContext);
                // User tables are deltas: preserve a zero row so an already
                // owned ticket is explicitly cleared in the native client.
                using (var consume = Command(connection, transaction,
                    "UPDATE owned_consumes SET item_qty=item_qty-1 WHERE account_id=$id AND item_id=$item AND item_qty=$before"))
                {
                    consume.Parameters.AddWithValue("$id", id); consume.Parameters.AddWithValue("$item", pool.TicketItemId);
                    consume.Parameters.AddWithValue("$before", quantity);
                    if (consume.ExecuteNonQuery() != 1) throw new GachaCapacityException();
                }
                var result = selection.ItemType == 5
                    ? GrantCard(connection, transaction, id, ticketGachaConfig.Cards.Single(c => c.CardId == selection.ItemId))
                    : GrantEquip(connection, transaction, id, now, ticketGachaConfig.Equips.Single(e => e.EquipId == selection.ItemId), grantContext);
                IncrementGachaCounter(connection, transaction, id, pool.GachaId, 3, now);
                var frozen = JsonSerializer.Serialize(new { lot_contents = new[] { result }, lot_bonus = Array.Empty<object>() });
                var cost = JsonSerializer.Serialize(new { item_type = 6, item_id = pool.TicketItemId, item_qty = 1,
                    before_qty = quantity, after_qty = quantity - 1 });
                InsertEconomyReceipt(connection, transaction, id, retryHash, TicketGachaRequest.Route, request.Semantic,
                    TicketGachaConfig.ExpectedFingerprint, frozen, cost, now);
                var action = EconomyDrawAction(connection, transaction, id, player, frozen); transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private static bool TicketDrawWithinLimits(SqliteConnection connection, SqliteTransaction transaction,
        long id, TicketGachaPool pool, long now)
    {
        using var query = Command(connection, transaction,
            "SELECT total_special_count,today_special_count,last_special_date FROM user_gachas WHERE account_id=$id AND gacha_id=$gacha");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$gacha", pool.GachaId);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return true;
        var total = reader.GetInt64(0); var today = reader.GetInt64(1); var last = reader.GetInt64(2);
        if (total is < 0 or > int.MaxValue || today is < 0 or > int.MaxValue ||
            last < 0 || last > DateTimeOffset.MaxValue.ToUnixTimeSeconds() - 32400) throw new GachaCapacityException();
        if (last == 0 || DateTimeOffset.FromUnixTimeSeconds(last).ToOffset(TimeSpan.FromHours(9)).Date !=
            DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).Date) today = 0;
        return (pool.TotalMaxCount == 0 || total < pool.TotalMaxCount) && (pool.DailyMaxCount == 0 || today < pool.DailyMaxCount);
    }
}
