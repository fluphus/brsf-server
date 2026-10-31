using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const string LegacyOwnedShopCountSql = "CREATE TABLE owned_shop_counts (account_id INTEGER NOT NULL REFERENCES accounts(account_id), parent_product_id INTEGER NOT NULL CHECK(parent_product_id BETWEEN 1001001 AND 1001007), count INTEGER NOT NULL CHECK(count BETWEEN 1 AND 2147483647), reset_timestamp INTEGER NOT NULL CHECK(reset_timestamp = 0), PRIMARY KEY(account_id,parent_product_id)) STRICT";
    private const string VipOwnedShopCountSql = "CREATE TABLE owned_shop_counts (account_id INTEGER NOT NULL REFERENCES accounts(account_id), parent_product_id INTEGER NOT NULL CHECK(parent_product_id = 4001 OR parent_product_id BETWEEN 1001001 AND 1001007), count INTEGER NOT NULL, reset_timestamp INTEGER NOT NULL CHECK(reset_timestamp = 0), CHECK((parent_product_id = 4001 AND count IN (0,1)) OR (parent_product_id BETWEEN 1001001 AND 1001007 AND count BETWEEN 1 AND 2147483647)), PRIMARY KEY(account_id,parent_product_id)) STRICT";

    private static JsonElement[] RawShopRows(SqliteConnection connection, SqliteTransaction transaction, long id, long now)
    {
        using var query = Command(connection, transaction,
            "SELECT json_object('parent_product_id',parent_product_id,'count',CASE WHEN parent_product_id=4001 THEN CASE WHEN EXISTS(SELECT 1 FROM vip_entitlements v WHERE v.account_id=$id AND v.expires_at>$now) THEN 1 ELSE 0 END ELSE count END,'reset_timestamp',reset_timestamp) FROM owned_shop_counts WHERE account_id=$id ORDER BY parent_product_id");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$now", now); using var reader = query.ExecuteReader();
        var result = new List<JsonElement>();
        while (reader.Read()) { using var json = JsonDocument.Parse(reader.GetString(0)); result.Add(json.RootElement.Clone()); }
        return result.ToArray();
    }

    private JsonElement[] ShopRows(SqliteConnection connection, SqliteTransaction transaction, long id, long now)
        => CommerceShopRows(connection, transaction, id, now, commerceConfig);

    private static long ShopCount(SqliteConnection connection, SqliteTransaction transaction, long id, int product)
    {
        using var query = Command(connection, transaction,
            "SELECT count FROM owned_shop_counts WHERE account_id=$id AND parent_product_id=$product");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$product", product);
        return query.ExecuteScalar() is long count ? count : 0;
    }

    private static int ReadBirthday(string player)
    {
        using var document = JsonDocument.Parse(player);
        if (!document.RootElement.TryGetProperty("birthday", out var birthday) || birthday.ValueKind != JsonValueKind.Number ||
            !birthday.TryGetInt32(out var result) || result < 0) throw new GachaCapacityException();
        return result;
    }

    private static string CreditCurrency(string player, LocalPurchaseProduct product, out object before, out object after)
        => CreditCurrency(player, product.PaidQty, product.FreeQty, out before, out after);

    private static string CreditCurrency(string player, int paidQty, int freeQty, out object before, out object after)
    {
        if (paidQty < 0 || freeQty < 0) throw new GachaCapacityException();
        using var document = JsonDocument.Parse(player);
        var free = ReadCurrency(document.RootElement, "free_paid_coin"); var paid = ReadCurrency(document.RootElement, "paid_coin");
        if (free > ulong.MaxValue - checked((ulong)freeQty) || paid > ulong.MaxValue - checked((ulong)paidQty))
            throw new GachaCapacityException();
        before = new { free_paid_coin = free, paid_coin = paid };
        var newFree = free + checked((ulong)freeQty); var newPaid = paid + checked((ulong)paidQty);
        if (newFree > MaximumLocalGemBalance || newPaid > MaximumLocalGemBalance || newFree > MaximumLocalGemBalance - newPaid)
            throw new GachaCapacityException();
        after = new { free_paid_coin = newFree, paid_coin = newPaid };
        var updated = JsonNode.Parse(player)!.AsObject(); updated["free_paid_coin"] = newFree; updated["paid_coin"] = newPaid;
        return updated.ToJsonString();
    }

    public AccountResult LocalShopList(LocalShopListRequest request)
    {
        if (localPurchaseConfig is null && vipPassConfig is null && skinShopConfig is null && commerceConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
            var now = Now;
            if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
            if (!HasFinishedTutorial(player)) return new(409);
            var action = Snapshot(connection, transaction, id, player, nowOverride: now); transaction.Commit(); return new(200, action);
        }
    }

    public AccountResult EnterBirthday(BirthdayEntryRequest request)
    {
        if (localPurchaseConfig is null && vipPassConfig is null && commerceConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                var retryHash = Hash(request.RetryToken); var old = FindEconomyReceipt(connection, transaction, id, retryHash);
                if (old is not null)
                    return old.Route == BirthdayEntryRequest.Route && old.Semantic == request.Semantic
                        ? new(200, Snapshot(connection, transaction, id, player)) : new(409);
                if (!HasFinishedTutorial(player)) return new(409);
                var birthday = int.Parse(request.Birthday, CultureInfo.InvariantCulture); var registered = ReadBirthday(player);
                if (registered != 0 && registered != birthday) return new(409);
                var updated = JsonNode.Parse(player)!.AsObject(); updated["birthday"] = birthday; player = updated.ToJsonString();
                SavePlayer(connection, transaction, id, player);
                var result = JsonSerializer.Serialize(new { source = "local-birthday-entry", transaction_id = Guid.NewGuid().ToString("D"), birthday });
                InsertEconomyReceipt(connection, transaction, id, retryHash, BirthdayEntryRequest.Route, request.Semantic,
                    localPurchaseConfig is not null ? LocalPurchaseConfig.ExpectedFingerprint : VipPassConfig.ExpectedFingerprint, result, "{}", now);
                var action = Snapshot(connection, transaction, id, player); transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    public AccountResult CheckLocalPurchase(LocalPurchaseRequest request)
    {
        if (request.ContentsType == 4) return CheckVipPurchase(request);
        if (request.ContentsType != 1) return new(400);
        if (localPurchaseConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                if (!HasFinishedTutorial(player) || ReadBirthday(player) == 0) return new(409);
                var product = localPurchaseConfig.Products.SingleOrDefault(p => p.ProductId == request.ProductId);
                if (product is null) return new(400);
                if (!product.IsOpen(now)) return new(409);
                _ = CreditCurrency(player, product, out _, out _);
                if (ShopCount(connection, transaction, id, product.ParentProductId) >= int.MaxValue) return new(503);
                var action = Snapshot(connection, transaction, id, player); transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    public AccountResult ClaimLocalPurchase(LocalPurchaseRequest request)
    {
        if (request.ContentsType == 4) return ClaimVipPurchase(request);
        if (request.ContentsType != 1) return new(400);
        if (localPurchaseConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        // This marker is the original local provider's continuation token. It is
        // never validated externally or used as the transaction identity.
        if (request.Receipt != localPurchaseConfig.LocalReceiptMarker) return new(400);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                var retryHash = Hash(request.RetryToken); var old = FindEconomyReceipt(connection, transaction, id, retryHash);
                if (old is not null)
                    return old.Route == LocalPurchaseRequest.Route && old.Semantic == request.Semantic
                        ? new(200, LocalPurchaseAction(connection, transaction, id, player)) : new(409);
                if (!HasFinishedTutorial(player) || ReadBirthday(player) == 0) return new(409);
                var product = localPurchaseConfig.Products.SingleOrDefault(p => p.ProductId == request.ProductId);
                if (product is null) return new(400);
                if (!product.IsOpen(now)) return new(409);
                var updated = CreditCurrency(player, product, out var before, out var after);
                var count = ShopCount(connection, transaction, id, product.ParentProductId);
                if (count >= int.MaxValue) return new(503);
                SavePlayer(connection, transaction, id, updated);
                using (var counter = Command(connection, transaction,
                    "INSERT INTO owned_shop_counts(account_id,parent_product_id,count,reset_timestamp) VALUES($id,$product,1,0) ON CONFLICT(account_id,parent_product_id) DO UPDATE SET count=count+1 WHERE count<2147483647"))
                {
                    counter.Parameters.AddWithValue("$id", id); counter.Parameters.AddWithValue("$product", product.ParentProductId);
                    if (counter.ExecuteNonQuery() != 1) throw new GachaCapacityException();
                }
                var rewards = new List<object> { new { item_type = 1, item_id = 0, item_qty = product.PaidQty } };
                if (product.FreeQty > 0) rewards.Add(new { item_type = 2, item_id = 0, item_qty = product.FreeQty });
                var result = JsonSerializer.Serialize(new { source = "local-claim", transaction_id = Guid.NewGuid().ToString("D"),
                    product_id = product.ProductId, rewards, before_balance = before, after_balance = after });
                var cost = JsonSerializer.Serialize(new { source = "local-claim", external_payment = false, amount = 0 });
                InsertEconomyReceipt(connection, transaction, id, retryHash, LocalPurchaseRequest.Route, request.Semantic,
                    LocalPurchaseConfig.ExpectedFingerprint, result, cost, now);
                var action = LocalPurchaseAction(connection, transaction, id, updated); transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private Dictionary<string, object?> LocalPurchaseAction(SqliteConnection connection, SqliteTransaction transaction, long id, string player, long? now = null)
    {
        var action = Snapshot(connection, transaction, id, player, nowOverride: now);
        action["receipt_validation_result"] = 1; // Native success enum for local delivery only.
        action["is_send_direct"] = true; action["is_send_present_box"] = false; return action;
    }
}
