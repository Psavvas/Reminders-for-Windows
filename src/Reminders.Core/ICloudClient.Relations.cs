using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class ICloudClient
{
    private static JsonObject Snapshot(JsonNode record)
    {
        var reminder = ToReminder(record) ?? throw new CoreException("ERROR", "iCloud returned an invalid reminder.");
        reminder["cloud_record"] = record.DeepClone(); return reminder;
    }
    private static void AddTokens(JsonObject fields, params string[] names)
    {
        var map = J.Parse(fields["ResolutionTokenMap"].Text("value"))?["map"] as JsonObject ?? new();
        var added = J.Parse(CloudKit.ResolutionTokens(names).Text("value"))!["map"]!.AsObject();
        foreach (var pair in added)
        {
            var token = pair.Value!.DeepClone(); token["counter"] = map[pair.Key].Number("counter") + 1; map[pair.Key] = token;
        }
        fields["ResolutionTokenMap"] = CloudKit.String(new JsonObject { ["map"] = map.DeepClone() }.ToJsonString());
    }

    private static JsonObject MergeResult(JsonNode original, JsonArray result)
    {
        var copy = original.DeepClone();
        var returned = result.FirstOrDefault(r => r.Text("recordName") == original.Text("recordName"))
            ?? throw new CoreException("NETWORK", "iCloud did not return the changed reminder.");
        copy["fields"] ??= new JsonObject();
        if (returned["fields"] is JsonObject changed)
            foreach (var pair in changed) copy["fields"]![pair.Key] = pair.Value?.DeepClone();
        copy["recordChangeTag"] = returned["recordChangeTag"]?.DeepClone();
        return Snapshot(copy);
    }

    private async Task<JsonObject> CompleteRecurring(JsonNode original, JsonObject patch, string? baseTag, CancellationToken token)
    {
        var id = original.Required("recordName");
        if (CloudKit.Int(original, "Completed") != 0) return Snapshot(original);
        var historyId = patch.Text("completion_id") ?? throw CoreException.Bad("Refresh this reminder before completing its repeat.");
        // Resolve an ambiguous network failure before comparing revisions. A
        // committed atomic request must never advance the schedule twice.
        var history = (await Cloud.LookupMany([historyId], token, true)).FirstOrDefault();
        if (history is not null) { var recovered = Snapshot(original); recovered["completed_occurrence"] = Snapshot(history); return recovered; }
        if (baseTag is not null && baseTag != original.Text("recordChangeTag"))
            throw new CoreException("CONFLICT", "This repeating reminder changed on another device.", ToReminder(original)!.ToJsonString());
        var ids = (JsonArray)CloudKit.Field(original, "RecurrenceRuleIDs")!;
        if (ids.Count != 1) throw CoreException.Bad("Complete reminders with multiple repeat rules on an Apple device to preserve their schedule.");
        var ruleId = ids[0]!.ToString(); if (!ruleId.StartsWith("RecurrenceRule/")) ruleId = "RecurrenceRule/" + ruleId;
        var rule = (await Cloud.Lookup(ruleId, token)).FirstOrDefault() ?? throw CoreException.Bad("iCloud did not return this reminder's repeat rule.");
        var due = ToReminder(original)!.Text("due_date") ?? throw CoreException.Bad("Set a due date before completing this repeating reminder.");
        var now = DateTimeOffset.UtcNow;
        var next = Recurrence.Next(rule, due, CloudKit.Text(original, "TimeZone"), now, out var consumed);
        if (next is null)
        {
            var last = UpdateFields(original, patch);
            var reply = await Cloud.Modify(CloudKit.Operation("update", id, original.Text("recordChangeTag"), last), token);
            var final = original.DeepClone(); foreach (var pair in last) final["fields"]![pair.Key] = pair.Value?.DeepClone();
            return MergeResult(final, reply);
        }
        var activePatch = (JsonObject)patch.DeepClone(); activePatch["completed"] = false; activePatch["due_date"] = next;
        var active = UpdateFields(original, activePatch);
        if (CloudKit.Field(original, "StartDate") is JsonValue start && start.TryGetValue<long>(out var startMs))
        {
            var shift = CloudKit.Field(new JsonObject { ["fields"] = active.DeepClone() }, "DueDate")!.GetValue<long>() - CloudKit.Field(original, "DueDate")!.GetValue<long>();
            active["StartDate"] = CloudKit.Timestamp(startMs + shift); AddTokens(active, "startDate");
        }
        var past = (JsonObject)original["fields"]!.DeepClone();
        foreach (var pair in UpdateFields(original, patch)) past[pair.Key] = pair.Value?.DeepClone();
        foreach (var link in new[] { "RecurrenceRuleIDs", "AlarmIDs", "HashtagIDs", "AttachmentIDs" })
            past[link] = J.Node(new { type = "STRING_LIST", value = Array.Empty<string>() });
        past["CreationDate"] = CloudKit.Timestamp(now.ToUnixTimeMilliseconds());
        AddTokens(past, "recurrenceRuleIDs", "alarmIDs", "attachmentIDs", "creationDate");
        var operations = new JsonArray(
            CloudKit.Operation("update", id, original.Text("recordChangeTag"), active),
            CloudKit.Operation("create", historyId, null, past, parent: CloudKit.Field(original, "List").Text("recordName")));
        var remaining = CloudKit.Int(rule, "OccurrenceCount");
        if (remaining > 1)
            operations.Add(CloudKit.Operation("update", ruleId, rule.Text("recordChangeTag"), new() { ["OccurrenceCount"] = CloudKit.Number(remaining - consumed) }, "RecurrenceRule"));
        var result = await Cloud.Modify(operations, token);
        var merged = original.DeepClone(); foreach (var pair in active) merged["fields"]![pair.Key] = pair.Value?.DeepClone();
        var reminder = MergeResult(merged, result);
        reminder["completed_occurrence"] = MergeResult(new JsonObject { ["recordName"] = historyId, ["fields"] = past.DeepClone() }, result);
        return reminder;
    }
}
