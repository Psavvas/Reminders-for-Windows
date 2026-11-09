using System.Text.Json;

namespace Reminders.Windows.Services;

public static class ReminderEditPatch
{
    public static Dictionary<string, object?> Build(string id, string baseline, string draft, IReadOnlyDictionary<string, object?> values)
    {
        using var before = JsonDocument.Parse(baseline);
        using var after = JsonDocument.Parse(draft);
        bool Changed(string field) => before.RootElement.GetProperty(field).GetRawText() != after.RootElement.GetProperty(field).GetRawText();
        var patch = new Dictionary<string, object?> { ["id"] = id };
        void Add(string field, params string[] controls)
        {
            if (values.TryGetValue(field, out var value) && controls.Any(Changed)) patch[field] = value;
        }
        Add("title", "Title"); Add("description", "Notes"); Add("list_id", "List");
        Add("flagged", "Flagged"); Add("priority", "Priority");
        if (new[] { "Date", "Time", "AllDay" }.Any(Changed))
        {
            // Date, time and all-day state express one scheduling decision.
            patch["due_date"] = values["due_date"]; patch["all_day"] = values["all_day"];
        }
        Add("recurrence", "Repeat", "Interval");
        return patch;
    }
}
