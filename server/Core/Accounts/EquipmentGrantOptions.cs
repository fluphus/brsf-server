using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

// Original wire thresholds are 0/4/5; UI step indices 0/1/2 are not wire values.
public readonly record struct EquipmentGrantOptions(bool IsNewEquipAutoLock, int RarityEquipAutoLock)
{
    public static EquipmentGrantOptions Read(JsonElement common)
    {
        var properties = common.EnumerateObject().Where(p => p.NameEquals("option")).ToArray();
        if (properties.Length == 0 || properties.Length == 1 && properties[0].Value.ValueKind == JsonValueKind.Null) return default;
        if (properties.Length != 1 || properties[0].Value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Exactly one equipment option object is allowed.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = false; var rarity = 0;
        foreach (var property in properties[0].Value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate equipment option.");
            switch (property.Name)
            {
                case "is_new_equip_auto_lock":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("Invalid equipment first acquisition option.");
                    first = property.Value.GetBoolean(); break;
                case "rarity_equip_auto_lock":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out rarity) || rarity is not (0 or 4 or 5))
                        throw new InvalidDataException("Invalid equipment rarity option.");
                    break;
                default: throw new InvalidDataException("Unknown equipment option.");
            }
        }
        return new(first, rarity);
    }
}

// All state is immutable and passed by value along the current request's calls.
internal readonly record struct EquipmentGrantContext(EquipmentGrantOptions Options, BattleEquipmentOverflow? Overflow = null);
internal sealed record BattleEquipmentOverflow(int RunId, string RetryHash, string ConfigHash);
