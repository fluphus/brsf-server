using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Brs.Local.Accounts;

internal static class VipRequestFields
{
    internal static int Int(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw new InvalidDataException("Invalid VIP request integer.");
        return number;
    }
    internal static bool Bool(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid VIP request boolean.");
        return value.GetBoolean();
    }
    internal static int[] IntArray(JsonElement root, string name, int max, bool nonempty = false)
    {
        var field = root.GetProperty(name);
        if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() > max || (nonempty && field.GetArrayLength() == 0))
            throw new InvalidDataException("Invalid VIP request array.");
        var result = new List<int>(); var seen = new HashSet<int>();
        foreach (var value in field.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw new InvalidDataException("Invalid VIP request array value.");
            if (!seen.Add(number)) throw new InvalidDataException("Duplicate VIP request array value.");
            result.Add(number);
        }
        return result.ToArray();
    }
}

public sealed record VipPresentListRequest(string? AuthToken, int[] ItemTypes, int Expiration, int Sort, int Order, int Page, bool NeedCount, bool NeedWarning)
{
    public static VipPresentListRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "filter_item_types", "filter_receive_expiration", "sort_type", "sort_order_type", "page_num", "is_need_unread_present_qty", "is_need_warning_receive_present");
        var types = VipRequestFields.IntArray(root, "filter_item_types", 18);
        if (types.Any(value => value is not (>= 0 and <= 15 or 98 or 99))) throw new InvalidDataException("Unknown present item type.");
        var expiration = VipRequestFields.Int(root, "filter_receive_expiration"); var sort = VipRequestFields.Int(root, "sort_type");
        var order = VipRequestFields.Int(root, "sort_order_type"); var page = VipRequestFields.Int(root, "page_num");
        if (expiration is < 0 or > 2 || sort is < 1 or > 4 || order is not (1 or 2) || page < 1)
            throw new InvalidDataException("Unknown present list policy.");
        return new(EconomyRequestFields.Auth(common), types, expiration, sort, order, page,
            VipRequestFields.Bool(root, "is_need_unread_present_qty"), VipRequestFields.Bool(root, "is_need_warning_receive_present"));
    }
}

public sealed record VipPresentReceiveRequest(string RetryToken, string? AuthToken, int[] PresentIds)
{
    public const string Route = "present/item/receive";
    public string Semantic => "vip-present-receive:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(',', PresentIds.Order().Select(value => value.ToString(CultureInfo.InvariantCulture))))));
    public static VipPresentReceiveRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "receive_user_present_item_ids");
        var ids = VipRequestFields.IntArray(root, "receive_user_present_item_ids", 50, nonempty: true);
        if (ids.Any(id => id <= 0)) throw new InvalidDataException("Invalid present item identity.");
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), ids);
    }
}

public sealed record VipLoginBonusListRequest(string? AuthToken, int[] LoginBonusIds)
{
    public static VipLoginBonusListRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "login_bonus_ids");
        var ids = VipRequestFields.IntArray(root, "login_bonus_ids", 100);
        if (ids.Any(id => id <= 0)) throw new InvalidDataException("Invalid login bonus identity.");
        return new(EconomyRequestFields.Auth(common), ids);
    }
}
