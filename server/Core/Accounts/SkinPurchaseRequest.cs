using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

// Internal currency purchase has no provider receipt or billing birthday.
public sealed record SkinPurchaseRequest(string RetryToken, string? AuthToken, string ProductId)
{
    public const string Route = "shop/purchase";
    public string Semantic => "skin-purchase:3:" + ProductId + ":1";
    public static SkinPurchaseRequest Read(JsonElement root, bool isCheck)
    {
        var common = EconomyRequestFields.Read(root, "contents_type", "product_id", "qty");
        if (root.GetProperty("contents_type").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("contents_type").TryGetInt32(out var type) || type != 3 ||
            root.GetProperty("qty").ValueKind != JsonValueKind.Number || !root.GetProperty("qty").TryGetInt32(out var qty) || qty != 1)
            throw new LocalShopContractException(LocalShopFailureReason.TypeOrQtyInvalid);
        if (root.GetProperty("product_id").ValueKind != JsonValueKind.String ||
            root.GetProperty("product_id").GetString() is not { Length: >= 1 and <= 128 } product)
            throw new LocalShopContractException(LocalShopFailureReason.ProductIdShapeInvalid);
        return new(isCheck ? "" : EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), product);
    }
}
