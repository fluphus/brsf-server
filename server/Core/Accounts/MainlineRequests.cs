using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

internal static class MainlineFields
{
    internal static JsonElement Read(JsonElement root, params string[] fields)
    {
        var common = RequestEnvelope.ReadCommon(root);
        var names = fields.Append("common").ToHashSet(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
            if (!names.Remove(p.Name)) throw new InvalidDataException("Unknown or duplicate mainline field.");
        if (names.Count != 0) throw new InvalidDataException("Missing mainline field.");
        return common;
    }

    internal static int Int(JsonElement root, string key) => root.GetProperty(key) is var value &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n :
        throw new InvalidDataException("Invalid mainline integer.");

    internal static float Float(JsonElement root, string key) => root.GetProperty(key) is var value &&
        value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var n) && float.IsFinite(n) ? n :
        throw new InvalidDataException("Invalid mainline float.");

    internal static bool Bool(JsonElement root, string key) => root.GetProperty(key).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new InvalidDataException("Invalid mainline boolean.")
    };

    internal static string String(JsonElement root, string key)
    {
        var value = root.GetProperty(key);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 65536 } text
            ? text : throw new InvalidDataException("Invalid bounded mainline string.");
    }

    internal static string? Auth(JsonElement common)
    {
        if (!common.TryGetProperty("auth_token", out var value)) return null;
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 256 } token
            ? token : throw new InvalidDataException("Invalid mainline authentication type.");
    }

    internal static string Retry(JsonElement common) => EconomyRequestFields.Retry(common);

    internal static string Semantic(string route, object value) => route + ":" +
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal static int[] Integers(JsonElement root, string key)
    {
        var array = root.GetProperty(key);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 4096)
            throw new InvalidDataException("Invalid bounded mainline integer list.");
        return array.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
            ? n : throw new InvalidDataException("Invalid mainline list integer.")).ToArray();
    }

    internal static SortedDictionary<int, int> Counts(JsonElement root, string key)
    {
        var value = root.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid mainline counts.");
        var result = new SortedDictionary<int, int>();
        foreach (var p in value.EnumerateObject())
        {
            if (result.Count >= 4096 || !int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                p.Name != id.ToString(CultureInfo.InvariantCulture) || p.Value.ValueKind != JsonValueKind.Number ||
                !p.Value.TryGetInt32(out var n) || !result.TryAdd(id, n))
                throw new InvalidDataException("Invalid or duplicate mainline count entry.");
        }
        return result;
    }
}

public sealed record QuestStoryRequest(string RetryToken, string? AuthToken, int QuestId, int StoryId)
{
    public const string Route = "quest/story";
    public string Semantic => MainlineFields.Semantic(Route, new { QuestId, StoryId });
    public static QuestStoryRequest Read(JsonElement root)
    {
        var c = MainlineFields.Read(root, "quest_id", "story_id");
        return new(MainlineFields.Retry(c), MainlineFields.Auth(c), MainlineFields.Int(root, "quest_id"), MainlineFields.Int(root, "story_id"));
    }
}

public sealed record QuestSupportRequest(string? AuthToken)
{
    public static QuestSupportRequest Read(JsonElement root) => new(MainlineFields.Auth(MainlineFields.Read(root)));
}

public sealed record QuestActiveSupportRequest(string? AuthToken, int SupportUserId)
{
    public static QuestActiveSupportRequest Read(JsonElement root)
    {
        var c = MainlineFields.Read(root, "support_user_id");
        return new(MainlineFields.Auth(c), MainlineFields.Int(root, "support_user_id"));
    }
}

public sealed record QuestCheckRequest(string? AuthToken, int QuestId, int RestartPoint)
{
    public static QuestCheckRequest Read(JsonElement root)
    {
        var c = MainlineFields.Read(root, "quest_id", "restart_point");
        return new(MainlineFields.Auth(c), MainlineFields.Int(root, "quest_id"), MainlineFields.Int(root, "restart_point"));
    }
}

public sealed record QuestRetireRequest(string RetryToken, string? AuthToken, int QuestId)
{
    public const string Route = "quest/retire";
    public string Semantic => MainlineFields.Semantic(Route, new { QuestId });
    public static QuestRetireRequest Read(JsonElement root)
    {
        var c = MainlineFields.Read(root, "quest_id");
        return new(MainlineFields.Retry(c), MainlineFields.Auth(c), MainlineFields.Int(root, "quest_id"));
    }
}

