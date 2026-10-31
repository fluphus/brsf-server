using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private sealed record MainlineReward(int Type, int Item, int Qty, int Rarity, bool Vip = false);

    private List<MainlineReward> DrawMainlineRewards(MainlineQuest quest, MainlineConfig config)
    {
        var rewards = new List<MainlineReward>();
        foreach (var drop in config.Drops.Where(d => d.QuestId == quest.QuestId).OrderBy(d => d.Id))
        {
            var frames = config.Frames.Where(f => f.DropFrameId == drop.DropFrameId).ToArray();
            var items = config.DropItems.Where(i => i.DropItemId == drop.DropItemId).ToArray();
            var count = GachaWeights.Pick(frames, f => f.Weight, nextGachaBelow ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32).FrameCount;
            for (var n = 0; n < count; n++)
            {
                var item = GachaWeights.Pick(items, i => i.Weight, nextGachaBelow ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32);
                rewards.Add(new(item.ItemType, item.ItemId, item.ItemQty, drop.Rarity));
            }
        }
        return rewards;
    }

    private object ApplyMainlineReward(SqliteConnection c, SqliteTransaction t, long account, JsonObject player, MainlineReward reward, long now, MainlineConfig config, EquipmentGrantContext grantContext)
    {
        if (reward.Qty <= 0) throw new GachaCapacityException();
        var isNew = false;
        switch (reward.Type)
        {
            case 2:
                AddMainlineBalance(player, "free_paid_coin", reward.Qty, CommerceItemCap);
                break;
            case 4:
                AddMainlineBalance(player, "game_coin", reward.Qty, 999999999);
                break;
            case 12:
                if (!config.EnhanceIds.Contains(reward.Item)) throw new GachaCapacityException();
                var before = CommerceQuantity(c, t, account, 12, reward.Item, 0);
                if (before > CommerceItemCap - reward.Qty) throw new GachaCapacityException();
                using (var seen = Command(c, t, "SELECT EXISTS(SELECT 1 FROM owned_enhances WHERE account_id=$account AND item_id=$item)"))
                {
                    seen.Parameters.AddWithValue("$account", account); seen.Parameters.AddWithValue("$item", reward.Item);
                    isNew = (long)seen.ExecuteScalar()! == 0;
                }
                using (var q = Command(c, t, "INSERT INTO owned_enhances(account_id,item_id,item_qty) VALUES($account,$item,$qty) ON CONFLICT(account_id,item_id) DO UPDATE SET item_qty=excluded.item_qty"))
                {
                    q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$item", reward.Item);
                    q.Parameters.AddWithValue("$qty", before + reward.Qty); q.ExecuteNonQuery();
                }
                break;
            case 7:
                var equip = config.Equips.SingleOrDefault(e => e.EquipId == reward.Item) ?? throw new GachaCapacityException();
                if (inventoryMaterialConfig.Find(equip.EquipId) is not null)
                    isNew = (bool)GrantEquip(c, t, account, now, equip, grantContext, reward.Qty)["is_new"]!;
                else
                    for (var n = 0; n < reward.Qty; n++) isNew |= (bool)GrantEquip(c, t, account, now, equip, grantContext)["is_new"]!;
                break;
            case 3: case 6: case 10: case 11: case 14:
                if (activityConfig is null) throw new GachaCapacityException();
                isNew = reward.Type switch { 6 => CommerceQuantity(c,t,account,6,reward.Item,0)==0, 10 => !OwnsQuestTitle(c,t,account,reward.Item), 11 => CommerceQuantity(c,t,account,11,reward.Item,0)==0, 14 => CommerceQuantity(c,t,account,14,reward.Item,QuestEventItemOwner(reward.Item))==0,_=>false };
                ApplyQuestTypedRewards(c,t,account,player,[new(reward.Type,reward.Item,reward.Qty)],now, grantContext);
                break;
            default: throw new GachaCapacityException();
        }
        return new { item_id = reward.Item, item_type = reward.Type, item_qty = reward.Qty, rarity = reward.Rarity, is_new = isNew, is_vip = reward.Vip };
    }

    private static void AddMainlineBalance(JsonObject player, string field, long quantity, long cap)
    {
        using var current = JsonDocument.Parse(player.ToJsonString()); var before = ReadCurrency(current.RootElement, field);
        if (quantity < 0 || before > (ulong)cap || (ulong)quantity > (ulong)cap - before) throw new GachaCapacityException();
        player[field] = before + (ulong)quantity;
        var free = ReadCurrency(JsonSerializer.SerializeToElement(player), "free_paid_coin");
        var paid = ReadCurrency(JsonSerializer.SerializeToElement(player), "paid_coin");
        if (paid > MaximumLocalGemBalance || free > MaximumLocalGemBalance - paid) throw new GachaCapacityException();
    }

    private void ApplyMainlineExperience(SqliteConnection c, SqliteTransaction t, long account, JsonObject wallet,
        int userExp, int masteryExp, int[] cards, long now)
    {
        var cfg = mainlineConfig!;
        var oldRank = wallet["rank"]?.GetValue<int>() ?? throw new GachaCapacityException();
        var oldExp = wallet["exp"]?.GetValue<int>() ?? throw new GachaCapacityException();
        if (oldRank is < 1 or > 100 || oldExp < 0 || oldExp > cfg.Ranks[^1].Exp) throw new GachaCapacityException();
        var afterExp = Math.Min(cfg.Ranks[^1].Exp, checked((long)oldExp + userExp));
        var rank = Math.Max(oldRank, cfg.Ranks.Last(r => r.Exp <= afterExp).Rank);
        var stamina = MainlineStamina.Read(wallet, now);
        foreach (var earned in cfg.Ranks.Where(r => r.Rank > oldRank && r.Rank <= rank))
        {
            if (earned.ItemType == 2) AddMainlineBalance(wallet, "free_paid_coin", earned.ItemQty, CommerceItemCap);
            // Explicit local reconstruction: each new rank adds its original
            // StaminaMax, capped at 999, without rejecting the completed fight.
            var restored = (int)Math.Min(MainlineStamina.HoldingCap, (long)stamina.Current + earned.StaminaMax);
            stamina = new(restored, earned.StaminaMax, restored >= earned.StaminaMax ? now : stamina.Anchor);
        }
        stamina.Save(wallet); wallet["rank"] = rank; wallet["exp"] = (int)afterExp;
        foreach (var card in cards.Distinct())
        {
            int oldLv, oldMasteryExp;
            using (var q = Command(c, t, "SELECT mastery_lv,mastery_exp FROM owned_cards WHERE account_id=$account AND card_id=$card"))
            {
                q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$card", card); using var r = q.ExecuteReader();
                if (!r.Read()) throw new MainlineRejectedException(); oldLv = r.GetInt32(0); oldMasteryExp = r.GetInt32(1);
            }
            if (oldLv is < 1 or > 5 || oldMasteryExp < 0 || oldMasteryExp > cfg.MasteryLevels[^1].MasteryExp) throw new GachaCapacityException();
            var exp = (int)Math.Min(cfg.MasteryLevels[^1].MasteryExp, checked((long)oldMasteryExp + masteryExp));
            var derived = cfg.MasteryLevels.Where(m => m.MasteryExp <= exp).Select(m => m.MasteryLv).DefaultIfEmpty(0).Max();
            // Historical local cards were initialized lv1/exp0. Preserve that
            // level, without inventing EXP or downgrading existing ownership.
            var lv = Math.Min(5, Math.Max(oldLv, derived));
            using var update = Command(c, t, "UPDATE owned_cards SET mastery_lv=$lv,mastery_exp=$exp WHERE account_id=$account AND card_id=$card");
            update.Parameters.AddWithValue("$lv", lv); update.Parameters.AddWithValue("$exp", exp);
            update.Parameters.AddWithValue("$account", account); update.Parameters.AddWithValue("$card", card); update.ExecuteNonQuery();
        }
    }
}
