using System.Globalization;
using System.Text.Json.Nodes;

namespace Brs.Local.Accounts;

// Original client interval is 300 seconds. These functions preserve fractional
// progress below the natural maximum and never bank time spent at/above it.
internal sealed record MainlineStamina(int Current, int Maximum, long Anchor)
{
    internal const int HoldingCap = 999;
    internal const int CureSeconds = 300;

    internal static MainlineStamina Read(JsonObject player, long now)
    {
        if (player["stamina"] is not JsonValue stamina || !stamina.TryGetValue<int>(out var current) ||
            player["stamina_max"] is not JsonValue maximum || !maximum.TryGetValue<int>(out var max) ||
            current is < 0 or > HoldingCap || max is < 1 or > HoldingCap ||
            player["stamina_cure_started_at"] is not JsonValue start || !start.TryGetValue<string>(out var text) ||
            !DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("Invalid persisted stamina.");
        var anchor = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(9)).ToUnixTimeSeconds();
        if (anchor > now || anchor <= 0) throw new InvalidDataException("Invalid persisted stamina anchor.");
        if (current >= max) return new(current, max, now);
        var ticks = (now - anchor) / CureSeconds;
        var after = (int)Math.Min(max, current + ticks);
        return new(after, max, after >= max ? now : checked(anchor + ticks * CureSeconds));
    }

    internal MainlineStamina? Spend(int cost, long now)
    {
        if (cost < 0) throw new InvalidDataException("Invalid stamina cost.");
        return cost > Current ? null : new(Current - cost, Maximum, Current >= Maximum ? now : Anchor);
    }

    internal void Save(JsonObject player)
    {
        player["stamina"] = Current;
        player["stamina_max"] = Maximum;
        player["stamina_cure_started_at"] = DateTimeOffset.FromUnixTimeSeconds(Anchor).ToOffset(TimeSpan.FromHours(9))
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
