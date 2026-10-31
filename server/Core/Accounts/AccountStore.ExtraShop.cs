using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult PurchaseExtraShop(ExtraShopPurchaseRequest request)
    {
        if (commerceConfig is null || eventQuestConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        if (request.ContentsId <= 0 || request.ProductId <= 0 || request.Qty is < 1 or > 5 ||
            string.IsNullOrWhiteSpace(request.RetryToken) || request.RetryToken.Length > 256) return new(400);
        var product = commerceConfig.ExtraProducts.SingleOrDefault(p => p.ProductId == request.ProductId);
        if (product is null || product.ContentsId != request.ContentsId) return new(400);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var now = Now;
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var account, out var player)) return new(401);
                var retry = Hash(request.RetryToken);
                if (FindEconomyReceipt(connection, transaction, account, retry) is { } old)
                    return old.Route == ExtraShopPurchaseRequest.Route && old.Semantic == request.Semantic
                        ? new(200, Snapshot(connection, transaction, account, player, nowOverride: now)) : new(409);
                if (!HasFinishedTutorial(player) || product.IsDeleted || !CommerceDates.IsOpen(product.OpenDate, product.CloseDate, now) ||
                    product.ConditionType != 1 || product.ConditionQuestIds.Length == 0 ||
                    product.ConditionQuestIds.Any(q => !ExtraShopPrerequisiteCleared(connection, transaction, account, q))) return new(409);
                var before = ExtraShopPurchaseCount(connection, transaction, account, product.ContentsId, product.ProductId);
                if (before < 0 || before > product.PurchaseLimit || request.Qty > product.PurchaseLimit - before) return new(409);
                var cost = checked(product.ConsumeItemQty * request.Qty);
                if (product.ConsumeItemType != 6 || !DebitConsume(connection, transaction, account, product.ConsumeItemId, cost)) return new(409);
                var after = checked(before + request.Qty);
                using (var update = Command(connection, transaction,
                    "INSERT INTO owned_extra_shop_counts(account_id,contents_id,product_id,count) VALUES($account,$contents,$product,$count) ON CONFLICT(account_id,contents_id,product_id) DO UPDATE SET count=excluded.count"))
                {
                    update.Parameters.AddWithValue("$account", account); update.Parameters.AddWithValue("$contents", product.ContentsId);
                    update.Parameters.AddWithValue("$product", product.ProductId); update.Parameters.AddWithValue("$count", after); update.ExecuteNonQuery();
                }
                // RewardType1 is an original client QuestBuff count; type2 is a
                // real gate entitlement. Neither manufactures inventory rewards.
                var result = JsonSerializer.Serialize(new { source = "extra-shop-purchase", contents_id = product.ContentsId,
                    product_id = product.ProductId, qty = request.Qty, reward_type = product.RewardType,
                    purchase_count_before = before, purchase_count_after = after });
                var spent = JsonSerializer.Serialize(new { item_type = 6, item_id = product.ConsumeItemId, item_qty = cost });
                InsertEconomyReceipt(connection, transaction, account, retry, ExtraShopPurchaseRequest.Route, request.Semantic,
                    CommerceConfig.ExpectedFingerprint, result, spent, now);
                var action = Snapshot(connection, transaction, account, player, nowOverride: now);
                transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
            catch (OverflowException) { return new(503); }
        }
    }

    private static long ExtraShopPurchaseCount(SqliteConnection c, SqliteTransaction t, long account, int contents, int product)
    {
        using var q = Command(c, t, "SELECT count FROM owned_extra_shop_counts WHERE account_id=$account AND contents_id=$contents AND product_id=$product");
        q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$contents", contents); q.Parameters.AddWithValue("$product", product);
        return q.ExecuteScalar() is { } value ? Convert.ToInt64(value) : 0;
    }

    private static bool ExtraShopPrerequisiteCleared(SqliteConnection c, SqliteTransaction t, long account, int quest)
    {
        using var q = Command(c, t, "SELECT EXISTS(SELECT 1 FROM event_quests WHERE account_id=$account AND quest_id=$quest AND clear_rate>0)");
        q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$quest", quest); return (long)q.ExecuteScalar()! == 1;
    }
}
