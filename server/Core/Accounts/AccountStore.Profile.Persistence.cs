using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const string UserProfileSql = "CREATE TABLE user_profiles (account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(account_id), profile_birthday TEXT NOT NULL CHECK(length(profile_birthday)<=5), card_id INTEGER NOT NULL CHECK(card_id>0), title_id1 INTEGER NOT NULL CHECK(title_id1>=0), title_id2 INTEGER NOT NULL CHECK(title_id2>=0), FOREIGN KEY(account_id,card_id) REFERENCES owned_cards(account_id,card_id)) STRICT";

    private sealed record UserProfile(string Birthday, int CardId, int TitleId1, int TitleId2);

    private static UserProfile? ReadUserProfile(SqliteConnection c, SqliteTransaction t, long account)
    {
        using (var q = Command(c, t, "SELECT profile_birthday,card_id,title_id1,title_id2 FROM user_profiles WHERE account_id=$account"))
        {
            q.Parameters.AddWithValue("$account", account); using var r = q.ExecuteReader();
            if (r.Read()) return new(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
        }
        using var first = Command(c, t, "SELECT card_id FROM owned_cards WHERE account_id=$account ORDER BY user_card_id LIMIT 1");
        first.Parameters.AddWithValue("$account", account);
        return first.ExecuteScalar() is long card ? new("", checked((int)card), 0, 0) : null;
    }

    private static void SaveUserProfile(SqliteConnection c, SqliteTransaction t, long account, UserProfile profile)
    {
        using var q = Command(c, t, "INSERT INTO user_profiles(account_id,profile_birthday,card_id,title_id1,title_id2) VALUES($account,$birthday,$card,$title1,$title2) ON CONFLICT(account_id) DO UPDATE SET profile_birthday=excluded.profile_birthday,card_id=excluded.card_id,title_id1=excluded.title_id1,title_id2=excluded.title_id2");
        q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$birthday", profile.Birthday);
        q.Parameters.AddWithValue("$card", profile.CardId); q.Parameters.AddWithValue("$title1", profile.TitleId1); q.Parameters.AddWithValue("$title2", profile.TitleId2);
        if (q.ExecuteNonQuery() != 1) throw new InvalidDataException("Profile account changed.");
    }

    private JsonElement[] UserProfileRows(SqliteConnection c, SqliteTransaction t, long account)
    {
        var p = ReadUserProfile(c, t, account);
        return p is null ? [] : [JsonSerializer.SerializeToElement(new { user_id = checked((int)account), profile_birthday = p.Birthday,
            card_id = p.CardId, title_id1 = p.TitleId1, title_id2 = p.TitleId2, main_quest_id = ProfileMainQuest(c,t,account) })];
    }
}
