using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult TutorialGacha(TutorialGachaRequest request)
    {
        if (gachaConfig is null) return new(501);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long id; string player;
                using (var session = Command(connection, transaction, "SELECT a.account_id,a.player_json FROM sessions s JOIN accounts a ON a.account_id=s.account_id WHERE s.token_hash=$hash AND s.expires_at>$now"))
                {
                    session.Parameters.AddWithValue("$hash", Hash(request.AuthToken)); session.Parameters.AddWithValue("$now", now);
                    using var reader = session.ExecuteReader();
                    if (!reader.Read()) return new(401);
                    id = reader.GetInt64(0); player = reader.GetString(1);
                }
                var retryHash = Hash(request.RetryToken);
                using (var receipt = Command(connection, transaction, "SELECT route,semantic FROM tutorial_receipts WHERE account_id=$id AND retry_hash=$hash"))
                {
                    receipt.Parameters.AddWithValue("$id", id); receipt.Parameters.AddWithValue("$hash", retryHash);
                    using var reader = receipt.ExecuteReader();
                    if (reader.Read())
                    {
                        var match = reader.GetString(0) == TutorialGachaRequest.Route && reader.GetString(1) == TutorialGachaRequest.Semantic;
                        reader.Close();
                        if (!match) return new(409);
                        using var draw = Command(connection, transaction, "SELECT contents_json FROM tutorial_gacha_draws WHERE account_id=$id AND retry_hash=$hash");
                        draw.Parameters.AddWithValue("$id", id); draw.Parameters.AddWithValue("$hash", retryHash);
                        return draw.ExecuteScalar() is string frozen ? new(200, DrawAction(connection, transaction, id, player, frozen)) : new(503);
                    }
                }
                using var document = JsonDocument.Parse(player);
                var current = TutorialProgression.ReadStep(document.RootElement);
                var next = current switch { 1200 => 1300, 1250 => 1350, 1260 => 1360, _ => 0 };
                if (next == 0) return new(409);
                if(FindEconomyReceipt(connection,transaction,id,retryHash)is not null)return new(409);
                EnsureCompleteTutorialStarter(connection,transaction,id,player,now, grantContext);
                using (var existing = Command(connection, transaction, "SELECT COUNT(*) FROM tutorial_gacha_draws WHERE account_id=$id"))
                {
                    existing.Parameters.AddWithValue("$id", id);
                    if ((long)existing.ExecuteScalar()! != 0) return new(409);
                }
                using (var count = Command(connection, transaction, "SELECT COUNT(*) FROM tutorial_receipts WHERE account_id=$id"))
                {
                    count.Parameters.AddWithValue("$id", id);
                    if ((long)count.ExecuteScalar()! >= MaximumTutorialReceiptsPerAccount) return new(503);
                }
                var selections = gachaConfig.Draw(nextGachaBelow);
                EnsureInstanceCapacity(connection, transaction, "owned_cards", selections.Count(c => c.ItemType == 5));
                EnsureEquipmentCapacity(connection, transaction, id, selections.Where(c => c.ItemType == 7)
                    .Select(c => (gachaConfig.Equips.Single(e => e.EquipId == c.ItemId), 1)), grantContext);
                EnsureInstanceCapacity(connection, transaction, "user_gachas", 1);
                var results = new List<Dictionary<string, object?>>();
                foreach (var selection in selections)
                    results.Add(selection.ItemType == 5
                        ? GrantCard(connection, transaction, id, gachaConfig.Cards.Single(c => c.CardId == selection.ItemId))
                        : GrantEquip(connection, transaction, id, now, gachaConfig.Equips.Single(e => e.EquipId == selection.ItemId), grantContext));
                if(current==1200) EnsurePhoneTutorialMainWeapon(connection, transaction, id, now, grantContext);
                var frozenJson = JsonSerializer.Serialize(results);
                using (var counters = Command(connection, transaction, "INSERT INTO user_gachas(account_id,gacha_id,today_once_count,today_multi_count,today_special_count,total_once_count,total_multi_count,total_special_count,last_once_date,last_multi_date,last_special_date) VALUES($id,9999,0,1,0,0,1,0,0,$now,0)"))
                {
                    counters.Parameters.AddWithValue("$id", id); counters.Parameters.AddWithValue("$now", now); counters.ExecuteNonQuery();
                }
                player = TutorialProgression.SetStep(player, next, now);
                using (var update = Command(connection, transaction, "UPDATE accounts SET player_json=$player WHERE account_id=$id"))
                {
                    update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$player", player);
                    if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Gacha account changed.");
                }
                // The shared retry receipt precedes its FK-linked immutable result;
                // any subsequent failure rolls back both and all inventory grants.
                using (var receipt = Command(connection, transaction, "INSERT INTO tutorial_receipts(account_id,retry_hash,route,semantic,result_step,created_at) VALUES($id,$hash,'tutorial/gacha_result','gacha:9999:99991:1:false',$step,$now)"))
                {
                    receipt.Parameters.AddWithValue("$id", id); receipt.Parameters.AddWithValue("$hash", retryHash);
                    receipt.Parameters.AddWithValue("$step", next); receipt.Parameters.AddWithValue("$now", now); receipt.ExecuteNonQuery();
                }
                using (var draw = Command(connection, transaction, "INSERT INTO tutorial_gacha_draws(account_id,retry_hash,gacha_id,lot_id,contents_json,created_at) VALUES($id,$hash,9999,99991,$contents,$now)"))
                {
                    draw.Parameters.AddWithValue("$id", id); draw.Parameters.AddWithValue("$hash", retryHash);
                    draw.Parameters.AddWithValue("$contents", frozenJson); draw.Parameters.AddWithValue("$now", now); draw.ExecuteNonQuery();
                }
                var action = DrawAction(connection, transaction, id, player, frozenJson);
                transaction.Commit();
                return new(200, action);
            }
            catch (GachaCapacityException) { return new(503); }
        }
    }

    private Dictionary<string, object?> DrawAction(SqliteConnection connection, SqliteTransaction transaction,
        long id, string player, string contents)
    {
        var action = Snapshot(connection, transaction, id, player);
        using var result = JsonDocument.Parse(contents);
        action["lot_contents"] = result.RootElement.Clone();
        action["lot_bonus"] = Array.Empty<object>();
        return action;
    }

    private static Dictionary<string, object?> Lot(int type, int id, bool isNew, object? before = null) => new()
    { ["item_type"] = type, ["item_id"] = id, ["item_qty"] = 1, ["is_new"] = isNew, ["before"] = before };

    private static Dictionary<string, object?> GrantCard(SqliteConnection connection, SqliteTransaction transaction,
        long account, GachaCard card)
    {
        var cardId = card.CardId;
        using (var owned = Command(connection, transaction, "SELECT COUNT(*) FROM owned_cards WHERE account_id=$id AND card_id=$card"))
        {
            owned.Parameters.AddWithValue("$id", account); owned.Parameters.AddWithValue("$card", cardId);
            if ((long)owned.ExecuteScalar()! != 0)
            {
                using var booster = Command(connection, transaction, "INSERT INTO owned_awakenings(account_id,item_id,item_qty) VALUES($id,$card,1) ON CONFLICT(account_id,item_id) DO UPDATE SET item_qty=item_qty+1 WHERE item_qty<99999999");
                booster.Parameters.AddWithValue("$id", account); booster.Parameters.AddWithValue("$card", cardId);
                if (booster.ExecuteNonQuery() != 1) throw new GachaCapacityException();
                return Lot(11, cardId, false, Lot(5, cardId, false));
            }
        }
        using (var insert = Command(connection, transaction, "INSERT INTO owned_cards(account_id,card_id,chara_id,lv,exp,mastery_lv,mastery_exp,awakening_count,main_skill_lv,kill_ability_lv,kill_zone_skill_lv,card_skin_id,home_skin_id,profile_skin_id) VALUES($id,$card,$chara,1,0,1,0,0,1,1,1,$skin,$skin,$skin)"))
        {
            insert.Parameters.AddWithValue("$id", account); insert.Parameters.AddWithValue("$card", cardId);
            insert.Parameters.AddWithValue("$chara", card.CharaId); insert.Parameters.AddWithValue("$skin", card.DefaultSkinId); insert.ExecuteNonQuery();
        }
        using (var skin = Command(connection, transaction, "INSERT INTO owned_card_skins(account_id,card_id,card_skin_id) VALUES($id,$card,$skin)"))
        {
            skin.Parameters.AddWithValue("$id", account); skin.Parameters.AddWithValue("$card", cardId);
            skin.Parameters.AddWithValue("$skin", card.DefaultSkinId); skin.ExecuteNonQuery();
        }
        return Lot(5, cardId, true);
    }

    private Dictionary<string, object?> GrantEquip(SqliteConnection connection, SqliteTransaction transaction,
        long account, long now, GachaEquip equip, EquipmentGrantContext grantContext, int qty = 1)
    {
        if (inventoryMaterialConfig.Find(equip.EquipId) is { } material)
        {
            if (equip != material.Equipment) throw new InvalidDataException("Material equipment definition differs from its original mapping.");
            return CreditEquipMaterial(connection, transaction, account, equip.EquipId, qty);
        }
        if (qty != 1) throw new InvalidDataException("Ordinary equipment grants create individual instances.");
        // Final guard also covers late quest mission/event grants and future
        // producers that bypass a batch planner. Caller owns atomic rollback.
        if (ActiveOrdinaryEquipmentCount(connection, transaction, account) >= OrdinaryEquipmentCapacity)
            return grantContext.Overflow is { } overflow
                ? QueueBattleEquipmentOverflow(connection, transaction, account, now, equip, overflow)
                : throw new GachaCapacityException();
        EnsureInstanceCapacity(connection, transaction, "owned_equips", 1);
        var equipId = equip.EquipId;
        int acquired;
        using (var group = Command(connection, transaction, "INSERT OR IGNORE INTO acquired_equip_groups(account_id,equip_group_id) VALUES($id,$group)"))
        {
            group.Parameters.AddWithValue("$id", account); group.Parameters.AddWithValue("$group", equip.EquipGroupId); acquired = group.ExecuteNonQuery();
        }
        var isLocked = AutomaticEquipmentLock(equip, acquired == 1, grantContext.Options);
        using (var insert = Command(connection, transaction, "INSERT INTO owned_equips(account_id,equip_id,lv,exp,free_property_ids,over_limit_count,is_lock,is_deleted,created_at) VALUES($id,$equip,1,0,$properties,0,$locked,0,$now)"))
        {
            insert.Parameters.AddWithValue("$id", account); insert.Parameters.AddWithValue("$equip", equipId);
            insert.Parameters.AddWithValue("$locked", isLocked ? 1 : 0);
            insert.Parameters.AddWithValue("$properties", equip.FreePropertyIds); insert.Parameters.AddWithValue("$now", now); insert.ExecuteNonQuery();
        }
        return Lot(7, equipId, acquired == 1);
    }
}
