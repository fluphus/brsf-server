using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record DeckSlot(int CardId, int[] EquipIds);
internal static class FormationRequestFields
{
    internal static void Fields(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid formation object.");
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Remove(property.Name)) throw new InvalidDataException("Unknown or duplicate formation field.");
        if (allowed.Count != 0) throw new InvalidDataException("Missing formation field.");
    }
    internal static int DeckNo(JsonElement root)
    {
        var number = VipRequestFields.Int(root, "deck_no");
        if (number is < 1 or > 10) throw new InvalidDataException("Invalid deck number.");
        return number;
    }
    internal static string Semantic(string route, object payload) => route + ":" +
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    internal static int[] EquipIds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: >= 5 and <= 32 } csv)
            throw new InvalidDataException("Invalid deck equipment string.");
        var parts = csv.Split(','); if (parts.Length != 3) throw new InvalidDataException("Invalid equipment positions.");
        return parts.Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
            id >= 0 && part == id.ToString(CultureInfo.InvariantCulture) ? id : throw new InvalidDataException("Invalid equipment identity.")).ToArray();
    }
}

public sealed record DeckEditRequest(string RetryToken, string? AuthToken, KeyValuePair<int, DeckSlot[]>[] Decks, int TotalPower)
{
    public const string Route = "deck/edit";
    public string Semantic => FormationRequestFields.Semantic(Route, new { decks = Decks.OrderBy(pair => pair.Key).ToArray(), total_power = TotalPower });
    public static DeckEditRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "edit_decks", "total_power"); var total = VipRequestFields.Int(root, "total_power");
        if (total < 0) throw new InvalidDataException("Negative client deck power.");
        var edits = root.GetProperty("edit_decks"); if (edits.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid deck dictionary.");
        var result = new SortedDictionary<int, DeckSlot[]>();
        foreach (var property in edits.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var no) || no is < 1 or > 10 ||
                property.Name != no.ToString(CultureInfo.InvariantCulture) || result.ContainsKey(no)) throw new InvalidDataException("Invalid or duplicate deck key.");
            FormationRequestFields.Fields(property.Value, Enumerable.Range(1, 6).SelectMany(slot => new[] { $"slot_{slot}_card_id", $"slot_{slot}_user_equip_ids" }).ToArray());
            var slots = new List<DeckSlot>();
            for (var slot = 1; slot <= 6; slot++)
            {
                var card = VipRequestFields.Int(property.Value, $"slot_{slot}_card_id");
                if (card < 0) throw new InvalidDataException("Negative deck card identity.");
                slots.Add(new(card, FormationRequestFields.EquipIds(property.Value.GetProperty($"slot_{slot}_user_equip_ids"))));
            }
            result.Add(no, slots.ToArray());
        }
        if (result.Count is < 1 or > 10) throw new InvalidDataException("Empty or excessive deck edit.");
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), result.ToArray(), total);
    }
}

public sealed record DeckNameRequest(string RetryToken, string? AuthToken, int DeckNo, string Name)
{
    public const string Route = "deck/name";
    public string Semantic => FormationRequestFields.Semantic(Route, new { deck_no = DeckNo, name = Name });
    public static DeckNameRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "deck_no", "name"); var name = root.GetProperty("name");
        if (name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: >= 1 and <= 64 } value || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid bounded deck name.");
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), FormationRequestFields.DeckNo(root), value);
    }
}

public sealed record DeckNumberRequest(string RetryToken, string? AuthToken, int DeckNo)
{
    public static DeckNumberRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "deck_no");
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), FormationRequestFields.DeckNo(root));
    }
}

public sealed record HomeCustomizeRequest(string RetryToken, string? AuthToken, int BackgroundId, int[] CardIds)
{
    public const string Route = "home/customize";
    public string Semantic => FormationRequestFields.Semantic(Route, new { background_id = BackgroundId, card_ids = CardIds });
    public static HomeCustomizeRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "background_id", "card_ids");
        var background = VipRequestFields.Int(root, "background_id"); var ids = VipRequestFields.IntArray(root, "card_ids", 6, nonempty: true);
        if (background <= 0 || ids.Any(id => id <= 0)) throw new InvalidDataException("Invalid home ownership identity.");
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), background, ids);
    }
}

public sealed record ProfileTopRequest(string? AuthToken, int UserId)
{
    public static ProfileTopRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "user_id"); var id = VipRequestFields.Int(root, "user_id");
        if (id <= 0) throw new InvalidDataException("Invalid profile target.");
        return new(EconomyRequestFields.Auth(common), id);
    }
}

public sealed record PresentEquipListRequest(string? AuthToken)
{
    public static PresentEquipListRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "filter_rarities", "filter_equip_types", "filter_equip_skill_types", "filter_receive_expiration",
            "sort_order_type", "sort_type", "page_num", "is_need_unread_present_qty", "is_need_warning_receive_present");
        var rarity = VipRequestFields.IntArray(root, "filter_rarities", 5); var types = VipRequestFields.IntArray(root, "filter_equip_types", 8);
        var skills = VipRequestFields.IntArray(root, "filter_equip_skill_types", 5);
        if (rarity.Any(value => value is < 1 or > 5) || skills.Any(value => value is < 1 or > 5) ||
            types.Any(value => value is not (101 or 102 or 111 or 112 or 113 or 201 or 301 or 302)) ||
            VipRequestFields.Int(root, "filter_receive_expiration") is < 0 or > 2 ||
            VipRequestFields.Int(root, "sort_order_type") is not (1 or 2) || VipRequestFields.Int(root, "sort_type") is < 1 or > 4 ||
            VipRequestFields.Int(root, "page_num") is < 1 or > 20) throw new InvalidDataException("Unknown equipment gift filter.");
        _ = VipRequestFields.Bool(root, "is_need_unread_present_qty"); _ = VipRequestFields.Bool(root, "is_need_warning_receive_present");
        return new(EconomyRequestFields.Auth(common));
    }
}
