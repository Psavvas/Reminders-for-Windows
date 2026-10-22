using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class Cache
{
    public long PendingCount => Query("SELECT COUNT(*) AS n FROM outbox")[0].Number("n");

    public JsonArray UnsyncedReminders()
    {
        lock (gate)
        {
            var rows = new JsonArray();
            foreach (var item in Query("SELECT reminder_id FROM outbox UNION SELECT reminder_id FROM conflicts WHERE resolved=0"))
            {
                var id = item!.Required("reminder_id");
                var pending = Query("SELECT seq,reminder_id,op,payload,base_tag FROM outbox WHERE reminder_id=@p0 ORDER BY seq", id);
                var conflicts = Query("SELECT id,reminder_id,local_json,remote_json FROM conflicts WHERE reminder_id=@p0 AND resolved=0 ORDER BY id", id);
                var create = pending.FirstOrDefault(p => p.Text("op") == "create");
                var cloudId = create is null ? id : J.Parse(create.Text("payload")).Text("cloud_id");
                var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new JsonArray(pending, conflicts).ToJsonString())));
                rows.Add(new JsonObject
                {
                    ["id"] = id, ["title"] = Reminder(id).Text("title") ?? "", ["pending_changes"] = pending.Count,
                    ["conflicts"] = conflicts.Count, ["is_new"] = create is not null, ["cloud_id"] = cloudId, ["version"] = version
                });
            }
            return new JsonArray(rows.OrderBy(r => r.Text("title"), StringComparer.CurrentCultureIgnoreCase).Select(r => r!.DeepClone()).ToArray());
        }
    }

    internal JsonArray ValidateDiscard(JsonArray targets)
    {
        if (targets.Count is 0 or > 1000) throw CoreException.Bad("Select between 1 and 1,000 reminders to discard.");
        var current = UnsyncedReminders().ToDictionary(r => r!.Required("id"), r => r!);
        var seen = new HashSet<string>(); var selected = new JsonArray();
        foreach (var target in targets)
        {
            if (target is not JsonObject item || !seen.Add(item.Required("id"))) throw CoreException.Bad("Select each reminder only once.");
            var id = item.Required("id");
            if (!current.TryGetValue(id, out var row) || item.Required("version") != row.Required("version"))
                throw new CoreException("STALE_SELECTION", "These reminders changed while you were reviewing them. Refresh the list and choose again.");
            selected.Add(row.DeepClone());
        }
        return selected;
    }

    internal JsonNode DiscardUnsynced(JsonArray targets, IReadOnlyDictionary<string, JsonObject?> snapshots, CancellationToken token = default) => Transaction(() =>
    {
        var selected = ValidateDiscard(targets); var removed = 0; var restored = 0;
        var cachedRecords = Query("SELECT id,json FROM related_records");
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            var id = row!.Required("id");
            var cloudId = row.Text("cloud_id");
            if (cloudId is not null && !snapshots.ContainsKey(cloudId)) throw CoreException.Bad("Refresh iCloud before discarding these changes.");
            var remote = cloudId is null ? null : snapshots[cloudId];
            Execute("DELETE FROM outbox WHERE reminder_id=@p0", id);
            Execute("UPDATE conflicts SET resolved=1 WHERE reminder_id=@p0 AND resolved=0", id);
            Execute("DELETE FROM tags WHERE reminder_id=@p0", id);
            Execute("DELETE FROM reminders WHERE id=@p0", id);
            // Downloaded records belong to this task only; all other cache data
            // and the zone cursor remain intact. No cloud record is deleted.
            foreach (var cached in cachedRecords)
            {
                var record = J.Parse(cached.Text("json"));
                var parent = record?["parent"].Text("recordName");
                var owner = record is null ? null : CloudKit.Field(record, "Reminder").Text("recordName");
                var type = record.Text("recordType") ?? cached.Text("id")?.Split('/')[0];
                var ownedLink = type is "Hashtag" or "RecurrenceRule"
                    && (parent == id || cloudId is not null && parent == cloudId || owner == (cloudId ?? id));
                if (cached.Text("id") == id || cloudId is not null && cached.Text("id") == cloudId || ownedLink)
                    Execute("DELETE FROM related_records WHERE id=@p0", cached.Text("id"));
            }
            if (remote is null) { SetMeta("id_alias:" + id, null); removed++; }
            else
            {
                if (id != remote.Required("id")) SetMeta("id_alias:" + id, remote.Required("id"));
                Upsert(remote); StoreDiscardSnapshot(remote); restored++;
            }
        }
        token.ThrowIfCancellationRequested();
        return J.Node(new { discarded = selected.Count, removed_local = removed, restored_from_icloud = restored });
    });

    private void StoreDiscardSnapshot(JsonObject snapshot)
    {
        foreach (var record in snapshot.Array("related_records").Prepend(snapshot["cloud_record"]).OfType<JsonObject>())
        {
            var id = record.Required("recordName"); var type = record.Text("recordType") ?? id.Split('/')[0];
            if (record.Flag("deleted") || record.Text("reason") == "deleted" || CloudKit.Int(record, "Deleted") != 0) continue;
            Execute("INSERT INTO related_records(id,record_type,json) VALUES(@p0,@p1,@p2) ON CONFLICT(id) DO UPDATE SET json=excluded.json", id, type, record.ToJsonString());
            if (type == "Hashtag" && CloudKit.Text(record, "Name") is { } name)
                Execute("INSERT OR REPLACE INTO tags(id,name,reminder_id) VALUES(@p0,@p1,@p2)", id, name, snapshot.Required("id"));
        }
    }
}