public sealed record QuestStartRequest(string RetryToken, string? AuthToken, int QuestId, int DeckNo, int SupportUserId,
    int SetLoopCount, bool IsCureStaminaUseCoin, int LoopCount, float DeckTotalPower, float MaxUnitPower)
{
    public const string Route = "quest/start";
    public string Semantic => MainlineFields.Semantic(Route, new { QuestId, DeckNo, SupportUserId, SetLoopCount, IsCureStaminaUseCoin, LoopCount, DeckTotalPower, MaxUnitPower });
    public static QuestStartRequest Read(JsonElement root)
    {
        var c = MainlineFields.Read(root, "quest_id", "deck_no", "support_user_id", "set_loop_count", "is_cure_stamina_use_coin", "loop_count", "deck_total_power", "max_unit_power");
        return new(MainlineFields.Retry(c), MainlineFields.Auth(c), MainlineFields.Int(root, "quest_id"), MainlineFields.Int(root, "deck_no"),
            MainlineFields.Int(root, "support_user_id"), MainlineFields.Int(root, "set_loop_count"), MainlineFields.Bool(root, "is_cure_stamina_use_coin"),
            MainlineFields.Int(root, "loop_count"), MainlineFields.Float(root, "deck_total_power"), MainlineFields.Float(root, "max_unit_power"));
    }
}

public sealed record QuestResultRequest(string RetryToken, string? AuthToken, int QuestId, int ClearRate, int BattleTime,
    int KillAbilityCount, int MainSkillCount, SortedDictionary<int, int> DefeatEnemy, SortedDictionary<int, int> DefeatBoss,
    bool IsAllDefenseBaseSafety, bool IsLoopMode, int LoopCount, int SelectRewardId, string BattleData,
    float BattleTimeTa, float TotalPower, int[] GetDropPlaceIds)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

    public const string Route = "quest/result";
    public string Semantic => MainlineFields.Semantic(Route, new { QuestId, ClearRate, BattleTime, KillAbilityCount, MainSkillCount,
        DefeatEnemy, DefeatBoss, IsAllDefenseBaseSafety, IsLoopMode, LoopCount, SelectRewardId, BattleData, BattleTimeTa, TotalPower, GetDropPlaceIds });
    public static QuestResultRequest Read(JsonElement root)
    {
        var c = MainlineFields.Read(root, "quest_id", "clear_rate", "battle_time", "kill_ability_count", "main_skill_count", "defeat_enemy", "defeat_boss",
            "is_all_defense_base_safety", "is_loop_mode", "loop_count", "select_reward_id", "battle_data", "battle_time_ta", "total_power", "get_drop_place_ids");
        return new(MainlineFields.Retry(c), MainlineFields.Auth(c), MainlineFields.Int(root, "quest_id"), MainlineFields.Int(root, "clear_rate"),
            MainlineFields.Int(root, "battle_time"), MainlineFields.Int(root, "kill_ability_count"), MainlineFields.Int(root, "main_skill_count"),
            MainlineFields.Counts(root, "defeat_enemy"), MainlineFields.Counts(root, "defeat_boss"), MainlineFields.Bool(root, "is_all_defense_base_safety"),
            MainlineFields.Bool(root, "is_loop_mode"), MainlineFields.Int(root, "loop_count"), MainlineFields.Int(root, "select_reward_id"), MainlineFields.String(root, "battle_data"),
            MainlineFields.Float(root, "battle_time_ta"), MainlineFields.Float(root, "total_power"), MainlineFields.Integers(root, "get_drop_place_ids")) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
    }
}

public sealed record MissionTotalPowerRequest(string RetryToken, string? AuthToken, int TotalPower)
{
    public const string Route = "mission/check/total_power";
    public string Semantic => MainlineFields.Semantic(Route, new { TotalPower });
    public static MissionTotalPowerRequest Read(JsonElement root)
    {
        var c = MainlineFields.Read(root, "total_power");
        return new(MainlineFields.Retry(c), MainlineFields.Auth(c), MainlineFields.Int(root, "total_power"));
    }
}
