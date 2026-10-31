using System.Globalization;
using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record ChangeCardSkinRequest(string RetryToken, string? AuthToken, int CardId, int SkinId, int ChangeSkinType)
{
    public const string Route = "card/change_skin";
    public string Semantic => "change-skin:" + CardId.ToString(CultureInfo.InvariantCulture) + ":" +
        SkinId.ToString(CultureInfo.InvariantCulture) + ":" + ChangeSkinType.ToString(CultureInfo.InvariantCulture);
    public static ChangeCardSkinRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "card_id", "skin_id", "change_skin_type");
        int Number(string key)
        {
            var value = root.GetProperty(key);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0)
                throw new InvalidDataException("Invalid card skin field.");
            return number;
        }
        var card = Number("card_id"); var skin = Number("skin_id"); var type = Number("change_skin_type");
        if (type is < 1 or > 3) throw new InvalidDataException("Invalid card skin selector.");
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), card, skin, type);
    }
}
