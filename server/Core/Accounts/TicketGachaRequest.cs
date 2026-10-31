using System.Globalization;
using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record TicketGachaRequest(string RetryToken, string? AuthToken, int GachaId, int LotId)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

    public const string Route = "gacha/result";
    public string Semantic => string.Create(CultureInfo.InvariantCulture, $"gacha:{GachaId}:{LotId}:1:false");
    public static TicketGachaRequest Read(JsonElement root)
    {
        var common = EconomyRequestFields.Read(root, "gacha_id", "lot_id", "execute_count", "is_use_gacha_ticket");
        int Integer(string name)
        {
            var value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
                throw new InvalidDataException("Invalid guaranteed ticket draw integer.");
            return result;
        }
        var pool = Integer("gacha_id"); var lot = Integer("lot_id");
        // The original flag denotes a substituted common ticket for a gem lot.
        // These dedicated item-type6 lots use the original false branch.
        if (pool is not (55003 or 55004) || lot != pool * 10 + 1 || Integer("execute_count") != 1 ||
            root.GetProperty("is_use_gacha_ticket").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Unsupported guaranteed ticket draw.");
        return new(EconomyRequestFields.Retry(common), EconomyRequestFields.Auth(common), pool, lot) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
    }
}
