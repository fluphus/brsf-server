using System.Globalization;
using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record CommercePurchaseRequest(string RetryToken, string? AuthToken, int ContentsType,
    string ProductId, int Qty, string? Receipt)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

    public const string Route = "shop/purchase";
    public string Semantic => Receipt is not null ? "local-claim:" + ContentsType.ToString(CultureInfo.InvariantCulture) + ":" + ProductId + ":1:daruma_dummy_receipt"
        : ContentsType == 3 && Qty == 1 ? "skin-purchase:3:" + ProductId + ":1"
        : "commerce-purchase:" + ContentsType.ToString(CultureInfo.InvariantCulture) + ":" + ProductId + ":" + Qty.ToString(CultureInfo.InvariantCulture);

    public static CommercePurchaseRequest Read(JsonElement root, bool isCheck, CommerceConfig config)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("product_id", out var field) ||
            field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: >= 1 and <= 128 } productId)
            throw new LocalShopContractException(LocalShopFailureReason.ProductIdShapeInvalid);
        var p = config.Find(productId) ?? throw new LocalShopContractException(LocalShopFailureReason.ProductNotAllowed);
        var external = p.ConsumeItemType == 99;
        var common = external && !isCheck ? EconomyRequestFields.Read(root, "contents_type", "product_id", "qty", "receipt")
            : EconomyRequestFields.Read(root, "contents_type", "product_id", "qty");
        if (root.GetProperty("contents_type").ValueKind != JsonValueKind.Number || !root.GetProperty("contents_type").TryGetInt32(out var type) || type != p.ContentsType ||
            root.GetProperty("qty").ValueKind != JsonValueKind.Number || !root.GetProperty("qty").TryGetInt32(out var qty) || qty is < 1 or > 50 || external && qty != 1)
            throw new LocalShopContractException(LocalShopFailureReason.TypeOrQtyInvalid);
        string? receipt = null;
        if (external && !isCheck)
        {
            field = root.GetProperty("receipt");
            if (field.ValueKind != JsonValueKind.String || field.GetString() != config.LocalReceiptMarker)
                throw new LocalShopContractException(LocalShopFailureReason.ReceiptShapeInvalid);
            receipt = field.GetString();
        }
        return new(isCheck ? "" : EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), type, productId, qty, receipt) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
    }
}
