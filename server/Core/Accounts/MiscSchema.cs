using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

internal static class MiscSchema
{
    internal static readonly Dictionary<string,string> Tables = new(StringComparer.Ordinal)
    {
        ["story_reads"] = "CREATE TABLE story_reads ( account_id INTEGER NOT NULL REFERENCES accounts(account_id), story_id INTEGER NOT NULL CHECK(story_id>0), read_at INTEGER NOT NULL CHECK(read_at>0), PRIMARY KEY(account_id,story_id) ) STRICT",
        ["owned_chara_voices"] = "CREATE TABLE owned_chara_voices ( id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), chara_id INTEGER NOT NULL CHECK(chara_id>0), chara_voice_id INTEGER NOT NULL CHECK(chara_voice_id>0), play_date TEXT NOT NULL CHECK(length(play_date) BETWEEN 1 AND 32), recorded_at INTEGER NOT NULL CHECK(recorded_at>0), UNIQUE(account_id,chara_voice_id) ) STRICT",
        ["user_support_leaders"] = "CREATE TABLE user_support_leaders ( account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id), card_id INTEGER NOT NULL CHECK(card_id>0), equip_1 INTEGER REFERENCES owned_equips(user_equip_id), equip_2 INTEGER REFERENCES owned_equips(user_equip_id), equip_3 INTEGER REFERENCES owned_equips(user_equip_id), FOREIGN KEY(account_id,card_id) REFERENCES owned_cards(account_id,card_id), CHECK(equip_1 IS NULL OR equip_1>0), CHECK(equip_2 IS NULL OR equip_2>0), CHECK(equip_3 IS NULL OR equip_3>0), CHECK(equip_1 IS NULL OR equip_2 IS NULL OR equip_1<>equip_2), CHECK(equip_1 IS NULL OR equip_3 IS NULL OR equip_1<>equip_3), CHECK(equip_2 IS NULL OR equip_3 IS NULL OR equip_2<>equip_3) ) STRICT",
    };
    internal static void Create(SqliteConnection c,SqliteTransaction t)
    {
        using var q=c.CreateCommand();q.Transaction=t;q.CommandText=string.Join(';',Tables.Values)+";";q.ExecuteNonQuery();
    }
}
