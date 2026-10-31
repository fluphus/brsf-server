using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record GachaTopRequest(string? AuthToken)
{
    public static GachaTopRequest Read(JsonElement root)
    {
        var common = RequestEnvelope.ReadCommon(root);
        // Native GachaTopRequest inherits RequestBase without business fields.
        foreach (var property in root.EnumerateObject())
            if (!property.NameEquals("common"))
                throw new InvalidDataException("Unsupported gacha top request field.");
        var token = common.TryGetProperty("auth_token", out var auth) && auth.ValueKind == JsonValueKind.String
            ? auth.GetString() : null;
        if (token?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        return new(token);
    }
}
