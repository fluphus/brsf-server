using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private static readonly Dictionary<string, string> InventorySchema = new(StringComparer.Ordinal)
    {
        ["owned_cards"] = "CREATE TABLE owned_cards (user_card_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(user_card_id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), card_id INTEGER NOT NULL CHECK(card_id > 0), chara_id INTEGER NOT NULL CHECK(chara_id > 0), lv INTEGER NOT NULL CHECK(lv >= 1), exp INTEGER NOT NULL CHECK(exp >= 0), mastery_lv INTEGER NOT NULL CHECK(mastery_lv >= 1), mastery_exp INTEGER NOT NULL CHECK(mastery_exp >= 0), awakening_count INTEGER NOT NULL CHECK(awakening_count >= 0), main_skill_lv INTEGER NOT NULL CHECK(main_skill_lv >= 1), kill_ability_lv INTEGER NOT NULL CHECK(kill_ability_lv >= 1), kill_zone_skill_lv INTEGER NOT NULL CHECK(kill_zone_skill_lv >= 1), card_skin_id INTEGER NOT NULL CHECK(card_skin_id > 0), home_skin_id INTEGER NOT NULL CHECK(home_skin_id > 0), profile_skin_id INTEGER NOT NULL CHECK(profile_skin_id > 0), UNIQUE(account_id,card_id)) STRICT",
        ["owned_card_skins"] = "CREATE TABLE owned_card_skins (account_id INTEGER NOT NULL, card_id INTEGER NOT NULL, card_skin_id INTEGER NOT NULL CHECK(card_skin_id > 0), PRIMARY KEY(account_id,card_id,card_skin_id), FOREIGN KEY(account_id,card_id) REFERENCES owned_cards(account_id,card_id)) STRICT",
        ["owned_equips"] = "CREATE TABLE owned_equips (user_equip_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(user_equip_id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), equip_id INTEGER NOT NULL CHECK(equip_id > 0), lv INTEGER NOT NULL CHECK(lv >= 1), exp INTEGER NOT NULL CHECK(exp >= 0), free_property_ids TEXT NOT NULL CHECK(length(free_property_ids) BETWEEN 1 AND 256), over_limit_count INTEGER NOT NULL CHECK(over_limit_count >= 0), is_lock INTEGER NOT NULL CHECK(is_lock IN (0,1)), is_deleted INTEGER NOT NULL CHECK(is_deleted IN (0,1)), created_at INTEGER NOT NULL CHECK(created_at > 0)) STRICT",
        ["acquired_equip_groups"] = "CREATE TABLE acquired_equip_groups (account_id INTEGER NOT NULL REFERENCES accounts(account_id), equip_group_id INTEGER NOT NULL CHECK(equip_group_id > 0), PRIMARY KEY(account_id,equip_group_id)) STRICT",
        ["owned_awakenings"] = "CREATE TABLE owned_awakenings (account_id INTEGER NOT NULL REFERENCES accounts(account_id), item_id INTEGER NOT NULL CHECK(item_id > 0), item_qty INTEGER NOT NULL CHECK(item_qty BETWEEN 1 AND 99999999), PRIMARY KEY(account_id,item_id)) STRICT",
        ["user_gachas"] = "CREATE TABLE user_gachas (user_gacha_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(user_gacha_id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), gacha_id INTEGER NOT NULL CHECK(gacha_id = 9999), today_once_count INTEGER NOT NULL CHECK(today_once_count >= 0), today_multi_count INTEGER NOT NULL CHECK(today_multi_count >= 0), today_special_count INTEGER NOT NULL CHECK(today_special_count >= 0), total_once_count INTEGER NOT NULL CHECK(total_once_count >= 0), total_multi_count INTEGER NOT NULL CHECK(total_multi_count >= 0), total_special_count INTEGER NOT NULL CHECK(total_special_count >= 0), last_once_date INTEGER NOT NULL CHECK(last_once_date >= 0), last_multi_date INTEGER NOT NULL CHECK(last_multi_date > 0), last_special_date INTEGER NOT NULL CHECK(last_special_date >= 0), UNIQUE(account_id,gacha_id)) STRICT",
        ["tutorial_gacha_draws"] = "CREATE TABLE tutorial_gacha_draws (account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id), retry_hash BLOB NOT NULL CHECK(typeof(retry_hash) = 'blob' AND length(retry_hash) = 32), gacha_id INTEGER NOT NULL CHECK(gacha_id = 9999), lot_id INTEGER NOT NULL CHECK(lot_id = 99991), contents_json TEXT NOT NULL CHECK(json_valid(contents_json) AND json_type(contents_json) = 'array' AND json_array_length(contents_json) = 10), created_at INTEGER NOT NULL CHECK(created_at > 0), FOREIGN KEY(account_id,retry_hash) REFERENCES tutorial_receipts(account_id,retry_hash)) STRICT"
    };

    // Player, inventory, sparse decks and stored home preferences share one
    // transaction, including create receipt replay. Defaults grant no ownership.
    private Dictionary<string, object?> Snapshot(SqliteConnection connection, SqliteTransaction transaction,
        long id, string player, string? key = null, string? value = null, long? nowOverride = null, ActivityMissionState[]? activityStates = null)
    {
        var snapshotNow = nowOverride ?? Now;
        var action = key is null ? TutorialProgression.Action(player) : PlayerState.Action(player, key, value!);
        JsonElement[] Rows(string sql, bool useCurrentTime = false)
        {
            using var query = Command(connection, transaction, sql);
            query.Parameters.AddWithValue("$id", id);
            if (useCurrentTime) query.Parameters.AddWithValue("$now", snapshotNow);
            using var reader = query.ExecuteReader();
            var result = new List<JsonElement>();
            while (reader.Read())
            {
                using var json = JsonDocument.Parse(reader.GetString(0));
                result.Add(json.RootElement.Clone());
            }
            return result.ToArray();
        }
        action["u_card"] = Rows("SELECT json_object('user_card_id',user_card_id,'card_id',card_id,'chara_id',chara_id,'lv',lv,'exp',exp,'mastery_lv',mastery_lv,'mastery_exp',mastery_exp,'awakening_count',awakening_count,'main_skill_lv',main_skill_lv,'kill_ability_lv',kill_ability_lv,'kill_zone_skill_lv',kill_zone_skill_lv,'card_skin_id',card_skin_id,'home_skin_id',home_skin_id,'profile_skin_id',profile_skin_id) FROM owned_cards WHERE account_id=$id ORDER BY user_card_id");
        action["u_card_skin"] = Rows("SELECT json_object('card_id',card_id,'card_skin_id',card_skin_id) FROM owned_card_skins WHERE account_id=$id ORDER BY card_id,card_skin_id");
        action["u_equip"] = Rows("SELECT json_object('user_equip_id',user_equip_id,'equip_id',equip_id,'lv',lv,'exp',exp,'free_property_ids',free_property_ids,'over_limit_count',over_limit_count,'is_lock',is_lock,'is_deleted',is_deleted,'created_at',strftime('%Y-%m-%d %H:%M:%S',created_at,'unixepoch','+9 hours')) FROM owned_equips WHERE account_id=$id AND equip_id NOT IN (" + string.Join(',', inventoryMaterialConfig.Materials.Select(m => m.EquipId)) + ") ORDER BY user_equip_id");
        action["u_equip_material"] = Rows("SELECT json_object('id',id,'equip_id',equip_id,'qty',qty) FROM owned_equip_materials WHERE account_id=$id ORDER BY id");
        action["u_quest"] = MainlineQuestRows(connection, transaction, id);
        action["u_equip_acquire"] = Rows("SELECT json_object('equip_group_id',equip_group_id) FROM acquired_equip_groups WHERE account_id=$id ORDER BY equip_group_id");
        action["u_item_awakening"] = Rows("SELECT json_object('item_id',item_id,'item_qty',item_qty) FROM owned_awakenings WHERE account_id=$id ORDER BY item_id");
        action["u_item_consume"] = Rows("SELECT json_object('item_id',item_id,'item_qty',item_qty) FROM owned_consumes WHERE account_id=$id ORDER BY item_id");
        action["u_gacha"] = Rows("SELECT json_object('user_gacha_id',user_gacha_id,'gacha_id',gacha_id,'today_once_count',CASE WHEN last_once_date>0 AND date(last_once_date,'unixepoch','+4 hours')=date($now,'unixepoch','+4 hours') THEN today_once_count ELSE 0 END,'today_multi_count',CASE WHEN last_multi_date>0 AND date(last_multi_date,'unixepoch','+4 hours')=date($now,'unixepoch','+4 hours') THEN today_multi_count ELSE 0 END,'today_special_count',CASE WHEN last_special_date>0 AND date(last_special_date,'unixepoch','+4 hours')=date($now,'unixepoch','+4 hours') THEN today_special_count ELSE 0 END,'total_once_count',total_once_count,'total_multi_count',total_multi_count,'total_special_count',total_special_count,'last_once_date',CASE WHEN last_once_date=0 THEN '1970-01-01 00:00:00' ELSE strftime('%Y-%m-%d %H:%M:%S',last_once_date,'unixepoch','+9 hours') END,'last_multi_date',CASE WHEN last_multi_date=0 THEN '1970-01-01 00:00:00' ELSE strftime('%Y-%m-%d %H:%M:%S',last_multi_date,'unixepoch','+9 hours') END,'last_special_date',CASE WHEN last_special_date=0 THEN '1970-01-01 00:00:00' ELSE strftime('%Y-%m-%d %H:%M:%S',last_special_date,'unixepoch','+9 hours') END) FROM user_gachas WHERE account_id=$id ORDER BY user_gacha_id", true);
        action["u_item_enhance"] = Rows("SELECT json_object('item_id',item_id,'item_qty',item_qty) FROM owned_enhances WHERE account_id=$id ORDER BY item_id");
        action["u_event_item"] = Rows("SELECT json_object('event_id',event_id,'item_id',item_id,'item_qty',item_qty) FROM owned_event_items WHERE account_id=$id ORDER BY event_id,item_id");
        action["u_background"] = Rows("SELECT json_object('background_id',background_id) FROM owned_backgrounds WHERE account_id=$id ORDER BY background_id");
        action["u_extra_shop"] = Rows("SELECT json_object('contents_id',contents_id,'product_id',product_id,'count',count) FROM owned_extra_shop_counts WHERE account_id=$id ORDER BY contents_id,product_id");
        action["u_gacha_exchange"] = Rows("SELECT json_object('user_gacha_exchange_id',user_gacha_exchange_id,'gacha_group_id',gacha_group_id,'gacha_point',gacha_point,'is_converted',is_converted) FROM user_gacha_exchanges WHERE account_id=$id ORDER BY user_gacha_exchange_id");
        action["u_shop"] = ShopRows(connection, transaction, id, snapshotNow);
        action["u_deck"] = DeckRows(connection, transaction, id);
        action["u_home_customize"] = HomePreferenceRows(connection, transaction, id);
        action["u_user_profile"] = UserProfileRows(connection, transaction, id);
        ProjectEventState(connection, transaction, id, snapshotNow, action);
        ProjectQuestModes(connection, transaction, id, snapshotNow, action);
        ProjectTrainingTa(connection,transaction,id,action);
        ProjectMisc(connection, transaction, id, player, action);
        ProjectExpedition(connection,transaction,id,action);
        var sharedActivity=beginnerConfig is not null?ActivityReadSnapshot.Load(activityConfig!,connection,transaction,id,player,snapshotNow,mainlineConfig,eventQuestConfig,questModesConfig):null;
        ProjectBeginner(connection,transaction,id,player,snapshotNow,action,sharedActivity);
        if (activityConfig is not null) ProjectActivity(new ActivityEngine(activityConfig,mainlineConfig,eventQuestConfig,questModesConfig), connection, transaction, id, player, snapshotNow, action, activityStates,sharedActivity);
        return action;
    }

    private static void EnsureInstanceCapacity(SqliteConnection connection, SqliteTransaction transaction, string table, int count)
    {
        // Callers pass only these exact source-code constants; no request table names.
        if (table is not ("owned_cards" or "owned_equips" or "user_gachas" or "user_gacha_exchanges" or "owned_equip_materials" or "quest_runs" or "user_quests" or "quest_daily_counts" or "quest_expert_cycles" or "quest_loop_sessions")) throw new InvalidDataException("Unsupported instance sequence.");
        var column = table == "owned_cards" ? "user_card_id" : table == "owned_equips" ? "user_equip_id" : table == "user_gachas" ? "user_gacha_id" : table == "user_gacha_exchanges" ? "user_gacha_exchange_id" : "id";
        using var query = Command(connection, transaction, $"SELECT MAX(value) FROM (SELECT COALESCE(MAX({column}),0) AS value FROM {table} UNION ALL SELECT COALESCE(MAX(seq),0) FROM sqlite_sequence WHERE name=$table)");
        query.Parameters.AddWithValue("$table", table);
        if ((long)query.ExecuteScalar()! > int.MaxValue - count) throw new GachaCapacityException();
    }

    private sealed class GachaCapacityException : Exception;
}
