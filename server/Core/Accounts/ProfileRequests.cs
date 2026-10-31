using System.Text.Json;
using System.Globalization;

namespace Brs.Local.Accounts;

public sealed record ProfileTextRequest(string RetryToken, string? AuthToken, string Route, string Text)
{
    public string Semantic => MainlineFields.Semantic(Route, Text);

    public static ProfileTextRequest Read(JsonElement root, string route)
    {
        var field = route switch
        {
            "story/name_change" or "profile/name_change" => "name",
            "profile/message_change" => "message",
            _ => throw new InvalidDataException("Unsupported profile text route.")
        };
        var common = MainlineFields.Read(root, field);
        // Native text limits are applied by the profile policy in the store.
        var value = MainlineFields.String(root, field);
        return new(MainlineFields.Retry(common), MainlineFields.Auth(common), route, value);
    }
}

public sealed record ProfileBirthdayRequest(string RetryToken, string? AuthToken, string Birthday)
{
    public const string Route = "profile/birthday_set";
    public string Semantic => MainlineFields.Semantic(Route, Birthday);
    public static bool Valid(string text)
    {
        var parts = text.Split('/');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var day) && month is >= 1 and <= 12 &&
            day >= 1 && day <= DateTime.DaysInMonth(2000, month) && text == string.Create(CultureInfo.InvariantCulture, $"{month}/{day}");
    }
    public static ProfileBirthdayRequest Read(JsonElement root)
    {
        var common = MainlineFields.Read(root, "birthday"); var value = MainlineFields.String(root, "birthday");
        if (!Valid(value)) throw new InvalidDataException("Invalid profile month/day birthday.");
        return new(MainlineFields.Retry(common), MainlineFields.Auth(common), value);
    }
}

public sealed record ProfileCardRequest(string RetryToken, string? AuthToken, int CardId)
{
    public const string Route = "profile/card_change";
    public string Semantic => MainlineFields.Semantic(Route, CardId);
    public static ProfileCardRequest Read(JsonElement root)
    {
        var common = MainlineFields.Read(root, "card_id"); var id = MainlineFields.Int(root, "card_id");
        if (id <= 0) throw new InvalidDataException("Invalid profile card identity.");
        return new(MainlineFields.Retry(common), MainlineFields.Auth(common), id);
    }
}

public sealed record ProfileTitlesRequest(string RetryToken, string? AuthToken, int TitleId1, int TitleId2)
{
    public const string Route = "profile/title_set";
    public string Semantic => MainlineFields.Semantic(Route, new { title_id1 = TitleId1, title_id2 = TitleId2 });
    public static ProfileTitlesRequest Read(JsonElement root)
    {
        var common = MainlineFields.Read(root, "title_id1", "title_id2");
        var first = MainlineFields.Int(root, "title_id1"); var second = MainlineFields.Int(root, "title_id2");
        if (first < 0 || second < 0) throw new InvalidDataException("Invalid profile title identity.");
        return new(MainlineFields.Retry(common), MainlineFields.Auth(common), first, second);
    }
}
