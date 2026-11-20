using System.Globalization;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal static class TimeUtil
{
    public static TimeZoneInfo Zone(string? name = null)
    {
        if (name is not null) { try { return TimeZoneInfo.FindSystemTimeZoneById(name); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { } }
        return TimeZoneInfo.Local;
    }
    public static DateTimeOffset Anchor(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        for (var i = 0; i < 180 && zone.IsInvalidTime(wall); i++) wall = wall.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }
    public static DateTimeOffset Parse(string value)
    {
        var explicitZone = value.EndsWith('Z') || (value.Length > 10 && (value[10..].Contains('+') || value[10..].Contains('-')));
        if (explicitZone && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)) return instant.ToUniversalTime();
        if (DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return Anchor(wall, Zone());
        throw CoreException.Bad("invalid due date").WithPresentation("ErrorInvalidDueDate");
    }
    public static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    public static string Now => Iso(DateTimeOffset.UtcNow);
    public static string Tomorrow => Iso(Anchor(TimeZoneInfo.ConvertTime(DateTimeOffset.Now, Zone()).Date.AddDays(1), Zone()));
    public static DateTimeOffset FromFloating(long ms, string? zone) => Anchor(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, Zone(zone));
    public static long ToFloating(DateTimeOffset instant, string? zone) => new DateTimeOffset(DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, Zone(zone)).DateTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    public static string? Normalize(JsonNode? value, bool allDay)
    {
        if (value is null || value.ToString() == "") return null;
        DateTimeOffset instant;
        if (value is JsonValue v && v.TryGetValue<long>(out var ms))
        {
            try { instant = DateTimeOffset.FromUnixTimeMilliseconds(ms); }
            catch (ArgumentOutOfRangeException) { throw CoreException.Bad("due date is outside the supported range").WithPresentation("ErrorDueDateOutsideRange"); }
        }
        else if (value is JsonValue text && text.TryGetValue<string>(out var raw)) instant = Parse(raw);
        else throw CoreException.Bad("due date must be ISO-8601 or epoch millis").WithPresentation("ErrorInvalidDueDateFormat");
        if (allDay) instant = Anchor(TimeZoneInfo.ConvertTime(instant, Zone()).Date, Zone());
        return Iso(instant);
    }
    public static DateTimeOffset NotifyAt(string due, bool allDay) => allDay ? Anchor(TimeZoneInfo.ConvertTime(Parse(due), Zone()).Date.AddHours(9), Zone()) : Parse(due);
}
