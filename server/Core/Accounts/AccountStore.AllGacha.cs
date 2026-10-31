using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const int MaximumBusinessItemQuantity = 99999999;
    private const string GenericOwnedConsumeSql = "CREATE TABLE owned_consumes (account_id INTEGER NOT NULL REFERENCES accounts(account_id), item_id INTEGER NOT NULL CHECK(item_id BETWEEN 1 AND 2147483647), item_qty INTEGER NOT NULL CHECK(item_qty BETWEEN 0 AND 2147483647), PRIMARY KEY(account_id,item_id)) STRICT";
    private const string GachaExchangeSql = "CREATE TABLE user_gacha_exchanges (user_gacha_exchange_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(user_gacha_exchange_id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), gacha_group_id INTEGER NOT NULL CHECK(gacha_group_id BETWEEN 1 AND 2147483647), gacha_point INTEGER NOT NULL CHECK(gacha_point BETWEEN 0 AND 2147483647), is_converted INTEGER NOT NULL CHECK(is_converted IN (0,1)), UNIQUE(account_id,gacha_group_id)) STRICT";
    private static Dictionary<string, string> AllGachaInventorySchema => new(CurrentInventorySchema, StringComparer.Ordinal)
    {
        ["user_gachas"] = CurrentInventorySchema["user_gachas"].Replace("CHECK(gacha_id IN (9999,11012,12012,55003,55004))", "CHECK(gacha_id BETWEEN 1 AND 2147483647)", StringComparison.Ordinal),
        ["owned_awakenings"] = InventorySchema["owned_awakenings"].Replace("BETWEEN 1 AND 99999999", "BETWEEN 0 AND 2147483647", StringComparison.Ordinal),
        ["owned_equips"] = InventorySchema["owned_equips"].Replace("BETWEEN 1 AND 256", "BETWEEN 0 AND 256", StringComparison.Ordinal)
    };
    private static string AllOpenHomePreferenceSql => HomePreferenceSql.Replace("CHECK(background_id=100101)", "CHECK(background_id BETWEEN 1 AND 2147483647)", StringComparison.Ordinal);
    private const string CommerceOwnedShopSql = "CREATE TABLE owned_shop_counts (account_id INTEGER NOT NULL REFERENCES accounts(account_id), parent_product_id INTEGER NOT NULL CHECK(parent_product_id BETWEEN 1 AND 2147483647), count INTEGER NOT NULL CHECK(count BETWEEN 0 AND 2147483647), reset_timestamp INTEGER NOT NULL CHECK(reset_timestamp BETWEEN 0 AND 2147483647), PRIMARY KEY(account_id,parent_product_id)) STRICT";
    private static readonly Dictionary<string, string> CommerceInventorySchema = new(StringComparer.Ordinal)
    {
        ["owned_enhances"] = "CREATE TABLE owned_enhances (account_id INTEGER NOT NULL REFERENCES accounts(account_id), item_id INTEGER NOT NULL CHECK(item_id BETWEEN 1 AND 2147483647), item_qty INTEGER NOT NULL CHECK(item_qty BETWEEN 0 AND 2147483647), PRIMARY KEY(account_id,item_id)) STRICT",
        ["owned_event_items"] = "CREATE TABLE owned_event_items (account_id INTEGER NOT NULL REFERENCES accounts(account_id), event_id INTEGER NOT NULL CHECK(event_id BETWEEN 1 AND 2147483647), item_id INTEGER NOT NULL CHECK(item_id BETWEEN 1 AND 2147483647), item_qty INTEGER NOT NULL CHECK(item_qty BETWEEN 0 AND 2147483647), PRIMARY KEY(account_id,event_id,item_id)) STRICT",
        ["owned_backgrounds"] = "CREATE TABLE owned_backgrounds (account_id INTEGER NOT NULL REFERENCES accounts(account_id), background_id INTEGER NOT NULL CHECK(background_id BETWEEN 1 AND 2147483647), PRIMARY KEY(account_id,background_id)) STRICT",
        ["owned_extra_shop_counts"] = "CREATE TABLE owned_extra_shop_counts (account_id INTEGER NOT NULL REFERENCES accounts(account_id), contents_id INTEGER NOT NULL CHECK(contents_id BETWEEN 1 AND 2147483647), product_id INTEGER NOT NULL CHECK(product_id BETWEEN 1 AND 2147483647), count INTEGER NOT NULL CHECK(count BETWEEN 0 AND 2147483647), PRIMARY KEY(account_id,contents_id,product_id)) STRICT"
    };

    private static void MigrateAllGachaSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "ALTER TABLE user_gachas RENAME TO user_gachas_v10;" + AllGachaInventorySchema["user_gachas"] +
            "; INSERT INTO user_gachas SELECT * FROM user_gachas_v10;" +
            " INSERT INTO sqlite_sequence(name,seq) SELECT 'user_gachas',seq FROM sqlite_sequence WHERE name='user_gachas_v10' AND NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='user_gachas');" +
            " UPDATE sqlite_sequence SET seq=MAX(seq,COALESCE((SELECT seq FROM sqlite_sequence WHERE name='user_gachas_v10'),0)) WHERE name='user_gachas';" +
            " DROP TABLE user_gachas_v10; ALTER TABLE owned_consumes RENAME TO owned_consumes_v10;" + GenericOwnedConsumeSql +
            "; INSERT INTO owned_consumes SELECT * FROM owned_consumes_v10; DROP TABLE owned_consumes_v10;" + GachaExchangeSql +
            "; ALTER TABLE owned_awakenings RENAME TO owned_awakenings_v10;" + AllGachaInventorySchema["owned_awakenings"] +
            "; INSERT INTO owned_awakenings SELECT * FROM owned_awakenings_v10; DROP TABLE owned_awakenings_v10;" +
            "ALTER TABLE owned_shop_counts RENAME TO owned_shop_counts_v10;" + CommerceOwnedShopSql +
            "; INSERT INTO owned_shop_counts SELECT * FROM owned_shop_counts_v10; DROP TABLE owned_shop_counts_v10;" +
            string.Join(';', CommerceInventorySchema.Values) + "; ALTER TABLE home_preferences RENAME TO home_preferences_v10;" + AllOpenHomePreferenceSql +
            "; INSERT INTO home_preferences SELECT * FROM home_preferences_v10; DROP TABLE home_preferences_v10;" +
            // The child references owned_equips. Rebuild it under the same lock
            // before renaming its parent, keeping FK enforcement enabled.
            "CREATE TEMP TABLE migration_deck_equips AS SELECT * FROM deck_slot_equips; DROP TABLE deck_slot_equips;" +
            "ALTER TABLE owned_equips RENAME TO owned_equips_v10;" + AllGachaInventorySchema["owned_equips"] +
            "; INSERT INTO owned_equips SELECT * FROM owned_equips_v10;" +
            " INSERT INTO sqlite_sequence(name,seq) SELECT 'owned_equips',seq FROM sqlite_sequence WHERE name='owned_equips_v10' AND NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='owned_equips');" +
            " UPDATE sqlite_sequence SET seq=MAX(seq,COALESCE((SELECT seq FROM sqlite_sequence WHERE name='owned_equips_v10'),0)) WHERE name='owned_equips';" +
            " DROP TABLE owned_equips_v10;" + DeckEquipSql +
            "; INSERT INTO deck_slot_equips SELECT * FROM migration_deck_equips; DROP TABLE migration_deck_equips; PRAGMA user_version=11;");
    }

    // JST 05:00: compare the full shifted date, including month and year.
    private static DateTime GachaBusinessDate(long timestamp) => DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(4)).Date;

    private static bool GachaWithinLimits(SqliteConnection connection, SqliteTransaction transaction, long id,
        int pool, AllGachaLot lot, long now)
    {
        var suffix = lot.LotType switch { 1 => "once", 2 => "multi", 3 => "special", _ => throw new InvalidDataException("Invalid lot type.") };
        using var query = Command(connection, transaction, $"SELECT today_{suffix}_count,total_{suffix}_count,last_{suffix}_date FROM user_gachas WHERE account_id=$id AND gacha_id=$pool");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$pool", pool);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return true;
        var today = reader.GetInt64(0); var total = reader.GetInt64(1); var last = reader.GetInt64(2);
        if (today is < 0 or > int.MaxValue || total is < 0 or > int.MaxValue || last < 0) throw new GachaCapacityException();
        if (last == 0 || GachaBusinessDate(last) != GachaBusinessDate(now)) today = 0;
        return (lot.DailyMaxCount == 0 || today < lot.DailyMaxCount) && (lot.TotalMaxCount == 0 || total < lot.TotalMaxCount);
    }

    public AccountResult AllGacha(AllGachaRequest request)
    {
        if (allGachaConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now = Now;
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                var retryHash = Hash(request.RetryToken); var receipt = FindEconomyReceipt(connection, transaction, id, retryHash);
                if (receipt is not null)
                    return receipt.Route == AllGachaRequest.Route && receipt.Semantic == request.Semantic
                        ? new(200, EconomyDrawAction(connection, transaction, id, player, receipt.Result, now)) : new(409);
                if (!HasFinishedTutorial(player)) return new(409);
                var pool = allGachaConfig.Pools.SingleOrDefault(p => p.GachaId == request.GachaId);
                var lot = pool?.Lots.SingleOrDefault(l => l.LotId == request.LotId);
                if (pool is null || lot is null) return new(400);
                if (!pool.IsOpen(now) || !GachaWithinLimits(connection, transaction, id, pool.GachaId, lot, now)) return new(409);
                var costType = lot.CostType; var costItemId = lot.CostItemId; var costQty = lot.CostQty;
                if (request.IsUseGachaTicket)
                {
                    var ticket = allGachaConfig.CommonTickets.SingleOrDefault(t => t.GachaType == pool.GachaType && t.LotType == lot.LotType);
                    if (lot.CostType != 2 || ticket is null) return new(400);
                    costType = 6; costItemId = ticket.ItemId; costQty = 1;
                }
                ulong freeSpent = 0, paidSpent = 0;
                if (costType == 6)
                {
                    if (!DebitConsume(connection, transaction, id, costItemId, costQty)) return new(409);
                }
                else
                {
                    var updated = costType == 2 ? DebitCurrency(player, costQty, out freeSpent, out paidSpent)
                        : DebitPaidCurrency(player, costQty, out _, out _);
                    if (updated is null) return new(409);
                    if (costType == 1) paidSpent = checked((ulong)costQty);
                    player = updated;
                }
                var selections = pool.Draw(lot, nextGachaBelow);
                EnsureInstanceCapacity(connection, transaction, "owned_cards", selections.Count(c => c.ItemType == 5));
                EnsureEquipmentCapacity(connection, transaction, id, selections.Where(c => c.ItemType == 7)
                    .Select(c => (allGachaConfig.Equips.Single(e => e.EquipId == c.ItemId), 1)), grantContext);
                var results = selections.Select(c => c.ItemType == 5
                    ? GrantCard(connection, transaction, id, allGachaConfig.Cards.Single(card => card.CardId == c.ItemId))
                    : GrantEquip(connection, transaction, id, now, allGachaConfig.Equips.Single(equip => equip.EquipId == c.ItemId), grantContext)).ToArray();
                IncrementGachaCounter(connection, transaction, id, pool.GachaId, lot.LotType, now);
                if (pool.GachaGroupId != 0) CreditGachaPoint(connection, transaction, id, pool.GachaGroupId, selections.Length);
                SavePlayer(connection, transaction, id, player);
                var frozen = JsonSerializer.Serialize(new { lot_contents = results, lot_bonus = Array.Empty<object>() });
                var cost = JsonSerializer.Serialize(new { item_type = costType, item_id = costItemId, item_qty = costQty,
                    free_paid_coin = freeSpent, paid_coin = paidSpent, gacha_group_id = pool.GachaGroupId,
                    gacha_point_awarded = pool.GachaGroupId == 0 ? 0 : selections.Length });
                InsertEconomyReceipt(connection, transaction, id, retryHash, AllGachaRequest.Route, request.Semantic,
                    AllGachaConfig.ExpectedFingerprint, frozen, cost, now);
                ObserveActivityDailyClear(connection,transaction,id,player,now);
                var action = EconomyDrawAction(connection, transaction, id, player, frozen, now);
                transaction.Commit(); return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private static long ReadConsumeQuantity(SqliteConnection connection, SqliteTransaction transaction, long id, int itemId)
    {
        using var query = Command(connection, transaction, "SELECT item_qty FROM owned_consumes WHERE account_id=$id AND item_id=$item");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$item", itemId);
        return query.ExecuteScalar() is long qty ? qty : 0;
    }
    private static bool DebitConsume(SqliteConnection connection, SqliteTransaction transaction, long id, int itemId, int qty)
    {
        if (itemId <= 0 || qty < 0) throw new GachaCapacityException();
        var before = ReadConsumeQuantity(connection, transaction, id, itemId);
        if (before is < 0 or > MaximumBusinessItemQuantity) throw new GachaCapacityException();
        if (before < qty) return false;
        if (qty == 0) return true;
        using var update = Command(connection, transaction, "UPDATE owned_consumes SET item_qty=item_qty-$qty WHERE account_id=$id AND item_id=$item");
        update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$item", itemId); update.Parameters.AddWithValue("$qty", qty);
        if (update.ExecuteNonQuery() != 1) throw new GachaCapacityException(); return true;
    }
    private static void CreditConsume(SqliteConnection connection, SqliteTransaction transaction, long id, int itemId, int qty)
    {
        if (itemId <= 0 || qty < 0 || ReadConsumeQuantity(connection, transaction, id, itemId) > MaximumBusinessItemQuantity - (long)qty)
            throw new GachaCapacityException();
        using var update = Command(connection, transaction, "INSERT INTO owned_consumes(account_id,item_id,item_qty) VALUES($id,$item,$qty) ON CONFLICT(account_id,item_id) DO UPDATE SET item_qty=item_qty+excluded.item_qty");
        update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$item", itemId); update.Parameters.AddWithValue("$qty", qty); update.ExecuteNonQuery();
    }
    private static long ReadGachaPoint(SqliteConnection connection, SqliteTransaction transaction, long id, int group)
    {
        using var query = Command(connection, transaction, "SELECT gacha_point,is_converted FROM user_gacha_exchanges WHERE account_id=$id AND gacha_group_id=$group");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$group", group);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return 0;
        var qty = reader.GetInt64(0);
        if (reader.GetInt64(1) != 0 || qty is < 0 or > int.MaxValue) throw new GachaCapacityException();
        return qty;
    }
    private static void CreditGachaPoint(SqliteConnection connection, SqliteTransaction transaction, long id, int group, int qty)
    {
        if (group <= 0 || qty < 0 || ReadGachaPoint(connection, transaction, id, group) > int.MaxValue - (long)qty) throw new GachaCapacityException();
        using (var exists = Command(connection, transaction, "SELECT COUNT(*) FROM user_gacha_exchanges WHERE account_id=$id AND gacha_group_id=$group"))
        {
            exists.Parameters.AddWithValue("$id", id); exists.Parameters.AddWithValue("$group", group);
            if ((long)exists.ExecuteScalar()! == 0) EnsureInstanceCapacity(connection, transaction, "user_gacha_exchanges", 1);
        }
        // UPDATE first avoids burning an AUTOINCREMENT ID on each conflict.
        using var update = Command(connection, transaction, "UPDATE user_gacha_exchanges SET gacha_point=gacha_point+$qty WHERE account_id=$id AND gacha_group_id=$group");
        update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$group", group); update.Parameters.AddWithValue("$qty", qty);
        if (update.ExecuteNonQuery() == 0)
        {
            using var insert = Command(connection, transaction, "INSERT INTO user_gacha_exchanges(account_id,gacha_group_id,gacha_point,is_converted) VALUES($id,$group,$qty,0)");
            insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$group", group); insert.Parameters.AddWithValue("$qty", qty); insert.ExecuteNonQuery();
        }
    }
    private static bool DebitGachaPoint(SqliteConnection connection, SqliteTransaction transaction, long id, int group, int qty)
    {
        if (group <= 0 || qty < 0) throw new GachaCapacityException();
        if (ReadGachaPoint(connection, transaction, id, group) < qty) return false;
        if (qty == 0) return true;
        using var update = Command(connection, transaction, "UPDATE user_gacha_exchanges SET gacha_point=gacha_point-$qty WHERE account_id=$id AND gacha_group_id=$group");
        update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$group", group); update.Parameters.AddWithValue("$qty", qty);
        if (update.ExecuteNonQuery() != 1) throw new GachaCapacityException(); return true;
    }
}
