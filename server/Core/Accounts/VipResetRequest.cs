using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record VipResetRequest(string? AuthToken, string[] ProductIds)
{
    public static VipResetRequest Read(JsonElement root)
    {
        var common = RequestEnvelope.ReadCommon(root);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name is not ("common" or "product_ids"))
                throw new InvalidDataException("Duplicate or unsupported VIP reset request field.");
        if (!root.TryGetProperty("product_ids", out var products) || products.ValueKind != JsonValueKind.Array ||
            products.GetArrayLength() > 16)
            throw new InvalidDataException("Bounded VIP reset product array required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var product in products.EnumerateArray())
        {
            if (product.ValueKind != JsonValueKind.String || product.GetString() is not { } id ||
                id is not ("vip_pass_1" or "vip_pass_2"))
                throw new InvalidDataException("Unsupported VIP reset product.");
            ids.Add(id);
        }
        var token = common.TryGetProperty("auth_token", out var auth) && auth.ValueKind == JsonValueKind.String
            ? auth.GetString() : null;
        if (token?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        // Native VipPassReset uses CommonResponse and supplies no endpoint-specific
        // retry contract. This idempotent operation needs no receipt or new schema.
        return new VipResetRequest(token, ids.ToArray());
    }
}
