using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class ICloudClient
{
    internal async Task<Dictionary<string, JsonObject?>> RecoverySnapshots(IEnumerable<string> ids, CancellationToken token)
    {
        var result = new Dictionary<string, JsonObject?>();
        foreach (var batch in ids.Distinct().Chunk(100))
        {
            var records = await Cloud.LookupMany(batch, token, true, true);
            foreach (var id in batch)
            {
                var record = records.FirstOrDefault(r => r.Text("recordName") == id);
                if (record is null || record.Flag("deleted") || record.Text("reason") == "deleted" || CloudKit.Int(record, "Deleted") != 0) { result[id] = null; continue; }
                if (record["fields"] is not JsonObject || string.IsNullOrEmpty(record.Text("recordChangeTag"))
                    || record.Text("recordType") is { } type && type != "Reminder")
                    throw new CoreException("NETWORK", "iCloud returned an incomplete reminder. No local changes were discarded.");
                var snapshot = Snapshot(record);
                var links = new[] { ("HashtagIDs", "Hashtag/"), ("RecurrenceRuleIDs", "RecurrenceRule/") }
                    .SelectMany(link => (CloudKit.Field(record, link.Item1) as JsonArray ?? new())
                        .Select(value => value!.ToString().StartsWith(link.Item2, StringComparison.Ordinal) ? value.ToString() : link.Item2 + value));
                var related = new JsonArray();
                foreach (var linkedBatch in links.Distinct().Chunk(100))
                    foreach (var item in await Cloud.LookupMany(linkedBatch, token, true, true)) related.Add(item!.DeepClone());
                snapshot["related_records"] = related; result[id] = snapshot;
            }
        }
        return result;
    }
}
