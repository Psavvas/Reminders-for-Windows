using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class ICloudClient
{
    private static JsonObject TagFields(string parent, string name) => new()
    {
        ["Name"] = CloudKit.String(name), ["Type"] = CloudKit.Number(0), ["Imported"] = CloudKit.Number(0),
        ["Reminder"] = CloudKit.Reference(parent), ["CreationDate"] = CloudKit.Timestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        ["Deleted"] = CloudKit.Number(0)
    };

    public async Task<JsonObject> EditTags(string id, JsonObject payload, string? baseTag, CancellationToken token)
    {
        var original = (await Cloud.Lookup(id, token)).FirstOrDefault() ?? throw CoreException.Bad("No such reminder.");
        var repair = payload.Flag("repair_only");
        var linked = (CloudKit.Field(original, "HashtagIDs") as JsonArray ?? new()).Select(v => ReminderTags.RecordName(v!.ToString())).ToHashSet();
        var desired = payload.Array("tags");
        var names = linked.Concat(desired.Select(t => t!.Required("id"))).Concat(payload.Array("previous_ids").Select(v => v!.ToString())).Distinct().ToArray();
        var records = (names.Length == 0 ? new JsonArray() : await Cloud.LookupMany(names, token, true)).OfType<JsonObject>().ToDictionary(r => r.Required("recordName"));
        JsonObject ConflictSnapshot()
        {
            var snapshot = Snapshot(original);
            snapshot["tag_records"] = new JsonArray(records.Values.Where(r => linked.Contains(r.Required("recordName")) && CloudKit.Int(r, "Deleted") == 0)
                .Select(r => (JsonNode)new JsonObject { ["id"] = r.Required("recordName"), ["name"] = CloudKit.Text(r, "Name") }).ToArray());
            return snapshot;
        }
        var operations = new JsonArray();
        var touched = new List<JsonObject>();
        void Change(JsonObject record, JsonObject fields)
        {
            if (ReminderTags.IsLegacy(record))
            {
                fields["Name"] ??= CloudKit.String(CloudKit.Text(record, "Name") ?? "");
                fields["Type"] = CloudKit.Number(0); fields["Imported"] = CloudKit.Number(0);
            }
            var changed = (JsonObject)record.DeepClone();
            foreach (var pair in fields) changed["fields"]![pair.Key] = pair.Value?.DeepClone();
            touched.Add(changed);
            operations.Add(CloudKit.Operation("update", record.Required("recordName"), record.Text("recordChangeTag"), fields, "Hashtag"));
        }
        if (repair)
        {
            foreach (var record in records.Values.Where(r => linked.Contains(r.Required("recordName")) && ReminderTags.IsLegacy(r)
                && CloudKit.Field(r, "Reminder").Text("recordName") == id && CloudKit.Int(r, "Deleted") == 0))
                Change(record, new() { ["Name"] = CloudKit.String(CloudKit.Text(record, "Name") ?? ""), ["Type"] = CloudKit.Number(0), ["Imported"] = CloudKit.Number(0) });
        }
        else
        {
            foreach (var tag in desired)
            {
                var tagId = tag!.Required("id"); var name = tag!.Required("name");
                if (records.TryGetValue(tagId, out var record))
                {
                    if (CloudKit.Field(record, "Reminder").Text("recordName") != id)
                        throw CoreException.Bad("This tag belongs to another reminder. Refresh before editing tags.");
                    if (CloudKit.Int(record, "Deleted") != 0 || CloudKit.Text(record, "Name") != name || record["fields"]!["Name"].Text("type") != "STRING" || ReminderTags.IsLegacy(record))
                    {
                        var fields = new JsonObject { ["Name"] = CloudKit.String(name), ["Deleted"] = CloudKit.Number(0) };
                        if (ReminderTags.IsLegacy(record)) { fields["Type"] = CloudKit.Number(0); fields["Imported"] = CloudKit.Number(0); }
                        Change(record, fields);
                    }
                }
                else
                {
                    var fields = TagFields(id, name);
                    operations.Add(CloudKit.Operation("create", tagId, null, fields, "Hashtag", id));
                    touched.Add(new() { ["recordName"] = tagId, ["recordType"] = "Hashtag", ["fields"] = fields.DeepClone() });
                }
            }
            var wanted = desired.Select(t => t!.Required("id")).ToHashSet();
            foreach (var record in records.Values.Where(r => (linked.Contains(r.Required("recordName")) || payload.Array("previous_ids").Any(v => v!.ToString() == r.Required("recordName"))) && !wanted.Contains(r.Required("recordName"))
                && CloudKit.Field(r, "Reminder").Text("recordName") == id && CloudKit.Int(r, "Deleted") == 0))
                Change(record, new() { ["Deleted"] = CloudKit.Number(1) });
        }
        var desiredIds = repair ? linked : desired.Select(t => t!.Required("id")).ToHashSet();
        // Recover an atomic write whose reply was lost, before comparing revisions.
        // This also covers removing the final tag, without adding a private token.
        if (operations.Count == 0 && linked.SetEquals(desiredIds))
        {
            var recovered = Snapshot(original); recovered["related_records"] = new JsonArray(records.Values.Select(r => r.DeepClone()).ToArray()); return recovered;
        }
        if (!repair && baseTag is not null && baseTag != original.Text("recordChangeTag"))
            throw new CoreException("CONFLICT", "This reminder changed on another device while you were editing its tags.", ConflictSnapshot().ToJsonString());
        var parentFields = UpdateFields(original, new());
        if (!repair) parentFields["HashtagIDs"] = new JsonObject { ["type"] = "STRING_LIST", ["value"] = new JsonArray(desiredIds.Select(v => JsonValue.Create(v["Hashtag/".Length..])).ToArray()) };
        operations.Insert(0, CloudKit.Operation("update", id, original.Text("recordChangeTag"), parentFields));
        JsonArray result;
        try { result = await Cloud.Modify(operations, token); }
        catch (CoreException error) when (error.Code == "CONFLICT")
        { throw new CoreException("CONFLICT", "This reminder or its tags changed on another device. Review the conflict before retrying.", ConflictSnapshot().ToJsonString()); }
        foreach (var pair in parentFields) original["fields"]![pair.Key] = pair.Value?.DeepClone();
        var saved = MergeResult(original, result);
        foreach (var changed in touched)
        {
            var returned = result.First(r => r.Text("recordName") == changed.Text("recordName"))!;
            changed["recordChangeTag"] = returned["recordChangeTag"]?.DeepClone();
            records[changed.Required("recordName")] = changed;
        }
        await VerifyTags(id, desiredIds, touched, token);
        saved["related_records"] = new JsonArray(records.Values.Select(r => r.DeepClone()).ToArray());
        return saved;
    }

    private async Task VerifyTags(string id, HashSet<string> ids, IEnumerable<JsonObject> expected, CancellationToken token)
    {
        var tags = expected.ToArray();
        var confirmed = (await Cloud.LookupMany(tags.Select(t => t.Required("recordName")).Prepend(id), token, true))
            .ToDictionary(r => r!.Required("recordName"), r => r!);
        var matches = confirmed.TryGetValue(id, out var parent) && ids.SetEquals(
            (CloudKit.Field(parent, "HashtagIDs") as JsonArray ?? new()).Select(v => ReminderTags.RecordName(v!.ToString())));
        foreach (var tag in tags)
        {
            if (!confirmed.TryGetValue(tag.Required("recordName"), out var actual)) { matches = false; continue; }
            foreach (var field in new[] { "Name", "Type", "Imported", "Deleted" })
                if (tag["fields"]!.AsObject().ContainsKey(field) &&
                    (tag["fields"]![field].Text("type") != actual["fields"]?[field].Text("type") || !JsonNode.DeepEquals(CloudKit.Field(tag, field), CloudKit.Field(actual, field)))) matches = false;
        }
        if (!matches) throw new CoreException("OPERATION_REJECTED", "iCloud did not keep the tag changes. They remain saved on this PC and will be retried.");
    }
}
