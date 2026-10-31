using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    // Staged adapter: reuses original Card/Equip grant routines; no CommerceProduct, purchase count or purchase receipt.
    // A later reviewed extraction may share this planner with Commerce. This file does NOT alter frozen Commerce.
    private ActivityGrantResult GrantActivityRewards(ActivityConfig activity, SqliteConnection c, SqliteTransaction t, long account, string player, long now, CommerceReward[] rewards, EquipmentGrantContext grantContext)
    {
        if (allGachaConfig is null || commerceConfig is null) throw new InvalidDataException("Activity inventory catalog is required.");
        var wallet = JsonNode.Parse(player)!.AsObject();
        long ReadWallet(string key) => wallet[key]?.GetValue<long>() ?? throw new GachaCapacityException();
        var paid = ReadWallet("paid_coin"); var free = ReadWallet("free_paid_coin");
        var gf = ReadWallet("game_coin"); var fp = ReadWallet("friend_pt");
        if (paid < 0 || free < 0 || gf < 0 || fp < 0) throw new GachaCapacityException();
        var quantities = new Dictionary<(int Type, int Item, int Owner), long>();
        var cards = new Dictionary<int, int>(); var equips = new Dictionary<int, int>();
        var titles = new HashSet<int>(); long staminaReward = 0;
        var items = new List<object>();
        foreach (var reward in rewards)
        {
            if (reward.ItemQty <= 0) throw new InvalidDataException("Invalid mission reward quantity.");
            switch (reward.ItemType)
            {
                case 2: free = checked(free + reward.ItemQty); break;
                case 3: fp = checked(fp + reward.ItemQty); break;
                case 4: gf = checked(gf + reward.ItemQty); break;
                case 5: cards[reward.ItemId] = checked(cards.GetValueOrDefault(reward.ItemId) + reward.ItemQty); break;
                case 7: equips[reward.ItemId] = checked(equips.GetValueOrDefault(reward.ItemId) + reward.ItemQty); break;
                case 10:
                    if (reward.ItemQty != 1 || !(activity.TitleIds.Contains(reward.ItemId) || eventQuestConfig?.TitleIds.Contains(reward.ItemId) == true || questModesConfig?.TitleIds.Contains(reward.ItemId) == true || reward.ItemId==390130301)) throw new InvalidDataException("Unknown mission title.");
                    titles.Add(reward.ItemId); break;
                case 15:
                    if (reward.ItemId != 0) throw new InvalidDataException("Invalid stamina reward identity.");
                    staminaReward = checked(staminaReward + reward.ItemQty); break;
                case 6: case 11: case 12: case 14:
                    if (reward.ItemId <= 0 || !QuestRewardReference(reward.ItemType, reward.ItemId))
                        throw new InvalidDataException("Unknown typed mission reward.");
                    var owner = reward.ItemType == 14 ? QuestEventItemOwner(reward.ItemId) : 0;
                    var key = (reward.ItemType, reward.ItemId, owner);
                    if (!quantities.TryGetValue(key, out var before)) before = CommerceQuantity(c, t, account, reward.ItemType, reward.ItemId, owner);
                    var after = checked(before + reward.ItemQty);
                    if (before < 0 || after > CommerceItemCap) throw new GachaCapacityException(); quantities[key] = after; break;
                default: throw new InvalidDataException("Mission reward requires a reviewed inventory adapter.");
            }
        }
        if (paid > CommerceItemCap || free > CommerceItemCap || paid + free > int.MaxValue || gf > 999999999 || fp > CommerceItemCap)
            throw new GachaCapacityException();
        if (staminaReward > 0)
        {
            var stamina = MainlineStamina.Read(wallet, now);
            var after = checked(stamina.Current + staminaReward);
            if (after > MainlineStamina.HoldingCap) throw new GachaCapacityException();
            new MainlineStamina((int)after, stamina.Maximum, after >= stamina.Maximum ? now : stamina.Anchor).Save(wallet);
        }
        var missingCards=0;
        foreach (var (card, qty) in cards)
        {
            if (!allGachaConfig.Cards.Any(c => c.CardId == card)) throw new InvalidDataException("Missing mission card definition.");
            var exists=CommerceOwns(c,t,account,"owned_cards","card_id",card);
            if(!exists)missingCards++;
            var duplicates = qty - (exists ? 0 : 1);
            if (CommerceQuantity(c, t, account, 11, card, 0) > CommerceItemCap - duplicates) throw new GachaCapacityException();
        }
        GachaEquip? Equip(int id) => CommerceEquip(id) ?? activity.SupplementalEquips.SingleOrDefault(e => e.EquipId == id) ?? eventQuestConfig?.Data.Equips.SingleOrDefault(e => e.EquipId == id) ?? questModesConfig?.Data.Equips.SingleOrDefault(e => e.EquipId == id);
        foreach (var (equip, _) in equips) if (Equip(equip) is null) throw new InvalidDataException("Missing mission equipment definition.");
        EnsureInstanceCapacity(c, t, "owned_cards", missingCards);
        EnsureEquipmentCapacity(c, t, account, equips.Select(x => (Equip(x.Key)!, x.Value)), grantContext);
        var missingTitles = new List<int>();
        foreach (var title in titles)
        {
            using var titleQuery = Command(c, t, "SELECT COUNT(*) FROM owned_titles WHERE account_id=$id AND title_id=$title");
            titleQuery.Parameters.AddWithValue("$id", account); titleQuery.Parameters.AddWithValue("$title", title);
            if ((long)titleQuery.ExecuteScalar()! == 0) missingTitles.Add(title);
        }
        using (var capacity = Command(c, t, "SELECT MAX(value) FROM (SELECT COALESCE(MAX(id),0) AS value FROM owned_titles UNION ALL SELECT COALESCE(MAX(seq),0) FROM sqlite_sequence WHERE name='owned_titles')"))
            if ((long)capacity.ExecuteScalar()! > int.MaxValue - missingTitles.Count) throw new GachaCapacityException();
        // All preflight completed before first mutation; unexpected SQL/late failure still rolls back caller transaction.
        foreach (var ((type, item, owner), qty) in quantities)
        {
            var table = CommerceQuantityTable(type);
            using var q = Command(c, t, type == 14
                ? "INSERT INTO owned_event_items(account_id,event_id,item_id,item_qty) VALUES($id,$owner,$item,$qty) ON CONFLICT(account_id,event_id,item_id) DO UPDATE SET item_qty=excluded.item_qty"
                : $"INSERT INTO {table}(account_id,item_id,item_qty) VALUES($id,$item,$qty) ON CONFLICT(account_id,item_id) DO UPDATE SET item_qty=excluded.item_qty");
            q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$item", item); q.Parameters.AddWithValue("$qty", qty);
            if (type == 14) q.Parameters.AddWithValue("$owner", owner); q.ExecuteNonQuery();
        }
        foreach (var (card, qty) in cards) for (var i = 0; i < qty; i++) items.Add(GrantCard(c, t, account, allGachaConfig.Cards.Single(x => x.CardId == card)));
        foreach (var (equip, qty) in equips)
            if (inventoryMaterialConfig.Find(equip) is not null) items.Add(GrantEquip(c, t, account, now, Equip(equip)!, grantContext, qty));
            else for (var i = 0; i < qty; i++) items.Add(GrantEquip(c, t, account, now, Equip(equip)!, grantContext));
        foreach (var title in missingTitles)
        {
            using var q = Command(c, t, "INSERT INTO owned_titles(account_id,title_id) VALUES($id,$title)");
            q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$title", title); q.ExecuteNonQuery();
        }
        foreach (var reward in rewards.Where(r => r.ItemType is not (5 or 7)))
            items.Add(new { item_type = reward.ItemType, item_id = reward.ItemId, item_qty = reward.ItemQty });
        wallet["free_paid_coin"] = free; wallet["friend_pt"] = fp; wallet["game_coin"] = gf;
        var updated = wallet.ToJsonString(); SavePlayer(c, t, account, updated);
        return new(updated, items.ToArray());
    }
}
