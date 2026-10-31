using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const long CommerceItemCap = 99999999;
    private sealed class CommerceRejectedException : Exception;
    private sealed class CommercePlan
    {
        internal required string Player;
        internal readonly Dictionary<(int Type, int Item, int Owner), long> Quantities = [];
        internal readonly Dictionary<int, int> Cards = [];
        internal readonly Dictionary<int, int> Equips = [];
        internal readonly HashSet<int> Backgrounds = [];
        internal readonly HashSet<(int Card, int Skin)> Skins = [];
        internal readonly List<object> Conversions = [];
        internal required object Cost;
    }

    public AccountResult CheckCommercePurchase(CommercePurchaseRequest request) => ExecuteCommerce(request, true);
    public AccountResult PurchaseCommerce(CommercePurchaseRequest request) => ExecuteCommerce(request, false);

    private AccountResult ExecuteCommerce(CommercePurchaseRequest request, bool check)
    {
        if (commerceConfig is null || allGachaConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = check
                ? new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString())
                : Open();
            if (check) connection.Open();
            using var transaction = connection.BeginTransaction(deferred: check);
            try
            {
                var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now = Now;
                if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
                var product = commerceConfig.Find(request.ProductId);
                if (product is null || product.ContentsType != request.ContentsType || request.Qty is < 1 or > 50 ||
                    product.ConsumeItemType == 99 && (request.Qty != 1 || !check && request.Receipt != commerceConfig.LocalReceiptMarker) ||
                    product.ConsumeItemType != 99 && request.Receipt is not null ||
                    !check && (string.IsNullOrWhiteSpace(request.RetryToken) || request.RetryToken.Length > 256)) return new(400);
                // Legacy gems/skins keep their exact route/semantic. Config fingerprints
                // remain audit evidence and never invalidate a committed old request.
                var retry = check ? null : Hash(request.RetryToken);
                if (retry is not null && FindEconomyReceipt(connection, transaction, id, retry) is { } old)
                    return old.Route == CommercePurchaseRequest.Route && old.Semantic == request.Semantic
                        ? new(200, CommerceAction(connection, transaction, id, player, now, true, product.ConsumeItemType == 99)) : new(409);
                if (!HasFinishedTutorial(player) || !product.IsOpen(now) || product.ConsumeItemType == 99 && ReadBirthday(player) == 0 ||
                    !CommerceWithinLimit(connection, transaction, id, product, request.Qty, now) ||
                    !CommerceGroupSelected(connection, transaction, id, product, now)) return new(409);
                var plan = PrepareCommerce(connection, transaction, id, player, product, request.Qty, grantContext);
                if (check)
                {
                    var state = CommerceAction(connection, transaction, id, player, now, false);
                    transaction.Commit(); return new(200, state);
                }
                ApplyCommercePlan(connection, transaction, id, plan, now, grantContext);
                IncrementCommerceCount(connection, transaction, id, product, request.Qty, now);
                SavePlayer(connection, transaction, id, plan.Player);
                var result = JsonSerializer.Serialize(new { source = product.ConsumeItemType == 99 ? "local-free-claim" : "internal-commerce",
                    transaction_id = Guid.NewGuid().ToString("D"), contents_type = product.ContentsType,
                    product_id = product.ProductId, qty = request.Qty, rewards = product.Rewards,
                    conversions = plan.Conversions, external_payment = false });
                InsertEconomyReceipt(connection, transaction, id, retry!, CommercePurchaseRequest.Route, request.Semantic,
                    CommerceConfig.ExpectedFingerprint, result, JsonSerializer.Serialize(plan.Cost), now);
                ObserveActivityDailyClear(connection,transaction,id,plan.Player,now);
                var action = CommerceAction(connection, transaction, id, plan.Player, now, true, product.ConsumeItemType == 99);
                transaction.Commit(); return new(200, action);
            }
            catch (CommerceRejectedException) { return new(409); }
            catch (GachaCapacityException) { return new(503); }
            catch (OverflowException) { return new(503); }
        }
    }

    private CommercePlan PrepareCommerce(SqliteConnection c, SqliteTransaction t, long account, string player,
        CommerceProduct product, int qty, EquipmentGrantContext grantContext)
    {
        var wallet = JsonNode.Parse(player)!.AsObject();
        long Balance(string key) => wallet[key]?.GetValue<long>() ?? throw new GachaCapacityException();
        var paid = Balance("paid_coin"); var free = Balance("free_paid_coin");
        var gf = Balance("game_coin"); var fp = Balance("friend_pt");
        if (paid < 0 || free < 0 || gf < 0 || fp < 0 || paid + free > int.MaxValue) throw new GachaCapacityException();
        var costQty = checked(product.Price * qty);
        long paidSpent = 0, freeSpent = 0;
        var plan = new CommercePlan { Player = player, Cost = new { } };
        long Read(int type, int item, int owner = 0)
        {
            var key = (type, item, owner);
            if (!plan.Quantities.TryGetValue(key, out var current))
                plan.Quantities[key] = current = CommerceQuantity(c, t, account, type, item, owner);
            return current;
        }
        void Add(int type, int item, long delta, int owner = 0)
        {
            var value = checked(Read(type, item, owner) + delta);
            if (value < 0) throw new CommerceRejectedException();
            if (value > CommerceItemCap) throw new GachaCapacityException();
            plan.Quantities[(type, item, owner)] = value;
        }
        switch (product.ConsumeItemType)
        {
            case 1: paidSpent = costQty; if (paid < costQty) throw new CommerceRejectedException(); paid -= costQty; break;
            case 2:
                freeSpent = Math.Min(free, costQty); paidSpent = costQty - freeSpent;
                if (paid < paidSpent) throw new CommerceRejectedException(); free -= freeSpent; paid -= paidSpent; break;
            case 3: if (fp < costQty) throw new CommerceRejectedException(); fp -= costQty; break;
            case 6: Add(6, product.ConsumeItemId, -costQty); break;
            case 11: Add(11, product.ConsumeItemId, -costQty); break;
            case 14: Add(14, product.ConsumeItemId, -costQty, commerceConfig!.Owner(14, product.ConsumeItemId)); break;
            case 98:
                using (var q = Command(c, t, "SELECT gacha_point,is_converted FROM user_gacha_exchanges WHERE account_id=$id AND gacha_group_id=$group"))
                {
                    q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$group", product.GachaGroupId);
                    using var r = q.ExecuteReader();
                    if (!r.Read() || r.GetInt64(0) < costQty || r.GetInt64(1) != 0) throw new CommerceRejectedException();
                }
                break;
            case 99: break;
            default: throw new GachaCapacityException();
        }
        foreach (var reward in product.Rewards)
        {
            var n = checked(reward.ItemQty * qty);
            switch (reward.ItemType)
            {
                case 1: paid = checked(paid + n); break;
                case 2: free = checked(free + n); break;
                case 4: gf = checked(gf + n); break;
                case 5: plan.Cards[reward.ItemId] = checked(plan.Cards.GetValueOrDefault(reward.ItemId) + n); break;
                case 7: plan.Equips[reward.ItemId] = checked(plan.Equips.GetValueOrDefault(reward.ItemId) + n); break;
                case 6: case 11: case 12: Add(reward.ItemType, reward.ItemId, n); break;
                case 14: Add(14, reward.ItemId, n, commerceConfig!.Owner(14, reward.ItemId)); break;
                case 8:
                    var card = commerceConfig!.Owner(8, reward.ItemId);
                    if (n != 1 || !plan.Skins.Add((card, reward.ItemId)) || CommerceOwns(c, t, account, "owned_card_skins", "card_skin_id", reward.ItemId))
                        throw new CommerceRejectedException();
                    break;
                case 9:
                    var first = plan.Backgrounds.Add(reward.ItemId) && !CommerceOwns(c, t, account, "owned_backgrounds", "background_id", reward.ItemId);
                    var duplicates = n - (first ? 1 : 0);
                    if (duplicates > 0)
                    {
                        var converted = checked(duplicates * commerceConfig!.BackgroundDuplicateFreeQty);
                        free = checked(free + converted);
                        plan.Conversions.Add(new { from_item_type = 9, from_item_id = reward.ItemId, from_item_qty = duplicates,
                            item_type = 2, item_id = 0, item_qty = converted });
                    }
                    break;
                default: throw new GachaCapacityException();
            }
        }
        foreach (var (card, n) in plan.Cards)
        {
            if (!allGachaConfig!.Cards.Any(x => x.CardId == card)) throw new GachaCapacityException();
            var duplicateQty = n - (CommerceOwns(c, t, account, "owned_cards", "card_id", card) ? 0 : 1);
            if (Read(11, card) + duplicateQty > CommerceItemCap) throw new GachaCapacityException();
        }
        if (plan.Equips.Any(pair => CommerceEquip(pair.Key) is null)) throw new GachaCapacityException();
        EnsureInstanceCapacity(c, t, "owned_cards", plan.Cards.Count);
        EnsureEquipmentCapacity(c, t, account, plan.Equips.Select(pair => (CommerceEquip(pair.Key)!, pair.Value)), grantContext);
        if (paid > CommerceItemCap || free > CommerceItemCap || paid + free > int.MaxValue || gf > 999999999 || fp > CommerceItemCap)
            throw new GachaCapacityException();
        wallet["paid_coin"] = paid; wallet["free_paid_coin"] = free; wallet["game_coin"] = gf; wallet["friend_pt"] = fp;
        plan.Player = wallet.ToJsonString();
        plan.Cost = new { item_type = product.ConsumeItemType, item_id = product.ConsumeItemId, item_qty = costQty,
            paid_spent = paidSpent, free_spent = freeSpent, external_payment = false,
            external_price_display_only = product.ConsumeItemType == 99 ? product.Price : 0,
            gacha_group_id = product.GachaGroupId };
        return plan;
    }

    private void ApplyCommercePlan(SqliteConnection c, SqliteTransaction t, long account, CommercePlan plan, long now, EquipmentGrantContext grantContext)
    {
        foreach (var ((type, item, owner), quantity) in plan.Quantities)
        {
            var table = CommerceQuantityTable(type);
            using var q = Command(c, t, type == 14
                ? "INSERT INTO owned_event_items(account_id,event_id,item_id,item_qty) VALUES($id,$owner,$item,$qty) ON CONFLICT(account_id,event_id,item_id) DO UPDATE SET item_qty=excluded.item_qty"
                : $"INSERT INTO {table}(account_id,item_id,item_qty) VALUES($id,$item,$qty) ON CONFLICT(account_id,item_id) DO UPDATE SET item_qty=excluded.item_qty");
            q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$item", item); q.Parameters.AddWithValue("$qty", quantity);
            if (type == 14) q.Parameters.AddWithValue("$owner", owner); q.ExecuteNonQuery();
        }
        foreach (var (card, n) in plan.Cards)
            for (var i = 0; i < n; i++) GrantCard(c, t, account, allGachaConfig!.Cards.Single(x => x.CardId == card));
        foreach (var (equip, n) in plan.Equips)
        {
            var definition = CommerceEquip(equip) ?? throw new GachaCapacityException();
            if (inventoryMaterialConfig.Find(equip) is not null) GrantEquip(c, t, account, now, definition, grantContext, n);
            else for (var i = 0; i < n; i++) GrantEquip(c, t, account, now, definition, grantContext);
        }
        foreach (var background in plan.Backgrounds)
        {
            using var q = Command(c, t, "INSERT OR IGNORE INTO owned_backgrounds(account_id,background_id) VALUES($id,$item)");
            q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$item", background); q.ExecuteNonQuery();
        }
        foreach (var (card, skin) in plan.Skins)
        {
            using var q = Command(c, t, "INSERT INTO owned_card_skins(account_id,card_id,card_skin_id) VALUES($id,$card,$skin)");
            q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$card", card); q.Parameters.AddWithValue("$skin", skin); q.ExecuteNonQuery();
        }
        using var cost = JsonDocument.Parse(JsonSerializer.Serialize(plan.Cost));
        if (cost.RootElement.GetProperty("item_type").GetInt32() == 98 &&
            !DebitGachaPoint(c, t, account, cost.RootElement.GetProperty("gacha_group_id").GetInt32(), cost.RootElement.GetProperty("item_qty").GetInt32()))
            throw new CommerceRejectedException();
    }

    private static string CommerceQuantityTable(int type) => type switch
    {
        6 => "owned_consumes", 11 => "owned_awakenings", 12 => "owned_enhances", 14 => "owned_event_items",
        _ => throw new GachaCapacityException()
    };
    private GachaEquip? CommerceEquip(int equipId) => allGachaConfig!.Equips.SingleOrDefault(e => e.EquipId == equipId)
        ?? commerceConfig!.SupplementalEquips.SingleOrDefault(e => e.EquipId == equipId);
    private static long CommerceQuantity(SqliteConnection c, SqliteTransaction t, long account, int type, int item, int owner)
    {
        using var q = Command(c, t, $"SELECT item_qty FROM {CommerceQuantityTable(type)} WHERE account_id=$id AND item_id=$item" + (type == 14 ? " AND event_id=$owner" : ""));
        q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$item", item);
        if (type == 14) q.Parameters.AddWithValue("$owner", owner);
        return q.ExecuteScalar() is long value ? value : 0;
    }
    private static bool CommerceOwns(SqliteConnection c, SqliteTransaction t, long account, string table, string column, int item)
    {
        if ((table, column) is not (("owned_cards", "card_id") or ("owned_card_skins", "card_skin_id") or ("owned_backgrounds", "background_id")))
            throw new GachaCapacityException();
        using var q = Command(c, t, $"SELECT EXISTS(SELECT 1 FROM {table} WHERE account_id=$id AND {column}=$item)");
        q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$item", item); return (long)q.ExecuteScalar()! != 0;
    }

    private static long CommerceCount(SqliteConnection c, SqliteTransaction t, long account, CommerceProduct product, long now)
    {
        using var q = Command(c, t, "SELECT count,reset_timestamp FROM owned_shop_counts WHERE account_id=$id AND parent_product_id=$parent");
        q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$parent", product.ParentProductId);
        using var r = q.ExecuteReader();
        if (!r.Read()) return 0;
        var count = r.GetInt64(0); var timestamp = r.GetInt64(1);
        if (timestamp > now) throw new GachaCapacityException();
        return product.ResetType != 0 && timestamp != 0 && timestamp < CommerceDates.PeriodStart(product.ResetType, now) ? 0 : count;
    }
    private static bool CommerceWithinLimit(SqliteConnection c, SqliteTransaction t, long account, CommerceProduct product, int qty, long now)
    {
        var count = CommerceCount(c, t, account, product, now);
        return count <= int.MaxValue - qty && (product.PurchaseLimit == 0 || count <= product.PurchaseLimit - qty);
    }
    private bool CommerceGroupSelected(SqliteConnection c, SqliteTransaction t, long account, CommerceProduct product, long now)
        => !commerceConfig!.Products.Any(p => p.ContentsId == product.ContentsId && p.ProductGroup == product.ProductGroup &&
            p.GroupPriority < product.GroupPriority && p.IsOpen(now) && CommerceWithinLimit(c, t, account, p, 1, now));

    private static void IncrementCommerceCount(SqliteConnection c, SqliteTransaction t, long account, CommerceProduct p, int qty, long now)
    {
        var current = CommerceCount(c, t, account, p, now);
        using var q = Command(c, t, "INSERT INTO owned_shop_counts(account_id,parent_product_id,count,reset_timestamp) VALUES($id,$parent,$count,$period) ON CONFLICT(account_id,parent_product_id) DO UPDATE SET count=excluded.count,reset_timestamp=excluded.reset_timestamp");
        q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$parent", p.ParentProductId);
        q.Parameters.AddWithValue("$count", checked(current + qty)); q.Parameters.AddWithValue("$period", CommerceDates.PeriodStart(p.ResetType, now)); q.ExecuteNonQuery();
    }

    // Called by common Snapshot: no writes on login/list/check.
    private static JsonElement[] CommerceShopRows(SqliteConnection c, SqliteTransaction t, long account, long now, CommerceConfig? config)
    {
        var rows = RawShopRows(c, t, account, now);
        if (config is null) return rows;
        return rows.Select(row =>
        {
            var parent = row.GetProperty("parent_product_id").GetInt32();
            var product = config.Products.FirstOrDefault(p => p.ParentProductId == parent);
            var count = product is null ? row.GetProperty("count").GetInt64() : CommerceCount(c, t, account, product, now);
            return JsonSerializer.SerializeToElement(new { parent_product_id = parent, count, reset_timestamp = row.GetProperty("reset_timestamp").GetInt32() });
        }).ToArray();
    }

    private Dictionary<string, object?> CommerceAction(SqliteConnection c, SqliteTransaction t, long id, string player, long now, bool purchase, bool externalReceipt = false)
    {
        var action = Snapshot(c, t, id, player, nowOverride: now);
        action["u_shop"] = CommerceShopRows(c, t, id, now, commerceConfig);
        if (purchase) { action["is_send_direct"] = true; action["is_send_present_box"] = false; }
        // The original external purchase consumer retries a default/omitted 0.
        // This acknowledges only a committed local-provider delivery or replay.
        if (purchase && externalReceipt) action["receipt_validation_result"] = 1;
        return action;
    }
}
