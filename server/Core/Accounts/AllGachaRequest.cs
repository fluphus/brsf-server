using System.Globalization;
using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record AllGachaRequest(string RetryToken, string? AuthToken, int GachaId, int LotId, bool IsUseGachaTicket)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

    public const string Route = "gacha/result";
    public string Semantic => string.Create(CultureInfo.InvariantCulture, $"gacha:{GachaId}:{LotId}:1:{(IsUseGachaTicket ? "true" : "false")}");
    public static AllGachaRequest Read(JsonElement root)
    {
        var common = RequestEnvelope.ReadCommon(root);
        var names = new HashSet<string>(["common", "gacha_id", "lot_id", "execute_count", "is_use_gacha_ticket"], StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Remove(property.Name)) throw new InvalidDataException("Duplicate or unknown gacha field.");
        if (names.Count != 0) throw new InvalidDataException("Missing gacha field.");
        int Integer(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Number && root.GetProperty(name).TryGetInt32(out var value) ? value : throw new InvalidDataException("Invalid gacha integer.");
        var pool = Integer("gacha_id"); var lot = Integer("lot_id"); var flag = root.GetProperty("is_use_gacha_ticket");
        if (pool <= 0 || lot <= 0 || Integer("execute_count") != 1 || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Invalid gacha operation.");
        var retries = common.EnumerateObject().Where(p => p.NameEquals("retry_token")).ToArray();
        if (retries.Length != 1 || retries[0].Value.ValueKind != JsonValueKind.String ||
            retries[0].Value.GetString() is not { } retry || string.IsNullOrWhiteSpace(retry) || retry.Length > 256)
            throw new InvalidDataException("Exactly one bounded gacha retry required.");
        if (common.TryGetProperty("auth_token", out var authValue) && authValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid auth type.");
        var auth = common.TryGetProperty("auth_token", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        if (auth?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        return new(retry, auth, pool, lot, flag.GetBoolean()) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
    }
}
