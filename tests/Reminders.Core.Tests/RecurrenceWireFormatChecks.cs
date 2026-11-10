using System.Text.Json.Nodes;
using Reminders.Core;

internal static class RecurrenceWireFormatChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Independent service fixtures, not generated through Recurrence.Fields.
        // Verified against Apple's public Reminders 2638Build16 script; see
        // docs/feature-validation.md for the URL, hash and conversion function.
        JsonObject AppleRule(long wireFrequency, int interval = 1) => J.Node(new { fields = new
        {
            Frequency = new { type = "INT64", value = wireFrequency },
            Interval = new { type = "INT64", value = interval }
        } }).AsObject();
        var due = "2026-10-20T09:00:00Z"; var completed = TimeUtil.Parse("2026-10-20T10:00:00Z");
        foreach (var (name, appFrequency, wireFrequency, next) in new[]
        {
            ("daily", 1, 0, "2026-10-21T09:00:00Z"),
            ("weekly", 2, 1, "2026-10-27T09:00:00Z"),
            ("monthly", 3, 2, "2026-11-20T09:00:00Z"),
            ("yearly", 4, 3, "2027-10-20T09:00:00Z")
        })
        {
            var fields = Recurrence.Fields("Reminder/test", J.Node(new { frequency = appFrequency, interval = 1 }));
            check(fields["Frequency"].Number("value", -1) == wireFrequency, "write " + name + " using Apple's frequency value");
            var rule = AppleRule(wireFrequency); var description = Recurrence.Describe(rule);
            check(description.Number("frequency") == appFrequency && description.Flag("supported"), "read Apple-created " + name + " as the same Windows schedule");
            check(Recurrence.Next(rule, due, "UTC", completed) == TimeUtil.Iso(TimeUtil.Parse(next)), "Apple-created " + name + " advances by the right calendar unit");
        }
        check(Recurrence.Next(AppleRule(1, 2), due, "UTC", completed) == TimeUtil.Iso(TimeUtil.Parse("2026-11-03T09:00:00Z")), "Apple weekly interval two advances by two weeks");
        foreach (var frequency in new long[] { -1, 4, 5, 6, 999 })
        {
            var rule = AppleRule(frequency);
            check(!Recurrence.Describe(rule).Flag("supported"), "hourly/minutely/secondly/unknown Apple frequencies are protected: " + frequency);
            try { Recurrence.Next(rule, due, "UTC", completed); check(false, "unsupported wire frequency must not be completed"); }
            catch (CoreException error) { check(error.Code == "BAD_REQUEST", "unsupported wire frequency refuses calendar advancement: " + frequency); }
        }
        foreach (var value in new JsonNode?[] { null, JsonValue.Create("0") })
        {
            var rule = AppleRule(0); rule["fields"]!["Frequency"]!["value"] = value;
            check(!Recurrence.Describe(rule).Flag("supported"), "missing or invalid frequency does not become a daily repeat");
        }
    }
}
