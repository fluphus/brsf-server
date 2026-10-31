using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const string VipEntitlementSql = "CREATE TABLE vip_entitlements (account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id), activation_id TEXT NOT NULL CHECK(length(activation_id)=36), product_id TEXT NOT NULL CHECK(product_id='vip_pass_2'), purchased_at INTEGER NOT NULL CHECK(purchased_at>0), purchased_business_day INTEGER NOT NULL CHECK(purchased_business_day>0), expires_at INTEGER NOT NULL CHECK(expires_at=purchased_at+2592000), config_hash TEXT NOT NULL CHECK(length(config_hash)=64)) STRICT";
    private const string VipPresentSql = "CREATE TABLE vip_present_items (user_present_item_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(user_present_item_id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), item_id INTEGER NOT NULL CHECK(item_id=0), item_type INTEGER NOT NULL CHECK(item_type=2), item_qty INTEGER NOT NULL CHECK(item_qty=50), receive_place INTEGER NOT NULL CHECK(receive_place=1600), condition_value INTEGER NOT NULL CHECK(condition_value=9001), is_over_qty_limit INTEGER NOT NULL CHECK(is_over_qty_limit=0), expire_timestamp INTEGER NOT NULL CHECK(expire_timestamp=0), created_at INTEGER NOT NULL CHECK(created_at>0), received_at INTEGER NOT NULL CHECK(received_at>=0)) STRICT";
    private const string VipDailySql = "CREATE TABLE vip_daily_deliveries (account_id INTEGER NOT NULL REFERENCES accounts(account_id), activation_id TEXT NOT NULL CHECK(length(activation_id)=36), business_day INTEGER NOT NULL CHECK(business_day>0), day_count INTEGER NOT NULL CHECK(day_count BETWEEN 1 AND 30), user_present_item_id INTEGER NOT NULL UNIQUE REFERENCES vip_present_items(user_present_item_id), receipt_hash BLOB NOT NULL CHECK(typeof(receipt_hash)='blob' AND length(receipt_hash)=32), created_at INTEGER NOT NULL CHECK(created_at>0), PRIMARY KEY(account_id,business_day), UNIQUE(account_id,activation_id,day_count), FOREIGN KEY(account_id,receipt_hash) REFERENCES economy_transactions(account_id,retry_hash)) STRICT";

    private static void MigrateVipSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "ALTER TABLE owned_shop_counts RENAME TO owned_shop_counts_v6;" + VipOwnedShopCountSql +
            "; INSERT INTO owned_shop_counts SELECT * FROM owned_shop_counts_v6; DROP TABLE owned_shop_counts_v6;" +
            VipEntitlementSql + ";" + VipPresentSql + ";" + VipDailySql + "; PRAGMA user_version=7;");
    }

    // Explicit compatibility policy, not recovered historical server arithmetic:
    // UTC+9 business day starts05:00; exact30*86400 lifetime; no missed-day catchup.
    private static long VipBusinessDay(long now) => checked((now + 4 * 3600) / 86400);
    private bool VipSalesOpen(long now) => DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).DateTime >=
        DateTime.ParseExact(vipPassConfig!.OpenDate, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static bool HasActiveVip(SqliteConnection connection, SqliteTransaction transaction, long id, long now)
    {
        using var query = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM vip_entitlements WHERE account_id=$id AND expires_at>$now)");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$now", now);
        return (long)query.ExecuteScalar()! == 1;
    }

    private AccountResult CheckVipPurchase(LocalPurchaseRequest request)
    {
        if (vipPassConfig is null) return new(501);
        if (request.ProductId != vipPassConfig.ProductId || request.ContentsType != 4) return new(400);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                var now = Now;
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                if (!HasFinishedTutorial(player) || ReadBirthday(player) == 0 || HasActiveVip(connection, transaction, id, now)) return new(409);
                if (!VipSalesOpen(now)) return new(409);
                _ = CreditCurrency(player, vipPassConfig.PaidQty, 0, out _, out _);
                var action = Snapshot(connection, transaction, id, player, nowOverride: now);
                transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private AccountResult ClaimVipPurchase(LocalPurchaseRequest request)
    {
        if (vipPassConfig is null) return new(501);
        if (request.ProductId != vipPassConfig.ProductId || request.ContentsType != 4 || request.Receipt != vipPassConfig.LocalReceiptMarker) return new(400);
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
                    return old.Route == LocalPurchaseRequest.Route && old.Semantic == request.Semantic
                        ? new(200, LocalPurchaseAction(connection, transaction, id, player, now)) : new(409);
                if (!HasFinishedTutorial(player) || ReadBirthday(player) == 0 || HasActiveVip(connection, transaction, id, now)) return new(409);
                if (!VipSalesOpen(now)) return new(409);
                var updated = CreditCurrency(player, vipPassConfig.PaidQty, 0, out var before, out var after);
                var expires = checked(now + vipPassConfig.LifetimeSeconds); var activation = Guid.NewGuid().ToString("D");
                var end = DateTimeOffset.FromUnixTimeSeconds(expires).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var node = JsonNode.Parse(updated)!.AsObject(); node["vip_login_bonus_end_date"] = end; node["vip_daily_quest_end_date"] = end;
                updated = node.ToJsonString(); SavePlayer(connection, transaction, id, updated);
                using (var entitlement = Command(connection, transaction,
                    "INSERT INTO vip_entitlements(account_id,activation_id,product_id,purchased_at,purchased_business_day,expires_at,config_hash) VALUES($id,$activation,'vip_pass_2',$now,$day,$end,$config) ON CONFLICT(account_id) DO UPDATE SET activation_id=excluded.activation_id,purchased_at=excluded.purchased_at,purchased_business_day=excluded.purchased_business_day,expires_at=excluded.expires_at,config_hash=excluded.config_hash"))
                {
                    entitlement.Parameters.AddWithValue("$id", id); entitlement.Parameters.AddWithValue("$activation", activation);
                    entitlement.Parameters.AddWithValue("$now", now); entitlement.Parameters.AddWithValue("$day", VipBusinessDay(now));
                    entitlement.Parameters.AddWithValue("$end", expires); entitlement.Parameters.AddWithValue("$config", VipPassConfig.ExpectedFingerprint);
                    if (entitlement.ExecuteNonQuery() != 1) throw new GachaCapacityException();
                }
                using (var counter = Command(connection, transaction,
                    "INSERT INTO owned_shop_counts(account_id,parent_product_id,count,reset_timestamp) VALUES($id,4001,1,0) ON CONFLICT(account_id,parent_product_id) DO UPDATE SET count=1"))
                { counter.Parameters.AddWithValue("$id", id); if (counter.ExecuteNonQuery() != 1) throw new GachaCapacityException(); }
                var result = JsonSerializer.Serialize(new { source = "local-vip-claim", transaction_id = activation, product_id = request.ProductId,
                    rewards = new[] { new { item_type = 1, item_id = 0, item_qty = vipPassConfig.PaidQty } },
                    purchased_at = now, expires_at = expires, purchased_business_day = VipBusinessDay(now), before_balance = before, after_balance = after });
                InsertEconomyReceipt(connection, transaction, id, retryHash, LocalPurchaseRequest.Route, request.Semantic,
                    VipPassConfig.ExpectedFingerprint, result, "{\"source\":\"local-vip-claim\",\"external_payment\":false,\"amount\":0}", now);
                var action = LocalPurchaseAction(connection, transaction, id, updated, now); transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private static JsonElement[] VipPresentRows(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var query = Command(connection, transaction,
            "SELECT json_object('user_present_item_id',user_present_item_id,'user_id',account_id,'item_id',item_id,'item_type',item_type,'item_qty',item_qty,'receive_place',receive_place,'condition_value',condition_value,'is_over_qty_limit',is_over_qty_limit,'expire_timestamp',expire_timestamp) FROM vip_present_items WHERE account_id=$id AND received_at=0 ORDER BY user_present_item_id");
        query.Parameters.AddWithValue("$id", id); using var reader = query.ExecuteReader(); var rows = new List<JsonElement>();
        while (reader.Read()) { using var json = JsonDocument.Parse(reader.GetString(0)); rows.Add(json.RootElement.Clone()); }
        return rows.ToArray();
    }

    private static void EnsureVipPresentCapacity(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var query = Command(connection, transaction,
            "SELECT MAX(value) FROM (SELECT COALESCE(MAX(user_present_item_id),0) AS value FROM vip_present_items UNION ALL SELECT COALESCE(MAX(seq),0) FROM sqlite_sequence WHERE name='vip_present_items')");
        if ((long)query.ExecuteScalar()! >= int.MaxValue) throw new GachaCapacityException();
    }

    private void DeliverVipLoginPresent(SqliteConnection connection, SqliteTransaction transaction, long id, long now)
    {
        if (vipPassConfig is null) return;
        string activation; long purchaseDay;
        using (var query = Command(connection, transaction, "SELECT activation_id,purchased_business_day FROM vip_entitlements WHERE account_id=$id AND expires_at>$now"))
        {
            query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$now", now);
            using var reader = query.ExecuteReader(); if (!reader.Read()) return;
            activation = reader.GetString(0); purchaseDay = reader.GetInt64(1);
        }
        var day = VipBusinessDay(now); var index = day - purchaseDay;
        if (index < 1 || index > vipPassConfig.Days) return;
        using (var existing = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM vip_daily_deliveries WHERE account_id=$id AND business_day=$day)"))
        {
            existing.Parameters.AddWithValue("$id", id); existing.Parameters.AddWithValue("$day", day);
            if ((long)existing.ExecuteScalar()! == 1) return;
        }
        EnsureVipPresentCapacity(connection, transaction);
        var sharedPresentId=NextPresentItemId(connection,transaction);
        using (var insert = Command(connection, transaction,
            "INSERT INTO vip_present_items(user_present_item_id,account_id,item_id,item_type,item_qty,receive_place,condition_value,is_over_qty_limit,expire_timestamp,created_at,received_at) VALUES($present,$id,0,2,50,1600,9001,0,0,$now,0)"))
        { insert.Parameters.AddWithValue("$present",sharedPresentId); insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$now", now); insert.ExecuteNonQuery(); }
        using var last = Command(connection, transaction, "SELECT last_insert_rowid()"); var present = checked((int)(long)last.ExecuteScalar()!);
        var receiptHash = RandomNumberGenerator.GetBytes(32);
        try
        {
            var result = JsonSerializer.Serialize(new { source = "vip-daily-present", user_present_item_id = present, login_bonus_id = 9001,
                day_count = index, business_day = day, item_type = 2, item_id = 0, item_qty = 50, wallet_credited = false, expire_timestamp = 0 });
            InsertEconomyReceipt(connection, transaction, id, receiptHash, "system/vip_daily_present", "vip-present:" + activation + ":" + day.ToString(CultureInfo.InvariantCulture),
                VipPassConfig.ExpectedFingerprint, result, "{\"source\":\"vip-daily-present\",\"external_payment\":false,\"amount\":0}", now);
            using var delivery = Command(connection, transaction,
                "INSERT INTO vip_daily_deliveries(account_id,activation_id,business_day,day_count,user_present_item_id,receipt_hash,created_at) VALUES($id,$activation,$day,$index,$present,$hash,$now)");
            delivery.Parameters.AddWithValue("$id", id); delivery.Parameters.AddWithValue("$activation", activation); delivery.Parameters.AddWithValue("$day", day);
            delivery.Parameters.AddWithValue("$index", index); delivery.Parameters.AddWithValue("$present", present); delivery.Parameters.AddWithValue("$hash", receiptHash); delivery.Parameters.AddWithValue("$now", now);
            delivery.ExecuteNonQuery();
        }
        finally { CryptographicOperations.ZeroMemory(receiptHash); }
        // Login action intentionally retains login_bonus=[]: the originalVIP
        // animation assumes normal1001, which this revival has not implemented.
    }
}
