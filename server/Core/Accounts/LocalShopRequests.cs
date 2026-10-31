using System.Globalization;
using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

internal static class EconomyRequestFields
{
    internal static JsonElement Read(JsonElement root, params string[] fields)
    {
        JsonElement common;
        try { common = RequestEnvelope.ReadCommon(root); }
        catch (InvalidDataException) { throw new LocalShopContractException(LocalShopFailureReason.CommonInvalid); }
        var allowed = fields.Append("common").ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Remove(property.Name)) throw new LocalShopContractException(LocalShopFailureReason.UnknownOrDuplicateField);
        if (allowed.Count != 0) throw new LocalShopContractException(LocalShopFailureReason.MissingField);
        return common;
    }
    internal static string? Auth(JsonElement common)
    {
        var auth = common.TryGetProperty("auth_token", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (auth?.Length > 256) throw new LocalShopContractException(LocalShopFailureReason.AuthShapeInvalid);
        return auth;
    }
    internal static string Retry(JsonElement common)
    {
        var retries = common.EnumerateObject().Where(p => p.NameEquals("retry_token")).ToArray();
        if (retries.Length != 1 || retries[0].Value.ValueKind != JsonValueKind.String ||
            retries[0].Value.GetString() is not { } token || string.IsNullOrWhiteSpace(token) || token.Length > 256)
            throw new LocalShopContractException(LocalShopFailureReason.RetryShapeInvalid);
        return token;
    }
}

public sealed record LocalShopListRequest(string? AuthToken)
{
    public static LocalShopListRequest Read(JsonElement root) => new(EconomyRequestFields.Auth(EconomyRequestFields.Read(root)));
}

public sealed record BirthdayEntryRequest(string RetryToken, string? AuthToken, string Birthday)
{
    public const string Route = "shop/birthday_entry";
    public string Semantic => "birthday:" + Birthday;
    public static BirthdayEntryRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "birthday");
        var birthday = root.GetProperty("birthday");
        if (birthday.ValueKind != JsonValueKind.String || birthday.GetString() is not { Length: 6 } value ||
            !value.All(char.IsAsciiDigit) || !DateTime.TryParseExact(value, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date.Year < 1000 || date > DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).Date)
            throw new LocalShopContractException(LocalShopFailureReason.BirthdayInvalid);
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), value);
    }
}

public sealed record LocalPurchaseRequest(string RetryToken, string? AuthToken, string ProductId, string? Receipt, int ContentsType = 1)
{
    public const string Route = "shop/purchase";
    public const string CheckRoute = "shop/purchase_check";
    public string Semantic => "local-claim:" + ContentsType.ToString(CultureInfo.InvariantCulture) + ":" + ProductId + ":1:daruma_dummy_receipt";
    public static LocalPurchaseRequest Read(JsonElement root, bool isCheck)
    {
        var common = isCheck ? EconomyRequestFields.Read(root, "contents_type", "product_id", "qty")
            : EconomyRequestFields.Read(root, "contents_type", "product_id", "qty", "receipt");
        if (root.GetProperty("contents_type").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("contents_type").TryGetInt32(out var contentsType) || contentsType is not (1 or 4) ||
            root.GetProperty("qty").ValueKind != JsonValueKind.Number || !root.GetProperty("qty").TryGetInt32(out var qty) || qty != 1)
            throw new LocalShopContractException(LocalShopFailureReason.TypeOrQtyInvalid);
        if (root.GetProperty("product_id").ValueKind != JsonValueKind.String || root.GetProperty("product_id").GetString() is not { } product ||
            product.Length is < 1 or > 128) throw new LocalShopContractException(LocalShopFailureReason.ProductIdShapeInvalid);
        string? receipt = null;
        if (!isCheck)
        {
            var field = root.GetProperty("receipt");
            if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } marker || marker.Length > 256)
                throw new LocalShopContractException(LocalShopFailureReason.ReceiptShapeInvalid);
            receipt = marker;
        }
        // The check is read-only and does not need an idempotency token.
        return new(isCheck ? "" : EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), product, receipt, contentsType);
    }
}
