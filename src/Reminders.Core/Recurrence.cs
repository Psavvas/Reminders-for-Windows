using System.Text.Json.Nodes;

namespace Reminders.Core;

internal static class Recurrence
{
    public static JsonObject? Validate(JsonNode? value, string? due)
    {
        if (value is null) return null;
        var interval = value.Property("interval") is null ? 1 : value.Number("interval", -1);
        if (value is not JsonObject || value.Number("frequency") is < 1 or > 4 || interval is < 1 or > 999)
            throw CoreException.Bad("Choose a daily, weekly, monthly or yearly repeat with an interval from 1 to 999.");
        if (due is null) throw CoreException.Bad("A repeating reminder needs a due date.");
        return new JsonObject { ["frequency"] = value.Number("frequency"), ["interval"] = interval };
    }

    public static JsonObject Describe(JsonNode rule)
    {
        var fields = rule["fields"] as JsonObject ?? new();
        var supported = fields.All(p => p.Key is "Reminder" or "Frequency" or "Interval" or "OccurrenceCount" or "FirstDayOfTheWeek" or "Imported" or "Deleted" or "EndDate" or "CreationDate" or "LastModifiedDate" or "ResolutionTokenMap"
            || p.Value?["value"] is null || p.Value?["value"] is JsonArray { Count: 0 } || EmptyConstraint(p.Key, p.Value));
        // The UI/RPC uses 1=daily through 4=yearly. Apple's stored enum uses
        // 0=daily, 1=weekly, 2=monthly, 3=yearly, then hourly/minutely/secondly.
        // A missing or unsupported service value must never become a daily
        // repeat or mislabel an hourly rule as yearly.
        var frequency = CloudKit.Field(rule, "Frequency") is JsonValue raw && raw.TryGetValue<long>(out var wire)
            ? wire switch { 0 => 1, 1 => 2, 2 => 3, 3 => 4, _ => 0 } : 0;
        var interval = Math.Max(1, CloudKit.Int(rule, "Interval"));
        return new() { ["frequency"] = frequency, ["interval"] = interval, ["supported"] = supported && frequency is >= 1 and <= 4 && interval <= 999 };
    }

    private static bool EmptyConstraint(string name, JsonNode? field)
    {
        if (name is not ("DaysOfTheWeek" or "DaysOfTheMonth" or "DaysOfTheYear" or "WeeksOfTheYear" or "MonthsOfTheYear" or "SetPositions")) return false;
        if (field.Text("type") is not ("BYTES" or "ENCRYPTED_BYTES") || field.Text("value") is not { } value) return false;
        try { return J.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value))) is JsonArray { Count: 0 }; }
        catch (FormatException) { return false; }
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
        ["Reminder"] = CloudKit.Reference(reminder),
        ["Frequency"] = CloudKit.Number(recurrence.Number("frequency") switch
        {
            1 => 0, 2 => 1, 3 => 2, 4 => 3,
            _ => throw CoreException.Bad("Choose a daily, weekly, monthly or yearly repeat.")
        }),
        ["Interval"] = CloudKit.Number(recurrence.Number("interval", 1)), ["OccurrenceCount"] = CloudKit.Number(0),
        ["FirstDayOfTheWeek"] = CloudKit.Number(0), ["Imported"] = CloudKit.Number(0), ["Deleted"] = CloudKit.Number(0)
    };
}
