using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record FriendPointPickupRequest(string? AuthToken)
{
    public static FriendPointPickupRequest Read(JsonElement root)
    {
        var common = RequestEnvelope.ReadCommon(root);
        // Native WebRequestFactory.Create<RequestBase> supplies only common.
        // Retry/options are shared envelope fields, never requested credit amounts.
        foreach (var property in root.EnumerateObject())
            if (!property.NameEquals("common"))
                throw new InvalidDataException("Unsupported friend point pickup request field.");
        var token = common.TryGetProperty("auth_token", out var auth) && auth.ValueKind == JsonValueKind.String
            ? auth.GetString() : null;
        if (token?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        return new FriendPointPickupRequest(token);
    }
}
