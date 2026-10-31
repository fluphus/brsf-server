using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private sealed record ProfileMutation(string Player, object Audit);

    private AccountResult MutateProfile(string? auth, string retry, string route, string semantic,
        Func<SqliteConnection, SqliteTransaction, long, string, ProfileMutation?> change)
    {
        if (profileConfig is null) return new(501);
        if (string.IsNullOrEmpty(auth)) return new(401);
        lock (gate)
        {
            using var c = Open(); using var t = c.BeginTransaction(deferred: false); var now = Now;
            if (!TryReadAuthenticatedAccount(c, t, auth, now, out var account, out var player)) return new(401);
            var hash = Hash(retry); var old = FindEconomyReceipt(c, t, account, hash);
            if (old is not null) return old.Route == route && old.Semantic == semantic
                ? new(200, Snapshot(c, t, account, player, nowOverride: now)) : new(409);
            var result = change(c, t, account, player);
            if (result is null) return new(409);
            if (result.Player != player) SavePlayer(c, t, account, result.Player);
            var audit = JsonSerializer.Serialize(new { source = "local-profile", transaction_id = Guid.NewGuid().ToString("D"), route, audit = result.Audit });
            InsertEconomyReceipt(c, t, account, hash, route, semantic, ProfileConfig.ExpectedFingerprint, audit, "{}", now);
            var action = Snapshot(c, t, account, result.Player, nowOverride: now); t.Commit(); return new(200, action);
        }
    }

    public AccountResult ChangeProfileText(ProfileTextRequest request)
    {
        if (profileConfig is null) return new(501);
        var field = request.Route switch { "story/name_change" or "profile/name_change" => "name", "profile/message_change" => "message", _ => null };
        if (field is null || !profileConfig.ValidText(request.Text, field == "name")) return new(400);
        return MutateProfile(request.AuthToken, request.RetryToken, request.Route, request.Semantic, (_, _, _, player) =>
        {
            var updated = JsonNode.Parse(player)!.AsObject(); updated[field] = request.Text;
            // The request changes only the public text field. UUID, user_id,
            // sessions, birthdays and all currency/progress stay authoritative.
            return new(updated.ToJsonString(), new { field, text_sha256 = Convert.ToHexStringLower(Hash(request.Text)) });
        });
    }

    public AccountResult ChangeProfileCard(ProfileCardRequest request)
    {
        if (request.CardId <= 0) return new(400);
        return MutateProfile(request.AuthToken, request.RetryToken, ProfileCardRequest.Route, request.Semantic, (c, t, account, player) =>
        {
            var profile = ReadUserProfile(c, t, account);
            if (profile is null || !FormationCards(c, t, account).ContainsKey(request.CardId)) return null;
            SaveUserProfile(c, t, account, profile with { CardId = request.CardId });
            return new(player, new { card_id = request.CardId });
        });
    }

    public AccountResult SetProfileTitles(ProfileTitlesRequest request)
    {
        if (request.TitleId1 < 0 || request.TitleId2 < 0) return new(400);
        return MutateProfile(request.AuthToken, request.RetryToken, ProfileTitlesRequest.Route, request.Semantic, (c, t, account, player) =>
        {
            bool Owned(int title) => title == 0 || activityConfig is not null && OwnsQuestTitle(c,t,account,title);
            if (!Owned(request.TitleId1) || !Owned(request.TitleId2) || request.TitleId1 != 0 && request.TitleId1 == request.TitleId2 || ReadUserProfile(c,t,account) is not {} profile) return null;
            SaveUserProfile(c,t,account,profile with { TitleId1=request.TitleId1,TitleId2=request.TitleId2 });
            return new(player, new { title_id1 = request.TitleId1, title_id2 = request.TitleId2, changed=profile.TitleId1!=request.TitleId1||profile.TitleId2!=request.TitleId2 });
        });
    }

    public AccountResult SetProfileBirthday(ProfileBirthdayRequest request)
    {
        if (!ProfileBirthdayRequest.Valid(request.Birthday)) return new(400);
        return MutateProfile(request.AuthToken, request.RetryToken, ProfileBirthdayRequest.Route, request.Semantic, (c, t, account, player) =>
        {
            var profile = ReadUserProfile(c, t, account);
            if (profile is null || profile.Birthday.Length != 0 && profile.Birthday != request.Birthday) return null;
            SaveUserProfile(c, t, account, profile with { Birthday = request.Birthday });
            return new(player, new { profile_birthday = request.Birthday });
        });
    }
}
