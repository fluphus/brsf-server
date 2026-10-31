using System.Globalization;
using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record StandardGachaRequest(string RetryToken, string? AuthToken, int GachaId, int LotId)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

    public const string Route = "gacha/result";
    public string Semantic => string.Create(CultureInfo.InvariantCulture, $"gacha:{GachaId}:{LotId}:1:false");
    public static StandardGachaRequest Read(JsonElement root)
    {
        var common = RequestEnvelope.ReadCommon(root);
        var allowed = new HashSet<string>(["common", "gacha_id", "lot_id", "execute_count", "is_use_gacha_ticket"], StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Remove(property.Name)) throw new InvalidDataException("Duplicate or unknown standard gacha request field.");
        if (allowed.Count != 0) throw new InvalidDataException("Missing standard gacha request field.");
        int Integer(string name)
        {
            var value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var integer))
                throw new InvalidDataException("Invalid standard gacha integer.");
            return integer;
        }
        var pool = Integer("gacha_id"); var lot = Integer("lot_id");
        if (pool is not (11012 or 12012) || lot != pool * 10 + 1 && lot != pool * 10 + 2 || Integer("execute_count") != 1 ||
            root.GetProperty("is_use_gacha_ticket").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Unsupported standard gacha operation.");
        var retries = common.EnumerateObject().Where(p => p.NameEquals("retry_token")).ToArray();
        if (retries.Length != 1 || retries[0].Value.ValueKind != JsonValueKind.String ||
            retries[0].Value.GetString() is not { } retry || string.IsNullOrWhiteSpace(retry) || retry.Length > 256)
            throw new InvalidDataException("Exactly one bounded economy retry token required.");
        var auth = common.TryGetProperty("auth_token", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        if (auth?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        return new(retry, auth, pool, lot) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
    }
}
