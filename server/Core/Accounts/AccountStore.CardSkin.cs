using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult ChangeCardSkin(ChangeCardSkinRequest request)
    {
        if (cardSkinConfig is null) return new(501);
        if (request.CardId <= 0 || request.SkinId <= 0 || request.ChangeSkinType is < 1 or > 3) return new(400);
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        // The selector is a closed source-code mapping, never a request column.
        var column = request.ChangeSkinType switch { 1 => "card_skin_id", 2 => "home_skin_id", 3 => "profile_skin_id", _ => throw new InvalidDataException("Invalid skin selector.") };
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            var now = Now;
            if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken, now, out var id, out var player)) return new(401);
            var hash = Hash(request.RetryToken); var old = FindEconomyReceipt(connection, transaction, id, hash);
            if (old is not null) return old.Route == ChangeCardSkinRequest.Route && old.Semantic == request.Semantic
                ? new(200, Snapshot(connection, transaction, id, player, nowOverride: now)) : new(409);
            long before;
            using (var card = Command(connection, transaction, "SELECT " + column + " FROM owned_cards WHERE account_id=$id AND card_id=$card"))
            {
                card.Parameters.AddWithValue("$id", id); card.Parameters.AddWithValue("$card", request.CardId);
                if (card.ExecuteScalar() is not long skin) return new(409);
                before = skin;
            }
            bool owned;
            using (var skin = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM owned_card_skins WHERE account_id=$id AND card_id=$card AND card_skin_id=$skin)"))
            {
                skin.Parameters.AddWithValue("$id", id); skin.Parameters.AddWithValue("$card", request.CardId); skin.Parameters.AddWithValue("$skin", request.SkinId);
                owned = (long)skin.ExecuteScalar()! == 1;
            }
            if (!cardSkinConfig.CanUseSkin(request.CardId, request.SkinId, owned)) return new(409);
            using (var update = Command(connection, transaction, "UPDATE owned_cards SET " + column + "=$skin WHERE account_id=$id AND card_id=$card"))
            {
                update.Parameters.AddWithValue("$skin", request.SkinId); update.Parameters.AddWithValue("$id", id); update.Parameters.AddWithValue("$card", request.CardId);
                if (update.ExecuteNonQuery() != 1) return new(409);
            }
            var frozen = JsonSerializer.Serialize(new { source = "local-card-skin-change", transaction_id = Guid.NewGuid().ToString("D"),
                card_id = request.CardId, skin_id = request.SkinId, change_skin_type = request.ChangeSkinType, before_skin_id = before, after_skin_id = request.SkinId });
            InsertEconomyReceipt(connection, transaction, id, hash, ChangeCardSkinRequest.Route, request.Semantic,
                CardSkinConfig.ExpectedFingerprint, frozen, "{}", now);
            var action = Snapshot(connection, transaction, id, player, nowOverride: now); transaction.Commit(); return new(200, action);
        }
    }
}
