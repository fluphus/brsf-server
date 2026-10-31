using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Brs.Local.Transport;

public enum LocalShopFailureStage { Decrypt, Json, ParseHandler }
public enum LocalShopFailureReason
{
    DecryptInvalid, JsonInvalid, CommonInvalid, UnknownOrDuplicateField, MissingField,
    AuthShapeInvalid, RetryShapeInvalid, BirthdayInvalid, TypeOrQtyInvalid, ProductIdShapeInvalid,
    ReceiptShapeInvalid, ProductNotAllowed, ProductOrReceiptNotAllowed, ContractRejected, HandlerPolicyRejected
}

public sealed class LocalShopContractException(LocalShopFailureReason reason) : Exception
{
    public LocalShopFailureReason Reason { get; } = reason;
}

public static class LocalShopDiagnostics
{
    private static readonly HashSet<string> KnownFields = new(["common", "contents_type", "qty", "product_id", "receipt", "birthday"], StringComparer.Ordinal);
    public static string Stage(LocalShopFailureStage stage) => stage switch
    {
        LocalShopFailureStage.Decrypt => "decrypt", LocalShopFailureStage.Json => "json",
        LocalShopFailureStage.ParseHandler => "parse-handler", _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    public static string Reason(LocalShopFailureReason reason) => reason switch
    {
        LocalShopFailureReason.DecryptInvalid => "decrypt_invalid", LocalShopFailureReason.JsonInvalid => "json_invalid",
        LocalShopFailureReason.CommonInvalid => "common_invalid", LocalShopFailureReason.UnknownOrDuplicateField => "unknown_or_duplicate_field",
        LocalShopFailureReason.MissingField => "missing_field", LocalShopFailureReason.AuthShapeInvalid => "auth_shape_invalid",
        LocalShopFailureReason.RetryShapeInvalid => "retry_shape_invalid", LocalShopFailureReason.BirthdayInvalid => "birthday_invalid",
        LocalShopFailureReason.TypeOrQtyInvalid => "type_or_qty_invalid", LocalShopFailureReason.ProductIdShapeInvalid => "product_id_shape_invalid",
        LocalShopFailureReason.ReceiptShapeInvalid => "receipt_shape_invalid", LocalShopFailureReason.ProductNotAllowed => "product_not_allowed",
        LocalShopFailureReason.ProductOrReceiptNotAllowed => "product_or_receipt_not_allowed", LocalShopFailureReason.ContractRejected => "contract_rejected",
        LocalShopFailureReason.HandlerPolicyRejected => "handler_policy_rejected", _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
    public static object Capture(JsonElement root)
    {
        var fields = new List<Dictionary<string, object?>>(); var count = 0;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
            {
                count++;
                if (fields.Count >= 16) continue;
                var field = new Dictionary<string, object?> { ["kind"] = property.Value.ValueKind.ToString().ToLowerInvariant() };
                if (KnownFields.Contains(property.Name)) field["name"] = property.Name;
                else
                {
                    // Unknown keys can themselves contain credentials or PII.
                    field["name_length"] = property.Name.Length; field["name_sha256"] = Digest(property.Name);
                }
                if (property.Name is "contents_type" or "qty" && property.Value.ValueKind == JsonValueKind.Number)
                {
                    if (property.Value.TryGetInt64(out var value)) field["integer"] = value;
                    else field["integer_status"] = "unrepresentable";
                }
                if (property.Name == "product_id" && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString()!;
                    if (Enumerable.Range(1, 7).Any(i => value == "paidcoin_normal_" + i)) field["value"] = value;
                    else { field["length"] = value.Length; field["sha256"] = Digest(value); }
                }
                // Never descend into common or inspect birthday/receipt values.
                fields.Add(field);
            }
        return new { root_kind = root.ValueKind.ToString().ToLowerInvariant(), fields, omitted_fields = Math.Max(0, count - fields.Count) };
    }
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
