using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record HomeUpdateStepRequest(string? AuthToken)
{
    public static HomeUpdateStepRequest Read(JsonElement root)
    {
        var common = RequestEnvelope.ReadCommon(root);
        // Native RequestTutorialReward supplies RequestBase, with no endpoint
        // arguments. Its retry token remains an optional shared envelope field.
        foreach (var property in root.EnumerateObject())
            if (!property.NameEquals("common"))
                throw new InvalidDataException("Unsupported home update step request field.");
        var token = common.TryGetProperty("auth_token", out var auth) && auth.ValueKind == JsonValueKind.String
            ? auth.GetString() : null;
        if (token?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        return new HomeUpdateStepRequest(token);
    }
}
