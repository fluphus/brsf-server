using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record HomeShopListRequest(string? AuthToken)
{
    public static HomeShopListRequest Read(JsonElement root)
    {
        var common = RequestEnvelope.ReadCommon(root);
        // Native RequestShopList supplies RequestBase only; shared retry/options
        // do not authorize prices, product selections or purchase state changes.
        foreach (var property in root.EnumerateObject())
            if (!property.NameEquals("common"))
                throw new InvalidDataException("Unsupported home shop list request field.");
        var token = common.TryGetProperty("auth_token", out var auth) && auth.ValueKind == JsonValueKind.String
            ? auth.GetString() : null;
        if (token?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        return new HomeShopListRequest(token);
    }
}
