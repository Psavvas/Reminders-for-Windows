using System.Text.Json.Nodes;

namespace Reminders.Core;

internal static class Recurrence
{
    public static JsonObject? Validate(JsonNode? value, string? due)
    {
        if (value is null) return null;
        if (value is not JsonObject || value.Number("frequency") is < 1 or > 4 || value.Number("interval", 1) is < 1 or > 999)
            throw CoreException.Bad("Choose a daily, weekly, monthly or yearly repeat with an interval from 1 to 999.");
        if (due is null) throw CoreException.Bad("A repeating reminder needs a due date.");
        return new JsonObject { ["frequency"] = value.Number("frequency"), ["interval"] = value.Number("interval", 1) };
    }

    public static JsonObject Describe(JsonNode rule)
    {
        var fields = rule["fields"] as JsonObject ?? new();
        var supported = fields.All(p => p.Key is "Reminder" or "Frequency" or "Interval" or "OccurrenceCount" or "FirstDayOfTheWeek" or "Imported" or "Deleted" or "EndDate" or "CreationDate" or "LastModifiedDate" or "ResolutionTokenMap"
            || p.Value?["value"] is null || p.Value?["value"] is JsonArray { Count: 0 });
        var frequency = CloudKit.Int(rule, "Frequency"); var interval = Math.Max(1, CloudKit.Int(rule, "Interval"));
        return new() { ["frequency"] = frequency, ["interval"] = interval, ["supported"] = supported && frequency is >= 1 and <= 4 && interval <= 999 };
    }

    // Calendar arithmetic preserves wall-clock time across DST. Invalid monthly
    // dates are skipped (Jan 31 -> Mar 31), rather than drifting to the 28th.
    public static string? Next(JsonNode rule, string due, string? zoneName, DateTimeOffset completedAt) => Next(rule, due, zoneName, completedAt, out _);
    public static string? Next(JsonNode rule, string due, string? zoneName, DateTimeOffset completedAt, out int consumed)
    {
        var description = Describe(rule);
        var frequency = description.Number("frequency");
        if (!description.Flag("supported") || frequency is < 1 or > 4)
            throw CoreException.Bad("This reminder has a custom repeat rule. Complete it on an Apple device to preserve its schedule.");
        var interval = description.Number("interval");
        if (interval is < 1 or > 999) throw CoreException.Bad("This reminder's repeat interval is not supported.");
        var zone = TimeUtil.Zone(zoneName);
        var start = TimeZoneInfo.ConvertTime(TimeUtil.Parse(due), zone).DateTime;
        var count = CloudKit.Int(rule, "OccurrenceCount");
        consumed = 0;
        for (var step = 1; step <= 10000; step++)
        {
            DateTime wall;
            try
            {
                wall = frequency switch { 1 => start.AddDays(step * interval), 2 => start.AddDays(step * interval * 7), 3 => start.AddMonths(checked((int)(step * interval))), _ => start.AddYears(checked((int)(step * interval))) };
            }
            catch (ArgumentOutOfRangeException) { return null; }
            if (frequency >= 3 && wall.Day != start.Day) continue;
            consumed++;
            if (count > 0 && consumed >= count) return null;
            var next = TimeUtil.Anchor(wall, zone);
            if (CloudKit.Field(rule, "EndDate") is JsonValue end && end.TryGetValue<long>(out var endMs) && next > TimeUtil.FromFloating(endMs, zoneName)) return null;
            if (next > completedAt) return TimeUtil.Iso(next);
        }
        throw CoreException.Bad("The next repeat is outside the supported date range.");
    }

    public static JsonObject Fields(string reminder, JsonNode recurrence) => new()
    {
        ["Reminder"] = CloudKit.Reference(reminder), ["Frequency"] = CloudKit.Number(recurrence.Number("frequency")),
        ["Interval"] = CloudKit.Number(recurrence.Number("interval", 1)), ["OccurrenceCount"] = CloudKit.Number(0),
        ["FirstDayOfTheWeek"] = CloudKit.Number(0), ["Imported"] = CloudKit.Number(0), ["Deleted"] = CloudKit.Number(0)
    };
}
