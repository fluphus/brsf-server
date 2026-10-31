using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record HomeRequest(string? AuthToken)
{
    public static HomeRequest Read(JsonElement root)
    {
        var common = RequestEnvelope.ReadCommon(root);
        // Original HomeRequest has exactly common; retry/options remain common
        // envelope fields and do not represent a home mutation.
        foreach (var property in root.EnumerateObject())
            if (!property.NameEquals("common"))
                throw new InvalidDataException("Unsupported home request field.");
        var token = common.TryGetProperty("auth_token", out var auth) && auth.ValueKind == JsonValueKind.String
            ? auth.GetString() : null;
        if (token?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        return new HomeRequest(token);
    }
}
