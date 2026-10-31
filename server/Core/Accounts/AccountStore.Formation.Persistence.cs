using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const string DeckHeaderSql = "CREATE TABLE user_decks (account_id INTEGER NOT NULL REFERENCES accounts(account_id), deck_no INTEGER NOT NULL CHECK(deck_no BETWEEN 1 AND 10), name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 64), is_main INTEGER NOT NULL CHECK(is_main IN (0,1)), PRIMARY KEY(account_id,deck_no)) STRICT";
    private const string DeckMainIndexSql = "CREATE UNIQUE INDEX ix_user_decks_main ON user_decks(account_id) WHERE is_main=1";
    private const string DeckSlotSql = "CREATE TABLE deck_slots (account_id INTEGER NOT NULL, deck_no INTEGER NOT NULL, slot_no INTEGER NOT NULL CHECK(slot_no BETWEEN 1 AND 6), card_id INTEGER NOT NULL CHECK(card_id>0), PRIMARY KEY(account_id,deck_no,slot_no), UNIQUE(account_id,deck_no,card_id), FOREIGN KEY(account_id,deck_no) REFERENCES user_decks(account_id,deck_no) ON DELETE CASCADE, FOREIGN KEY(account_id,card_id) REFERENCES owned_cards(account_id,card_id)) STRICT";
    private const string DeckEquipSql = "CREATE TABLE deck_slot_equips (account_id INTEGER NOT NULL, deck_no INTEGER NOT NULL, slot_no INTEGER NOT NULL, equip_slot_no INTEGER NOT NULL CHECK(equip_slot_no BETWEEN 1 AND 3), user_equip_id INTEGER NOT NULL REFERENCES owned_equips(user_equip_id), PRIMARY KEY(account_id,deck_no,slot_no,equip_slot_no), UNIQUE(account_id,deck_no,user_equip_id), FOREIGN KEY(account_id,deck_no,slot_no) REFERENCES deck_slots(account_id,deck_no,slot_no) ON DELETE CASCADE) STRICT";
    private const string HomePreferenceSql = "CREATE TABLE home_preferences (account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id), background_id INTEGER NOT NULL CHECK(background_id=100101), slot_1_card_id INTEGER NOT NULL CHECK(slot_1_card_id>0), slot_2_card_id INTEGER NOT NULL CHECK(slot_2_card_id>=0), slot_3_card_id INTEGER NOT NULL CHECK(slot_3_card_id>=0), slot_4_card_id INTEGER NOT NULL CHECK(slot_4_card_id>=0), slot_5_card_id INTEGER NOT NULL CHECK(slot_5_card_id>=0), slot_6_card_id INTEGER NOT NULL CHECK(slot_6_card_id>=0)) STRICT";

    private static void MigrateFormationSchema(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, DeckHeaderSql + ";" + DeckMainIndexSql + ";" + DeckSlotSql + ";" + DeckEquipSql + ";" + HomePreferenceSql + "; PRAGMA user_version=8;");

    private static Dictionary<int, OwnedFormationCard> FormationCards(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var query = Command(connection, transaction, "SELECT card_id,chara_id,awakening_count FROM owned_cards WHERE account_id=$id");
        query.Parameters.AddWithValue("$id", id); using var reader = query.ExecuteReader(); var rows = new Dictionary<int, OwnedFormationCard>();
        while (reader.Read()) rows.Add(reader.GetInt32(0), new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        return rows;
    }

    private static Dictionary<int, OwnedFormationEquip> FormationEquips(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var query = Command(connection, transaction, "SELECT user_equip_id,equip_id,is_deleted FROM owned_equips WHERE account_id=$id");
        query.Parameters.AddWithValue("$id", id); using var reader = query.ExecuteReader(); var rows = new Dictionary<int, OwnedFormationEquip>();
        while (reader.Read()) rows.Add(reader.GetInt32(0), new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) != 0));
        return rows;
    }

    private void EnsureDeckHeader(SqliteConnection connection, SqliteTransaction transaction, long id, int no)
    {
        using var insert = Command(connection, transaction,
            "INSERT INTO user_decks(account_id,deck_no,name,is_main) VALUES($id,$no,$name,0) ON CONFLICT(account_id,deck_no) DO NOTHING");
        insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$no", no);
        insert.Parameters.AddWithValue("$name", string.Format(CultureInfo.InvariantCulture, formationConfig!.DefaultDeckNameFormat, no));
        insert.ExecuteNonQuery();
    }

    private static void ReplaceDeckSlots(SqliteConnection connection, SqliteTransaction transaction, long id, int no, DeckSlot[] slots)
    {
        using (var clear = Command(connection, transaction, "DELETE FROM deck_slots WHERE account_id=$id AND deck_no=$no"))
        { clear.Parameters.AddWithValue("$id", id); clear.Parameters.AddWithValue("$no", no); clear.ExecuteNonQuery(); }
        for (var slot = 0; slot < slots.Length; slot++)
        {
            if (slots[slot].CardId == 0) continue;
            using (var insert = Command(connection, transaction, "INSERT INTO deck_slots(account_id,deck_no,slot_no,card_id) VALUES($id,$no,$slot,$card)"))
            {
                insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$no", no); insert.Parameters.AddWithValue("$slot", slot + 1);
                insert.Parameters.AddWithValue("$card", slots[slot].CardId); insert.ExecuteNonQuery();
            }
            for (var position = 0; position < 3; position++)
            {
                var equip = slots[slot].EquipIds[position]; if (equip == 0) continue;
                using var insert = Command(connection, transaction,
                    "INSERT INTO deck_slot_equips(account_id,deck_no,slot_no,equip_slot_no,user_equip_id) VALUES($id,$no,$slot,$position,$equip)");
                insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$no", no); insert.Parameters.AddWithValue("$slot", slot + 1);
                insert.Parameters.AddWithValue("$position", position + 1); insert.Parameters.AddWithValue("$equip", equip); insert.ExecuteNonQuery();
            }
        }
    }

    private static JsonElement[] DeckRows(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        var headers = new List<(int No, string Name, int Main)>();
        using (var query = Command(connection, transaction, "SELECT deck_no,name,is_main FROM user_decks WHERE account_id=$id ORDER BY deck_no"))
        {
            query.Parameters.AddWithValue("$id", id); using var reader = query.ExecuteReader();
            while (reader.Read()) headers.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        }
        var result = new List<JsonElement>();
        foreach (var header in headers)
        {
            var cards = new int[6]; var equips = Enumerable.Range(0, 6).Select(_ => new int[3]).ToArray();
            using (var query = Command(connection, transaction, "SELECT slot_no,card_id FROM deck_slots WHERE account_id=$id AND deck_no=$no"))
            {
                query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$no", header.No); using var reader = query.ExecuteReader();
                while (reader.Read()) cards[reader.GetInt32(0) - 1] = reader.GetInt32(1);
            }
            using (var query = Command(connection, transaction, "SELECT slot_no,equip_slot_no,user_equip_id FROM deck_slot_equips WHERE account_id=$id AND deck_no=$no"))
            {
                query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$no", header.No); using var reader = query.ExecuteReader();
                while (reader.Read()) equips[reader.GetInt32(0) - 1][reader.GetInt32(1) - 1] = reader.GetInt32(2);
            }
            var row = new Dictionary<string, object> { ["deck_no"] = header.No, ["name"] = header.Name, ["is_main"] = header.Main };
            for (var slot = 0; slot < 6; slot++)
            {
                row[$"slot_{slot + 1}_card_id"] = cards[slot];
                row[$"slot_{slot + 1}_user_equip_ids"] = string.Join(',', equips[slot].Select(equip => equip.ToString(CultureInfo.InvariantCulture)));
            }
            result.Add(JsonSerializer.SerializeToElement(row));
        }
        return result.ToArray();
    }

    private static JsonElement[] HomePreferenceRows(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using (var query = Command(connection, transaction,
            "SELECT json_object('background_id',background_id,'slot_1_card_id',slot_1_card_id,'slot_2_card_id',slot_2_card_id,'slot_3_card_id',slot_3_card_id,'slot_4_card_id',slot_4_card_id,'slot_5_card_id',slot_5_card_id,'slot_6_card_id',slot_6_card_id) FROM home_preferences WHERE account_id=$id"))
        {
            query.Parameters.AddWithValue("$id", id);
            if (query.ExecuteScalar() is string row) { using var json = JsonDocument.Parse(row); return [json.RootElement.Clone()]; }
        }
        using var card = Command(connection, transaction, "SELECT card_id FROM owned_cards WHERE account_id=$id ORDER BY user_card_id LIMIT 1");
        card.Parameters.AddWithValue("$id", id); var first = card.ExecuteScalar() is long value ? checked((int)value) : 0;
        return [JsonSerializer.SerializeToElement(new { background_id = 100101, slot_1_card_id = first, slot_2_card_id = 0,
            slot_3_card_id = 0, slot_4_card_id = 0, slot_5_card_id = 0, slot_6_card_id = 0 })];
    }
}
