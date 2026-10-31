using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private AccountResult MutateFormation(string? auth, string retry, string route, string semantic, object audit,
        Func<SqliteConnection, SqliteTransaction, long, bool> mutate)
    {
        if (formationConfig is null) return new(501);
        if (string.IsNullOrEmpty(auth)) return new(401);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            var now = Now;
            if (!TryReadAuthenticatedAccount(connection, transaction, auth, now, out var id, out var player)) return new(401);
            var hash = Hash(retry); var old = FindEconomyReceipt(connection, transaction, id, hash);
            if (old is not null) return old.Route == route && old.Semantic == semantic
                ? new(200, Snapshot(connection, transaction, id, player, nowOverride: now)) : new(409);
            if (!HasFinishedTutorial(player) || !mutate(connection, transaction, id)) return new(409);
            var frozen = JsonSerializer.Serialize(new { source = "local-formation", transaction_id = Guid.NewGuid().ToString("D"), route, audit });
            InsertEconomyReceipt(connection, transaction, id, hash, route, semantic, FormationConfig.ExpectedFingerprint, frozen, "{}", now);
            var action = Snapshot(connection, transaction, id, player, nowOverride: now); transaction.Commit(); return new(200, action);
        }
    }

    public AccountResult EditDecks(DeckEditRequest request)
    {
        if (request.TotalPower < 0 || request.Decks.Length is < 1 or > 10 || request.Decks.Any(pair => pair.Key is < 1 or > 10) ||
            request.Decks.Select(pair => pair.Key).Distinct().Count() != request.Decks.Length) return new(400);
        return MutateFormation(request.AuthToken, request.RetryToken, DeckEditRequest.Route, request.Semantic,
            new { edit_decks = request.Decks, total_power = request.TotalPower, total_power_is_client_audit_only = true },
            (connection, transaction, id) =>
            {
                var cards = FormationCards(connection, transaction, id); var equips = FormationEquips(connection, transaction, id);
                if (request.Decks.Any(pair => !FormationRules.ValidateDeck(formationConfig!, pair.Value, cards, equips))) return false;
                foreach (var pair in request.Decks.OrderBy(pair => pair.Key))
                { EnsureDeckHeader(connection, transaction, id, pair.Key); ReplaceDeckSlots(connection, transaction, id, pair.Key, pair.Value); }
                return true;
            });
    }

    public AccountResult NameDeck(DeckNameRequest request)
    {
        if (request.DeckNo is < 1 or > 10 || request.Name.Length is < 1 or > 64 || request.Name.Any(char.IsControl)) return new(400);
        return MutateFormation(request.AuthToken, request.RetryToken, DeckNameRequest.Route, request.Semantic,
            new { deck_no = request.DeckNo, name = request.Name }, (connection, transaction, id) =>
            {
                EnsureDeckHeader(connection, transaction, id, request.DeckNo);
                using var update = Command(connection, transaction, "UPDATE user_decks SET name=$name WHERE account_id=$id AND deck_no=$no");
                update.Parameters.AddWithValue("$name", request.Name); update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$no", request.DeckNo);
                return update.ExecuteNonQuery() == 1;
            });
    }

    public AccountResult SetMainDeck(DeckNumberRequest request)
    {
        if (request.DeckNo is < 1 or > 10) return new(400);
        const string route = "deck/main";
        return MutateFormation(request.AuthToken, request.RetryToken, route, route + ":" + request.DeckNo.ToString(CultureInfo.InvariantCulture),
            new { deck_no = request.DeckNo }, (connection, transaction, id) =>
            {
                using (var exists = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM deck_slots WHERE account_id=$id AND deck_no=$no)"))
                {
                    exists.Parameters.AddWithValue("$id", id); exists.Parameters.AddWithValue("$no", request.DeckNo);
                    if ((long)exists.ExecuteScalar()! != 1) return false;
                }
                using (var clear = Command(connection, transaction, "UPDATE user_decks SET is_main=0 WHERE account_id=$id AND is_main=1"))
                { clear.Parameters.AddWithValue("$id", id); clear.ExecuteNonQuery(); }
                using var set = Command(connection, transaction, "UPDATE user_decks SET is_main=1 WHERE account_id=$id AND deck_no=$no");
                set.Parameters.AddWithValue("$id", id); set.Parameters.AddWithValue("$no", request.DeckNo); return set.ExecuteNonQuery() == 1;
            });
    }

    public AccountResult ResetDeck(DeckNumberRequest request)
    {
        if (request.DeckNo is < 1 or > 10) return new(400);
        const string route = "deck/reset";
        return MutateFormation(request.AuthToken, request.RetryToken, route, route + ":" + request.DeckNo.ToString(CultureInfo.InvariantCulture),
            new { deck_no = request.DeckNo, preserve_name = true }, (connection, transaction, id) =>
            {
                using (var main = Command(connection, transaction, "SELECT is_main FROM user_decks WHERE account_id=$id AND deck_no=$no"))
                {
                    main.Parameters.AddWithValue("$id", id); main.Parameters.AddWithValue("$no", request.DeckNo);
                    if (main.ExecuteScalar() is long value && value == 1) return false;
                }
                EnsureDeckHeader(connection, transaction, id, request.DeckNo); ReplaceDeckSlots(connection, transaction, id, request.DeckNo, []);
                return true;
            });
    }

    public AccountResult CustomizeHome(HomeCustomizeRequest request)
    {
        var audit=new Dictionary<string,object?>{{"background_id",request.BackgroundId},{"card_ids",request.CardIds}};
        return MutateFormation(request.AuthToken, request.RetryToken, HomeCustomizeRequest.Route, request.Semantic,
            audit, (connection, transaction, id) =>
            {
                if (!OwnsHomeBackground(connection, transaction, id, request.BackgroundId) || !FormationRules.ValidateHome(formationConfig!, request.CardIds,
                    FormationCards(connection, transaction, id))) return false;
                var cards = request.CardIds.Concat(Enumerable.Repeat(0, 6 - request.CardIds.Length)).ToArray();
                var before=HomePreferenceRows(connection,transaction,id).Single();
                audit["changed"]=before.GetProperty("background_id").GetInt32()!=request.BackgroundId||Enumerable.Range(1,6).Any(i=>before.GetProperty($"slot_{i}_card_id").GetInt32()!=cards[i-1]);
                using var save = Command(connection, transaction,
                    "INSERT INTO home_preferences(account_id,background_id,slot_1_card_id,slot_2_card_id,slot_3_card_id,slot_4_card_id,slot_5_card_id,slot_6_card_id) VALUES($id,$bg,$a,$b,$c,$d,$e,$f) ON CONFLICT(account_id) DO UPDATE SET background_id=excluded.background_id,slot_1_card_id=excluded.slot_1_card_id,slot_2_card_id=excluded.slot_2_card_id,slot_3_card_id=excluded.slot_3_card_id,slot_4_card_id=excluded.slot_4_card_id,slot_5_card_id=excluded.slot_5_card_id,slot_6_card_id=excluded.slot_6_card_id");
                save.Parameters.AddWithValue("$id", id); save.Parameters.AddWithValue("$bg", request.BackgroundId);
                for (var position = 0; position < 6; position++) save.Parameters.AddWithValue("$" + (char)('a' + position), cards[position]);
                return save.ExecuteNonQuery() == 1;
            });
    }

    private static bool OwnsHomeBackground(SqliteConnection connection, SqliteTransaction transaction, long id, int background)
    {
        var rule=HomeBackgroundCatalog.Find(background);
        if(rule is null||rule.IsDeleted)return false;
        if(rule.IsDefault)return true;
        using var query = Command(connection, transaction, "SELECT COUNT(*) FROM owned_backgrounds WHERE account_id=$id AND background_id=$bg");
        query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$bg", background);
        return (long)query.ExecuteScalar()! == 1;
    }

    private AccountResult ReadFormation(string? auth, Func<SqliteConnection, SqliteTransaction, long, string, long, AccountResult> read)
    {
        if (formationConfig is null) return new(501);
        if (string.IsNullOrEmpty(auth)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open(); using var transaction = connection.BeginTransaction(deferred: true); var now = Now;
            if (!TryReadAuthenticatedAccount(connection, transaction, auth, now, out var id, out var player)) return new(401);
            if (!HasFinishedTutorial(player)) return new(409);
            var result = read(connection, transaction, id, player, now); transaction.Commit(); return result;
        }
    }

    public AccountResult ProfileTop(ProfileTopRequest request) => LocalBotCatalog.Known(request.UserId)?ReadLocalBotProfile(request):ReadFormation(request.AuthToken, (connection, transaction, id, player, now) =>
    {
        if (request.UserId != id) return new(403);
        var action = Snapshot(connection, transaction, id, player, nowOverride: now);
        var cards = (JsonElement[])action["u_card"]!; if (cards.Length == 0) return new(409);
        var profile = ReadUserProfile(connection, transaction, id)!;
        var card = cards.Single(c => c.GetProperty("card_id").GetInt32() == profile.CardId);
        using var json = JsonDocument.Parse(player); var user = json.RootElement;
        using var created = Command(connection, transaction, "SELECT created_at FROM accounts WHERE account_id=$id"); created.Parameters.AddWithValue("$id", id);
        action["profile_data"] = new { name = user.GetProperty("name").GetString(), rank = user.GetProperty("rank").GetInt32(),
            my_game_id = user.GetProperty("my_game_id").GetUInt64(), message = user.GetProperty("message").GetString(), profile_birthday = profile.Birthday,
            title_id1 = profile.TitleId1, title_id2 = profile.TitleId2, card_id = card.GetProperty("card_id").GetInt32(), profile_skin_id = card.GetProperty("profile_skin_id").GetInt32(),
            main_quest_id = ProfileMainQuest(connection,transaction,id), card_count = cards.Length, expedition_count = ExpeditionClaimedCount(connection,transaction,id),
            equip_count = ((JsonElement[])action["u_equip"]!).Count(equip => equip.GetProperty("is_deleted").GetInt32() == 0),
            created_at = DateTimeOffset.FromUnixTimeSeconds((long)created.ExecuteScalar()!).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };
        return new(200, action);
    });

    public AccountResult ListPresentEquips(PresentEquipListRequest request) => ReadFormation(request.AuthToken, (connection, transaction, id, player, now) =>
    {
        // Current server has no equipment-gift producer or owned equipment-gift
        // table. Held combat equipment is never substituted for mailbox gifts.
        var action = Snapshot(connection, transaction, id, player, nowOverride: now);
        action["u_present_equip"] = Array.Empty<object>(); action["unread_present_qty"] = 0; action["has_warning_receive_present"] = false;
        return new(200, action);
    });
}
