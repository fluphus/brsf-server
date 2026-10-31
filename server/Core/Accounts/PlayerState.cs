using System.Globalization;
using System.Text.Json;

namespace Brs.Local.Accounts;

public static class PlayerState
{
    // Native DateTimeFormatter maps this wire value to DateTime.MinValue.
    public const string NoVipEndDate = "0000-00-00 00:00:00";
    // Explicit local ownership: no granted cards/items/currency or completed
    // tutorial. Singleton profile/leader/home and function_release are omitted.
    private static readonly string[] EmptyOwnedLists = ["u_item_consume", "u_item_enhance", "u_item_awakening",
        "u_deck", "u_card", "u_card_skin", "u_equip", "u_quest", "u_quest_daily", "u_quest_hard",
        "u_background", "u_chara_voice", "u_equip_acquire", "u_still", "u_story", "u_event_point",
        "u_event_item", "u_event_quest", "u_title", "u_expedition", "u_present_message", "u_equip_material",
        "u_quest_ta", "u_mission_event", "u_beginner_mission", "u_quest_ranking", "u_gacha", "u_extra_shop", "u_shop_trigger"];

    public static string NewPlayer(int id,long issued)=>NewPlayer(id,issued,checked((ulong)id));
    public static string NewPlayer(int id, long issued,ulong publicId) => JsonSerializer.Serialize(new
    {
        user_id = id, my_game_id = publicId, status = 0, tutorial_step = 0, tutorial_ended_at = "",
        name = "指挥官", message = "", birthday = 0, rank = 1, exp = 0, stamina = 50, stamina_max = 50,
        stamina_cure_started_at = DateTimeOffset.FromUnixTimeSeconds(issued).ToOffset(TimeSpan.FromHours(9))
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        game_coin = 0UL, free_paid_coin = 0UL, paid_coin = 0UL, main_quest_part_no = 0, friend_pt = 0UL,
        os_type = 2, server_offset_time = 0, vip_login_bonus_end_date = NoVipEndDate,
        vip_loop_end_date = NoVipEndDate, vip_daily_quest_end_date = NoVipEndDate
    });

    public static Dictionary<string, object?> Action(string playerJson, string key, string value)
    {
        var action = EmptyOwnedLists.ToDictionary(name => name, _ => (object?)Array.Empty<object>(), StringComparer.Ordinal);
        using var player = JsonDocument.Parse(playerJson);
        action["u_user"] = new[] { player.RootElement.Clone() };
        action["available_dispatch_frame"] = false;
        action[key] = value;
        if (key == "auth_token") action["login_bonus"] = Array.Empty<object>();
        return action;
    }
}
