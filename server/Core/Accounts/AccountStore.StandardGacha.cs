using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult StandardGacha(StandardGachaRequest request)
    {
        if (standardGachaConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                var retryHash = Hash(request.RetryToken);
                var receipt = FindEconomyReceipt(connection, transaction, id, retryHash);
                if (receipt is not null)
                {
                    if (receipt.Route != StandardGachaRequest.Route || receipt.Semantic != request.Semantic) return new(409);
                    return new(200, EconomyDrawAction(connection, transaction, id, player, receipt.Result));
                }
                if (!HasFinishedTutorial(player)) return new(409);
                var pool = standardGachaConfig.Pools.SingleOrDefault(p => p.GachaId == request.GachaId);
                var lot = pool?.Lots.SingleOrDefault(l => l.LotId == request.LotId);
                if (pool is null || lot is null) return new(400);
                if (!pool.IsOpen(now)) return new(409);
                var updatedPlayer = DebitCurrency(player, lot.CostQty, out var freeSpent, out var paidSpent);
                if (updatedPlayer is null) return new(409);
                var selections = pool.Draw(lot, nextGachaBelow);
                EnsureInstanceCapacity(connection, transaction, "owned_cards", selections.Count(c => c.ItemType == 5));
                EnsureEquipmentCapacity(connection, transaction, id, selections.Where(c => c.ItemType == 7)
                    .Select(c => (standardGachaConfig.Equips.Single(e => e.EquipId == c.ItemId), 1)), grantContext);
                var results = new List<Dictionary<string, object?>>();
                foreach (var selection in selections)
                    results.Add(selection.ItemType == 5
                        ? GrantCard(connection, transaction, id, standardGachaConfig.Cards.Single(c => c.CardId == selection.ItemId))
                        : GrantEquip(connection, transaction, id, now, standardGachaConfig.Equips.Single(e => e.EquipId == selection.ItemId), grantContext));
                IncrementGachaCounter(connection, transaction, id, pool.GachaId, lot.LotType, now);
                SavePlayer(connection, transaction, id, updatedPlayer);
                var frozenJson = JsonSerializer.Serialize(new { lot_contents = results, lot_bonus = Array.Empty<object>() });
                var costJson = JsonSerializer.Serialize(new { item_type = lot.CostType, item_id = lot.CostItemId,
                    item_qty = lot.CostQty, free_paid_coin = freeSpent, paid_coin = paidSpent });
                InsertEconomyReceipt(connection, transaction, id, retryHash, StandardGachaRequest.Route, request.Semantic,
                    StandardGachaConfig.ExpectedFingerprint, frozenJson, costJson, now);
                var action = EconomyDrawAction(connection, transaction, id, updatedPlayer, frozenJson);
                transaction.Commit();
                return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private Dictionary<string, object?> EconomyDrawAction(SqliteConnection connection, SqliteTransaction transaction,
        long id, string player, string result, long? nowOverride = null)
    {
        var action = Snapshot(connection, transaction, id, player, nowOverride: nowOverride);
        using var frozen = JsonDocument.Parse(result);
        action["lot_contents"] = frozen.RootElement.GetProperty("lot_contents").Clone();
        action["lot_bonus"] = frozen.RootElement.GetProperty("lot_bonus").Clone();
        return action;
    }

    private static void IncrementGachaCounter(SqliteConnection connection, SqliteTransaction transaction,
        long id, int gachaId, int lotType, long now)
    {
        long[]? counters = null;
        using (var query = Command(connection, transaction,
            "SELECT today_once_count,today_multi_count,today_special_count,total_once_count,total_multi_count,total_special_count,last_once_date,last_multi_date,last_special_date FROM user_gachas WHERE account_id=$id AND gacha_id=$gacha"))
        {
            query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$gacha", gachaId);
            using var reader = query.ExecuteReader();
            if (reader.Read()) counters = Enumerable.Range(0, 9).Select(reader.GetInt64).ToArray();
        }
        var isNew = counters is null;
        if (isNew)
        {
            EnsureInstanceCapacity(connection, transaction, "user_gachas", 1);
            counters = new long[9];
        }
        var today = GachaBusinessDate(now);
        for (var i = 0; i < 3; i++)
        {
            if (counters![i] is < 0 or > int.MaxValue || counters[i + 3] is < 0 or > int.MaxValue ||
                counters[i + 6] < 0 || counters[i + 6] > DateTimeOffset.MaxValue.ToUnixTimeSeconds() - 32400)
                throw new GachaCapacityException();
            if (counters[i + 6] == 0 || GachaBusinessDate(counters[i + 6]) != today)
                counters[i] = 0;
        }
        var index = lotType - 1;
        if (index is < 0 or > 2 || counters![index] == int.MaxValue || counters[index + 3] == int.MaxValue)
            throw new GachaCapacityException();
        counters[index]++; counters[index + 3]++; counters[index + 6] = now;
        using var update = Command(connection, transaction, isNew
            ? "INSERT INTO user_gachas(account_id,gacha_id,today_once_count,today_multi_count,today_special_count,total_once_count,total_multi_count,total_special_count,last_once_date,last_multi_date,last_special_date) VALUES($id,$gacha,$c0,$c1,$c2,$c3,$c4,$c5,$c6,$c7,$c8)"
            : "UPDATE user_gachas SET today_once_count=$c0,today_multi_count=$c1,today_special_count=$c2,total_once_count=$c3,total_multi_count=$c4,total_special_count=$c5,last_once_date=$c6,last_multi_date=$c7,last_special_date=$c8 WHERE account_id=$id AND gacha_id=$gacha");
        update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$gacha", gachaId);
        for (var i = 0; i < counters.Length; i++) update.Parameters.AddWithValue("$c" + i, counters[i]);
        update.ExecuteNonQuery();
    }
}
