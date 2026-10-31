using System.Text.Json;

namespace Brs.Local.Accounts;

public static class HomeProjection
{
    private static readonly string[] EmptyLists = ["u_item_consume", "u_item_enhance", "u_item_awakening",
        "u_deck", "u_card", "u_card_skin", "u_equip", "u_quest", "u_quest_daily", "u_quest_hard",
        "u_background", "u_chara_voice", "u_equip_acquire", "u_still", "u_story", "u_event_point",
        "u_event_item", "u_event_quest", "u_title", "u_expedition", "u_present_message", "u_equip_material",
        "u_quest_ta", "u_mission_event", "u_beginner_mission", "u_quest_ranking", "u_gacha", "u_extra_shop",
        "u_shop_trigger", "u_gacha_exchange", "u_shop", "function_release", "login_bonus", "campaign",
        "rewards", "clear_mission_ids", "function_released_status"];

    // Completed tutorial and an actually owned card are required. Snapshot home
    // preferences take priority; profile/leader defaults still use real ownership.
    public static Dictionary<string, object?>? Build(Dictionary<string, object?> snapshot)
    {
        var player = ((JsonElement[])snapshot["u_user"]!)[0];
        if (TutorialProgression.ReadStep(player) is not (9999 or 10000)) return null;
        var cards = (JsonElement[])snapshot["u_card"]!;
        if (cards.Length == 0) return null;
        var cardId = cards.OrderBy(card => card.GetProperty("user_card_id").GetInt32())
            .First().GetProperty("card_id").GetInt32();
        var action = EmptyLists.ToDictionary(name => name, _ => (object?)Array.Empty<object>(), StringComparer.Ordinal);
        // All authoritative inventory arrays survive unchanged.
        foreach (var pair in snapshot) action[pair.Key] = pair.Value;
        if (!action.ContainsKey("u_home_customize")) action["u_home_customize"] = new[] { new
        {
            background_id = 100101, slot_1_card_id = cardId, slot_2_card_id = 0, slot_3_card_id = 0,
            slot_4_card_id = 0, slot_5_card_id = 0, slot_6_card_id = 0
        } };
        if (!action.ContainsKey("u_user_profile")) action["u_user_profile"] = new[] { new
        {
            user_id = player.GetProperty("user_id").GetInt32(), profile_birthday = "", card_id = cardId,
            title_id1 = 0, title_id2 = 0, main_quest_id = 0
        } };
        if (!action.ContainsKey("u_user_leader")) action["u_user_leader"] = new[] { new { card_id = cardId, user_equip_ids = "" } };
        action["available_dispatch_frame"] = false;
        action["mail"] = new { mail_count = 0, _mail_count = 0 };
        if (!action.ContainsKey("mission_count")) action["mission_count"] = 0;
        return action;
    }
}
