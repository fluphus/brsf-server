using System.Globalization;
using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record ExtraShopPurchaseRequest(string RetryToken, string? AuthToken, int ContentsId, int ProductId, int Qty)
{
    public const string Route = "extra_shop/purchase";
    public string Semantic => "extra-shop:" + ContentsId.ToString(CultureInfo.InvariantCulture) + ":" +
        ProductId.ToString(CultureInfo.InvariantCulture) + ":" + Qty.ToString(CultureInfo.InvariantCulture);

    public static ExtraShopPurchaseRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "contents_id", "product_id", "qty");
        int Integer(string name)
        {
            var value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0)
                throw new InvalidDataException("Invalid ExtraShop integer field.");
            return number;
        }
        var contents = Integer("contents_id"); var product = Integer("product_id"); var qty = Integer("qty");
        if (qty > 5) throw new InvalidDataException("Unsupported ExtraShop quantity.");
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), contents, product, qty);
    }
}
