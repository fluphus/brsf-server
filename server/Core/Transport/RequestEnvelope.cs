using System.Text.Json;

namespace Brs.Local.Transport;

public static class RequestEnvelope
{
    public static JsonElement ReadCommon(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Request must be an object.");
        var commonProperties = root.EnumerateObject().Where(p => p.NameEquals("common")).ToArray();
        if (commonProperties.Length != 1 || commonProperties[0].Value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Exactly one request common object is required.");
        var common = commonProperties[0].Value;
        var tokens = common.EnumerateObject().Where(p => p.NameEquals("auth_token")).ToArray();
        if (tokens.Length > 1 || (tokens.Length == 1 && tokens[0].Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)))
            throw new InvalidDataException("Invalid auth_token property.");
        return common;
    }

    public static bool IsPreAccount(JsonElement common) =>
        !common.TryGetProperty("auth_token", out var token) || token.ValueKind == JsonValueKind.Null || token.GetString() == "";
}
