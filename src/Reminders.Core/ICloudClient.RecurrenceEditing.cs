using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class ICloudClient
{
    private async Task<JsonObject> ChangeRecurrence(JsonNode original, JsonObject patch, string? baseTag, CancellationToken token)
    {
        if (patch.Flag("completed")) throw CoreException.Bad("Save the repeat change before completing this reminder.");
        var id = original.Required("recordName");
        var identity = patch.Required("recurrence_id");
        if (!Guid.TryParse(identity, out var editId)) throw CoreException.Bad("Refresh this reminder before changing its repeat.");
        identity = editId.ToString().ToUpperInvariant();
        var ids = CloudKit.Field(original, "RecurrenceRuleIDs") as JsonArray ?? new();
        var map = J.Parse(CloudKit.Text(original, "ResolutionTokenMap"));
        // Store the stable edit identity in the existing resolution token. A
        // committed edit whose reply was lost is recovered before revision
        // comparison, including removing a repeat (which has no new rule ID).
        if (map?["map"]?["recurrenceRuleIDs"].Text("replicaID") == identity)
        {
            var recovered = Snapshot(original);
            if (ids.Count > 0) recovered["related_records"] = await Cloud.LookupMany(ids.Select(v => RuleName(v!.ToString())), token);
            return recovered;
        }
        if (baseTag is not null && baseTag != original.Text("recordChangeTag"))
            throw new CoreException("CONFLICT", "This reminder changed on another device while you were editing its repeat.", ToReminder(original)!.ToJsonString());
        if (ids.Count > 1) throw CoreException.Bad("Edit this custom repeat on an Apple device to preserve its schedule.");
        var repeat = Recurrence.Validate(patch["recurrence"], patch.ContainsKey("due_date") ? patch.Text("due_date") : ToReminder(original)!.Text("due_date"));
        JsonNode? rule = null;
        if (ids.Count == 1)
        {
            rule = (await Cloud.Lookup(RuleName(ids[0]!.ToString()), token)).FirstOrDefault() ?? throw CoreException.Bad("iCloud did not return this reminder's repeat rule.");
            if (!Recurrence.Describe(rule).Flag("supported") || CloudKit.Int(rule, "Deleted") != 0)
                throw CoreException.Bad("Edit this custom repeat on an Apple device to preserve its schedule.");
        }
        var fields = UpdateFields(original, patch);
        var operations = new JsonArray();
        JsonNode? changedRule = null;
        if (repeat is not null)
        {
            var ruleId = rule?.Text("recordName") ?? "RecurrenceRule/" + identity;
            var ruleFields = Recurrence.Fields(id, repeat);
            if (rule is null)
            {
                changedRule = new JsonObject { ["recordName"] = ruleId, ["recordType"] = "RecurrenceRule", ["fields"] = ruleFields.DeepClone() };
                operations.Add(CloudKit.Operation("create", ruleId, null, ruleFields, "RecurrenceRule", id));
            }
            else
            {
                // Keep the rule's end date, remaining count and other metadata.
                var changed = new JsonObject { ["Frequency"] = ruleFields["Frequency"]!.DeepClone(), ["Interval"] = ruleFields["Interval"]!.DeepClone() };
                changedRule = rule.DeepClone();
                foreach (var pair in changed) changedRule["fields"]![pair.Key] = pair.Value?.DeepClone();
                operations.Add(CloudKit.Operation("update", ruleId, rule.Text("recordChangeTag"), changed, "RecurrenceRule"));
            }
            fields["RecurrenceRuleIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { ruleId["RecurrenceRule/".Length..] } });
        }
        else
        {
            fields["RecurrenceRuleIDs"] = J.Node(new { type = "STRING_LIST", value = Array.Empty<string>() });
            if (rule is not null)
            {
                changedRule = rule.DeepClone(); changedRule["fields"]!["Deleted"] = CloudKit.Number(1);
                operations.Add(CloudKit.Operation("update", rule.Required("recordName"), rule.Text("recordChangeTag"), new() { ["Deleted"] = CloudKit.Number(1) }, "RecurrenceRule"));
            }
        }
        AddTokens(fields, "recurrenceRuleIDs");
        var tokens = J.Parse(fields["ResolutionTokenMap"].Text("value"))!;
        tokens["map"]!["recurrenceRuleIDs"]!["replicaID"] = identity;
        fields["ResolutionTokenMap"] = CloudKit.String(tokens.ToJsonString());
        operations.Insert(0, CloudKit.Operation("update", id, original.Text("recordChangeTag"), fields));
        JsonArray result;
        try { result = await Cloud.Modify(operations, token); }
        catch (CoreException error) when (error.Code == "CONFLICT")
        {
            // Conflict UI expects a reminder, never a linked rule as a new task.
            throw new CoreException("CONFLICT", "This reminder or its repeat changed on another device. Review the conflict before retrying.", ToReminder(original)!.ToJsonString());
        }
        var merged = original.DeepClone();
        foreach (var pair in fields) merged["fields"]![pair.Key] = pair.Value?.DeepClone();
        var reminder = MergeResult(merged, result);
        if (changedRule is not null)
        {
            var returned = result.First(r => r.Text("recordName") == changedRule.Text("recordName"))!;
            if (returned["fields"] is JsonObject changed)
                foreach (var pair in changed) changedRule["fields"]![pair.Key] = pair.Value?.DeepClone();
            changedRule["recordChangeTag"] = returned["recordChangeTag"]?.DeepClone();
            reminder["related_records"] = new JsonArray(changedRule);
        }
        return reminder;
    }

    private static string RuleName(string id) => id.StartsWith("RecurrenceRule/", StringComparison.Ordinal) ? id : "RecurrenceRule/" + id;
}
