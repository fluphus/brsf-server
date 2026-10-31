using System.Text.Json;
using System.Text.Json.Nodes;

namespace Brs.Local.Accounts;

public sealed record TutorialRequest(string Route, bool IsSkipShort, string RetryToken, string? AuthToken)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }
    public string Semantic => Route == "tutorial/update_step" ? "update_step" : IsSkipShort ? "skip:true" : "skip:false";
}

public static class TutorialProgression
{
    // Exact TutorialStepMaster136 rows whose UpdateStepApi is tutorial/update_step.
    private static readonly Dictionary<int, int> UpdateSteps = new()
    {
        [0] = 100, [200] = 300, [300] = 400, [500] = 600, [600] = 700,
        [800] = 900, [1000] = 1100, [1100] = 1200, [1300] = 1400,
        [1350] = 1800, [1360] = 1700, [1500] = 1600, [1600] = 1700,
        [1700] = 1800, [1800] = 9999
    };
    private static readonly HashSet<int> KnownSteps = [0, 100, 200, 300, 400, 500, 600, 700, 800,
        900, 1000, 1100, 1200, 1250, 1260, 1300, 1350, 1360, 1400, 1500, 1600, 1700, 1800, 9999, 10000];

    public static int ReadStep(JsonElement player)
    {
        var fields = player.EnumerateObject().Where(p => p.NameEquals("tutorial_step")).ToArray();
        if (fields.Length != 1 || fields[0].Value.ValueKind != JsonValueKind.Number ||
            !fields[0].Value.TryGetInt32(out var step) || !KnownSteps.Contains(step))
            throw new InvalidDataException("Invalid persisted tutorial step.");
        return step;
    }

    public static TutorialRequest ReadRequest(string route, JsonElement root, JsonElement common)
    {
        var skip = route == "tutorial/skip";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name) || !(property.Name == "common" || skip && property.Name == "is_skip_short"))
                throw new InvalidDataException("Duplicate or unsupported tutorial request field.");
        var shortSkip = false;
        if (skip)
        {
            if (!root.TryGetProperty("is_skip_short", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Tutorial skip flag must be boolean.");
            shortSkip = flag.GetBoolean();
        }
        var retry = common.EnumerateObject().Where(p => p.NameEquals("retry_token")).ToArray();
        if (retry.Length != 1 || retry[0].Value.ValueKind != JsonValueKind.String ||
            retry[0].Value.GetString() is not { } token || string.IsNullOrWhiteSpace(token) || token.Length > 256)
            throw new InvalidDataException("Exactly one bounded tutorial retry token is required.");
        var auth = common.TryGetProperty("auth_token", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (auth?.Length > 256) throw new InvalidDataException("Invalid bounded auth token.");
        return new TutorialRequest(route, shortSkip, token, auth) { EquipmentOptions = EquipmentGrantOptions.Read(common) };
    }

    public static int? Next(int current, TutorialRequest request)
    {
        if (request.Route == "tutorial/update_step") return UpdateSteps.TryGetValue(current, out var next) ? next : null;
        // Local policy: normal early steps can Skip; the same target is a no-op.
        // New requests cannot switch draw branches or rewind later progress.
        // Selector rows1/2 are master routing rows, never persisted player steps.
        var target = request.IsSkipShort ? 1260 : 1250;
        return current == target || current is >= 0 and < 1200 && KnownSteps.Contains(current) ? target : null;
    }

    public static string SetStep(string playerJson, int step, long now)
    {
        var player = JsonNode.Parse(playerJson)!.AsObject();
        player["tutorial_step"] = step;
        if (step == 9999) player["tutorial_ended_at"] = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return player.ToJsonString();
    }

    public static Dictionary<string, object?> Action(string playerJson)
    {
        using var player = JsonDocument.Parse(playerJson);
        return new() { ["u_user"] = new[] { player.RootElement.Clone() } };
    }
}
