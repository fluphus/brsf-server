using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const string IndependentCardSkinSql = "CREATE TABLE owned_card_skins (account_id INTEGER NOT NULL REFERENCES accounts(account_id), card_id INTEGER NOT NULL CHECK(card_id>0), card_skin_id INTEGER NOT NULL CHECK(card_skin_id > 0), PRIMARY KEY(account_id,card_id,card_skin_id)) STRICT";
    private const string SingleSkinOwnedShopCountSql = "CREATE TABLE owned_shop_counts (account_id INTEGER NOT NULL REFERENCES accounts(account_id), parent_product_id INTEGER NOT NULL CHECK(parent_product_id IN (4001,3001001) OR parent_product_id BETWEEN 1001001 AND 1001007), count INTEGER NOT NULL, reset_timestamp INTEGER NOT NULL CHECK(reset_timestamp = 0), CHECK((parent_product_id = 4001 AND count IN (0,1)) OR (parent_product_id = 3001001 AND count=1) OR (parent_product_id BETWEEN 1001001 AND 1001007 AND count BETWEEN 1 AND 2147483647)), PRIMARY KEY(account_id,parent_product_id)) STRICT";
    private const string OwnedShopCountSql = "CREATE TABLE owned_shop_counts (account_id INTEGER NOT NULL REFERENCES accounts(account_id), parent_product_id INTEGER NOT NULL CHECK(parent_product_id BETWEEN 1 AND 2147483647), count INTEGER NOT NULL CHECK(count BETWEEN 0 AND 2147483647), reset_timestamp INTEGER NOT NULL CHECK(reset_timestamp = 0), PRIMARY KEY(account_id,parent_product_id)) STRICT";
    private static Dictionary<string, string> CurrentInventorySchema => new(LegacyCurrentInventorySchema, StringComparer.Ordinal)
    { ["owned_card_skins"] = IndependentCardSkinSql };

    private static void MigrateSkinShopSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "ALTER TABLE owned_shop_counts RENAME TO owned_shop_counts_v8;" + SingleSkinOwnedShopCountSql +
            "; INSERT INTO owned_shop_counts SELECT * FROM owned_shop_counts_v8; DROP TABLE owned_shop_counts_v8;" +
            "ALTER TABLE owned_card_skins RENAME TO owned_card_skins_v8;" + IndependentCardSkinSql +
            "; INSERT INTO owned_card_skins SELECT * FROM owned_card_skins_v8; DROP TABLE owned_card_skins_v8; PRAGMA user_version=9;");
    }

    private static void MigrateShopCatalogSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "ALTER TABLE owned_shop_counts RENAME TO owned_shop_counts_v9;" + OwnedShopCountSql +
            "; INSERT INTO owned_shop_counts SELECT * FROM owned_shop_counts_v9; DROP TABLE owned_shop_counts_v9; PRAGMA user_version=10;");
    }

    private static bool OwnsSkin(SqliteConnection connection, SqliteTransaction transaction, long id, SkinShopProduct product)
    {
        using var query = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM owned_card_skins WHERE account_id=$id AND card_id=$card AND card_skin_id=$skin)");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$card", product.CardId); query.Parameters.AddWithValue("$skin", product.CardSkinId);
        return (long)query.ExecuteScalar()! == 1;
    }

    private static string? DebitPaidCurrency(string player, int cost, out object before, out object after)
    {
        using var document = JsonDocument.Parse(player);
        var free = ReadCurrency(document.RootElement, "free_paid_coin"); var paid = ReadCurrency(document.RootElement, "paid_coin");
        if (cost < 0 || free > MaximumLocalGemBalance || paid > MaximumLocalGemBalance || free > MaximumLocalGemBalance - paid)
            throw new GachaCapacityException();
        before = new { free_paid_coin = free, paid_coin = paid }; after = before;
        if (paid < checked((ulong)cost)) return null;
        paid -= checked((ulong)cost); after = new { free_paid_coin = free, paid_coin = paid };
        var updated = JsonNode.Parse(player)!.AsObject(); updated["paid_coin"] = paid; return updated.ToJsonString();
    }

    public AccountResult CheckSkinPurchase(SkinPurchaseRequest request)
    {
        if (skinShopConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                var now = Now;
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                var product = skinShopConfig.Products.SingleOrDefault(p => p.ProductId == request.ProductId);
                if (product is null) return new(400);
                if (!HasFinishedTutorial(player) || !product.IsOpen(now) ||
                    ShopCount(connection, transaction, id, product.ParentProductId) >= product.PurchaseLimit ||
                    OwnsSkin(connection, transaction, id, product)) return new(409);
                if (DebitPaidCurrency(player, product.Price, out _, out _) is null) return new(409);
                var action = Snapshot(connection, transaction, id, player, nowOverride: now); transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    public AccountResult PurchaseSkin(SkinPurchaseRequest request)
    {
        if (skinShopConfig is null) return new(501);
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
                    return old.Route == SkinPurchaseRequest.Route && old.Semantic == request.Semantic
                        ? new(200, SkinPurchaseAction(connection, transaction, id, player, now)) : new(409);
                var product = skinShopConfig.Products.SingleOrDefault(p => p.ProductId == request.ProductId);
                if (product is null) return new(400);
                if (!HasFinishedTutorial(player) || !product.IsOpen(now) ||
                    ShopCount(connection, transaction, id, product.ParentProductId) >= product.PurchaseLimit ||
                    OwnsSkin(connection, transaction, id, product)) return new(409);
                var updated = DebitPaidCurrency(player, product.Price, out var before, out var after);
                if (updated is null) return new(409);
                SavePlayer(connection, transaction, id, updated);
                using (var skin = Command(connection, transaction, "INSERT INTO owned_card_skins(account_id,card_id,card_skin_id) VALUES($id,$card,$skin)"))
                {
                    skin.Parameters.AddWithValue("$id", id); skin.Parameters.AddWithValue("$card", product.CardId); skin.Parameters.AddWithValue("$skin", product.CardSkinId);
                    if (skin.ExecuteNonQuery() != 1) throw new GachaCapacityException();
                }
                using (var count = Command(connection, transaction, "INSERT INTO owned_shop_counts(account_id,parent_product_id,count,reset_timestamp) VALUES($id,$product,1,0) ON CONFLICT(account_id,parent_product_id) DO UPDATE SET count=count+1 WHERE count<$limit AND count<2147483647"))
                {
                    count.Parameters.AddWithValue("$id", id); count.Parameters.AddWithValue("$product", product.ParentProductId); count.Parameters.AddWithValue("$limit", product.PurchaseLimit);
                    if (count.ExecuteNonQuery() != 1) throw new GachaCapacityException();
                }
                var result = JsonSerializer.Serialize(new { source = "internal-skin-purchase", transaction_id = Guid.NewGuid().ToString("D"),
                    product_id = product.ProductId, rewards = new[] { new { item_type = product.ItemType, item_id = product.CardSkinId, item_qty = product.ItemQty } },
                    before_balance = before, after_balance = after });
                var cost = JsonSerializer.Serialize(new { item_type = product.ConsumeItemType, item_id = product.ConsumeItemId,
                    item_qty = product.Price, paid_spent = product.Price, free_spent = 0, external_payment = false });
                InsertEconomyReceipt(connection, transaction, id, retryHash, SkinPurchaseRequest.Route, request.Semantic,
                    SkinShopConfig.ExpectedFingerprint, result, cost, now);
                var action = SkinPurchaseAction(connection, transaction, id, updated, now); transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private Dictionary<string, object?> SkinPurchaseAction(SqliteConnection connection, SqliteTransaction transaction, long id, string player, long now)
    {
        var action = Snapshot(connection, transaction, id, player, nowOverride: now);
        action["is_send_direct"] = true; action["is_send_present_box"] = false; return action;
    }
}
