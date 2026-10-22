using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed class Cache : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private static readonly string[] Flags = ["completed", "flagged", "all_day", "deleted", "is_group", "resolved"];
    internal static readonly string[] EditFields = ["title", "description", "due_date", "priority", "completed", "flagged", "list_id", "deleted", "all_day", "recurrence", "recurrence_ids", "time_zone"];
    public Cache(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        // Migrate before creating indexes, including caches from pre-v2 releases.
        if (Query("SELECT name FROM sqlite_master WHERE type='table' AND name='reminders'").Count > 0 && !Query("PRAGMA table_info(reminders)").Any(r => r.Text("name") == "completed_date")) Execute("ALTER TABLE reminders ADD COLUMN completed_date TEXT");
        using var stream = typeof(Cache).Assembly.GetManifestResourceStream("Reminders.Core.Schema.sql")!;
        using var reader = new StreamReader(stream);
        Execute(reader.ReadToEnd());
        var columns = Query("PRAGMA table_info(reminders)").Select(r => r.Text("name")).ToHashSet();
        foreach (var column in new[] { "recurrence", "recurrence_ids", "time_zone" })
            if (!columns.Contains(column)) Execute($"ALTER TABLE reminders ADD COLUMN {column} TEXT");
        if (Meta("schema_version") != "3" && Settings.Number("sync_minutes") == 10)
            SetSettings(new() { ["sync_minutes"] = 1 });
        SetMeta("schema_version", "3");
    }
    private SqliteCommand Command(string sql, object?[] args)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("@p" + i, args[i] ?? DBNull.Value);
        return command;
    }
    public void Execute(string sql, params object?[] args) { lock (gate) { using var cmd = Command(sql, args); cmd.ExecuteNonQuery(); } }
    public JsonArray Query(string sql, params object?[] args)
    {
        lock (gate)
        {
            using var cmd = Command(sql, args); using var reader = cmd.ExecuteReader(); var rows = new JsonArray();
            while (reader.Read())
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i); var value = reader.GetValue(i);
                    row[name] = value is DBNull ? null : Flags.Contains(name) ? JsonValue.Create(Convert.ToInt64(value) != 0) : J.Node(value);
                }
                rows.Add(row);
            }
            return rows;
        }
    }
    // Mutations and their queue entry commit together; a crash cannot save an
    // offline edit without saving the corresponding upload.
    public T Transaction<T>(Func<T> action)
    {
        lock (gate)
        {
            Execute("BEGIN IMMEDIATE");
            try { var result = action(); Execute("COMMIT"); return result; }
            catch { Execute("ROLLBACK"); throw; }
        }
    }
    public string? Meta(string key) => Query("SELECT value FROM meta WHERE key=@p0", key).FirstOrDefault().Text("value");
    public void SetMeta(string key, string? value)
    {
        if (value is null) Execute("DELETE FROM meta WHERE key=@p0", key);
        else Execute("INSERT INTO meta(key,value) VALUES(@p0,@p1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    }
    public JsonArray Lists
    {
        get
        {
            var order = Settings.Array("list_order").Select(v => v?.ToString()).ToList();
            var rows = Query("SELECT l.*, (SELECT COUNT(*) FROM reminders r WHERE r.list_id=l.id AND r.completed=0 AND r.deleted=0) AS open_count FROM lists l ORDER BY l.position");
            return new JsonArray(rows.OrderBy(l => { var index = order.IndexOf(l.Text("id")); return index < 0 ? int.MaxValue : index; }).Select(l => l?.DeepClone()).ToArray());
        }
    }
    public void ReplaceLists(JsonArray lists) => Transaction(() =>
    {
        Execute("DELETE FROM lists");
        for (var i = 0; i < lists.Count; i++) { var l = lists[i]!; Execute("INSERT INTO lists(id,title,color_hex,count,is_group,position) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)", l.Text("id"), l.Text("title"), l.Text("color_hex"), l.Number("count"), l.Flag("is_group"), i); }
        return true;
    });
    public JsonObject? Reminder(string id)
    {
        lock (gate) { var row = Query("SELECT * FROM reminders WHERE id=@p0", id).FirstOrDefault() as JsonObject; if (row is not null) AttachTags(row); return row; }
    }
    private void AttachTags(JsonObject row)
    {
        row["tags"] = new JsonArray(Query("SELECT name FROM tags WHERE reminder_id=@p0 ORDER BY name", row.Text("id")).Select(t => JsonValue.Create(t.Text("name"))).ToArray());
        row["hashtag_ids"] = new JsonArray(Query("SELECT id FROM tags WHERE reminder_id=@p0 ORDER BY name", row.Text("id")).Select(t => JsonValue.Create(t.Text("id"))).ToArray());
        row["recurrence_ids"] = J.Parse(row.Text("recurrence_ids")) ?? new JsonArray();
        row["recurrence"] = J.Parse(row.Text("recurrence"));
        if (row["recurrence"] is null && row.Array("recurrence_ids").FirstOrDefault()?.ToString() is { } ruleId)
        {
            var rule = J.Parse(Query("SELECT json FROM related_records WHERE id=@p0", ruleId.StartsWith("RecurrenceRule/") ? ruleId : "RecurrenceRule/" + ruleId).FirstOrDefault().Text("json"));
            row["recurrence"] = rule is null ? J.Node(new { supported = false }) : Recurrence.Describe(rule);
        }
        if (row.Array("recurrence_ids").Count > 1 && row["recurrence"] is JsonObject repeat) repeat["supported"] = false;
    }
    public JsonArray Reminders(JsonNode p)
    {
        lock (gate)
        {
            var scope = p.Text("scope"); var args = new List<object?>();
            string Param(object? v) { args.Add(v); return "@p" + (args.Count - 1); }
            var sql = "SELECT DISTINCT r.* FROM reminders r ";
            if (p.Text("tag") is { } tag) sql += "JOIN tags t ON t.reminder_id=r.id AND t.name=" + Param(tag) + " ";
            sql += scope == "deleted" ? "WHERE r.deleted=1 " : "WHERE r.deleted=0 ";
            sql += scope switch { "today" => "AND r.completed=0 AND r.due_date IS NOT NULL AND r.due_date<" + Param(TimeUtil.Tomorrow) + " ", "upcoming" => "AND r.completed=0 AND r.due_date IS NOT NULL ", "completed" => "AND r.completed=1 ", "deleted" => "", _ => p.Flag("include_completed") ? "" : "AND r.completed=0 " };
            if (p.Text("list_id") is { } list) sql += "AND r.list_id=" + Param(list) + " ";
            if (p.Optional("search", 4096) is { Length: > 0 } search) sql += "AND (r.title LIKE " + Param("%" + search + "%") + " OR r.description LIKE " + Param("%" + search + "%") + ") ";
            var order = p.Text("sort") switch
            {
                "title" => "r.title COLLATE NOCASE,r.due_date", "title_desc" => "r.title COLLATE NOCASE DESC", "due" => "r.due_date IS NULL,r.due_date,r.title COLLATE NOCASE", "due_desc" => "r.due_date IS NULL,r.due_date DESC", "priority" => "CASE r.priority WHEN 1 THEN 0 WHEN 5 THEN 1 WHEN 9 THEN 2 ELSE 3 END,r.due_date IS NULL,r.due_date", "created" => "r.created DESC", "created_asc" => "r.created", _ => scope == "completed" ? "r.completed_date DESC NULLS LAST,r.modified DESC" : "r.completed,r.due_date IS NULL,r.due_date,r.title COLLATE NOCASE"
            };
            var rows = Query(sql + "ORDER BY " + order + " LIMIT " + Param(Math.Clamp(p.Number("limit", 1000), 1, scope == "completed" ? 50 : 10000)), args.ToArray());
            foreach (var row in rows.OfType<JsonObject>()) AttachTags(row);
            return rows;
        }
    }
    private static object? Sql(JsonNode? v) => v is null ? null : v is JsonValue x && x.TryGetValue<bool>(out var b) ? b ? 1 : 0 : v is JsonValue y && y.TryGetValue<long>(out var n) ? n : v is JsonValue z && z.TryGetValue<string>(out var s) ? s : v.ToJsonString();
    public void Edit(string id, JsonObject fields, bool dirty = true)
    {
        var selected = fields.Where(f => EditFields.Contains(f.Key)).ToArray(); if (selected.Length == 0) return;
        var args = selected.Select(f => Sql(f.Value)).ToList(); args.Add(TimeUtil.Now); args.Add(dirty ? 1 : 0); args.Add(id);
        Execute("UPDATE reminders SET " + string.Join(",", selected.Select((f, i) => f.Key + "=@p" + i)) + $",modified=@p{selected.Length},dirty=@p{selected.Length + 1} WHERE id=@p{selected.Length + 2}", args.ToArray());
    }
    public void Upsert(JsonNode r, bool local = false)
    {
        lock (gate)
        {
            var id = r.Required("id"); var old = Reminder(id);
            if (!local && old.Number("dirty") != 0) return;
            string[] columns = ["id", "list_id", "title", "description", "due_date", "priority", "completed", "completed_date", "flagged", "all_day", "deleted", "created", "modified", "change_tag", "notified", "dirty", "recurrence", "recurrence_ids", "time_zone"];
            var copy = (JsonObject)r.DeepClone(); copy["dirty"] = local ? 1 : 0; copy["notified"] = old.Text("due_date") == r.Text("due_date") ? old.Number("notified") : 0;
            foreach (var key in new[] { "title", "description", "list_id" }) copy[key] ??= "";
            foreach (var key in new[] { "priority", "completed", "flagged", "all_day", "deleted" }) copy[key] ??= 0;
            Execute("INSERT INTO reminders(" + string.Join(",", columns) + ") VALUES(" + string.Join(",", columns.Select((_, i) => "@p" + i)) + ") ON CONFLICT(id) DO UPDATE SET " + string.Join(",", columns.Where(c => c != "id" && c != "created").Select(c => c + "=excluded." + c)), columns.Select(c => Sql(copy[c])).ToArray());
        }
    }
    public JsonArray Tags => Query("SELECT name,COUNT(*) AS n FROM tags t JOIN reminders r ON r.id=t.reminder_id WHERE r.deleted=0 AND r.completed=0 GROUP BY name ORDER BY name");
    public void ApplyRecords(JsonArray records, bool full = false) => Transaction(() =>
    {
        if (full)
        {
            Execute("DELETE FROM lists");
            Execute("DELETE FROM related_records");
            Execute("DELETE FROM tags WHERE reminder_id IN (SELECT id FROM reminders WHERE dirty=0)");
            Execute("UPDATE reminders SET deleted=1 WHERE dirty=0");
        }
        foreach (var record in records.OfType<JsonObject>())
        {
            if (record.Text("recordName") is not { Length: > 0 } id) continue;
            var type = record.Text("recordType") ?? id.Split('/')[0];
            var deleted = record.Flag("deleted") || record.Text("reason") == "deleted" || CloudKit.Int(record, "Deleted") != 0;
            if (deleted)
            {
                Execute("DELETE FROM related_records WHERE id=@p0", id);
                if (type == "List") Execute("DELETE FROM lists WHERE id=@p0", id);
                else if (type == "Hashtag") Execute("DELETE FROM tags WHERE id=@p0 AND NOT EXISTS (SELECT 1 FROM outbox WHERE reminder_id=tags.reminder_id AND op IN ('tag','tags'))", id);
                else if (type == "Reminder" && Reminder(id).Number("dirty") == 0) Edit(id, new() { ["deleted"] = true }, false);
                continue;
            }
            var combined = J.Parse(Query("SELECT json FROM related_records WHERE id=@p0", id).FirstOrDefault().Text("json")) as JsonObject ?? new();
            foreach (var pair in record)
            {
                if (pair.Key == "fields" && combined["fields"] is JsonObject oldFields && pair.Value is JsonObject changed)
                { foreach (var field in changed) oldFields[field.Key] = field.Value?.DeepClone(); }
                else combined[pair.Key] = pair.Value?.DeepClone();
            }
            Execute("INSERT INTO related_records(id,record_type,json) VALUES(@p0,@p1,@p2) ON CONFLICT(id) DO UPDATE SET json=excluded.json", id, type, combined.ToJsonString());
            if (type == "Reminder" && ICloudClient.ToReminder(combined) is { } reminder) Upsert(reminder);
            else if (type == "Hashtag" && CloudKit.Text(combined, "Name") is { } name && CloudKit.Field(combined, "Reminder").Text("recordName") is { } parent && !HasPendingTags(parent))
                Execute("INSERT OR REPLACE INTO tags(id,name,reminder_id) VALUES(@p0,@p1,@p2)", id, name, parent);
            else if (type == "List")
            {
                var rawColor = CloudKit.Text(combined, "Color");
                Execute("INSERT INTO lists(id,title,color_hex,count,is_group,position) VALUES(@p0,@p1,@p2,@p3,@p4,(SELECT COUNT(*) FROM lists)) ON CONFLICT(id) DO UPDATE SET title=excluded.title,color_hex=excluded.color_hex,count=excluded.count,is_group=excluded.is_group",
                    id, CloudKit.Text(combined, "Name") ?? "Untitled", rawColor?.StartsWith('#') == true ? rawColor : J.Parse(rawColor).Text("daHexString"), CloudKit.Int(combined, "Count"), CloudKit.Int(combined, "IsGroup") != 0);
            }
        }
        return true;
    });
    internal bool HasPendingTags(string id) => Query("SELECT 1 FROM outbox WHERE reminder_id=@p0 AND op IN ('tag','tags') LIMIT 1", id).Count > 0;
    public void ReplaceTags(string id, JsonArray tags) => Transaction(() => { ReplaceTagsInTransaction(id, tags); return true; });
    internal void ReplaceTagsInTransaction(string id, JsonArray tags)
    {
        Execute("DELETE FROM tags WHERE reminder_id=@p0", id);
        foreach (var tag in tags) Execute("INSERT OR REPLACE INTO tags(id,name,reminder_id) VALUES(@p0,@p1,@p2)", tag.Text("id"), tag.Text("name"), id);
    }
    internal void QueueTagRepairs(HashSet<string> attempted) => Transaction(() =>
    {
        foreach (var row in Query("SELECT json FROM related_records WHERE record_type='Hashtag'"))
        {
            var record = J.Parse(row.Text("json"));
            if (record is null || !ReminderTags.IsLegacy(record)) continue;
            var id = CloudKit.Field(record, "Reminder").Text("recordName")!;
            var parent = Reminder(id);
            if (parent is null || parent.Flag("deleted") || parent.Number("dirty") != 0
                || Query("SELECT 1 FROM outbox WHERE reminder_id=@p0 UNION ALL SELECT 1 FROM conflicts WHERE reminder_id=@p0 AND resolved=0", id).Count > 0) continue;
            var raw = J.Parse(Query("SELECT json FROM related_records WHERE id=@p0", id).FirstOrDefault().Text("json"));
            if (raw is null || CloudKit.Field(raw, "HashtagIDs") is not JsonArray ids || !ids.Any(v => ReminderTags.RecordName(v!.ToString()) == record.Text("recordName"))) continue;
            if (!attempted.Add(id)) continue;
            Enqueue(id, "repair_tags", new JsonObject { ["repair_only"] = true }, parent.Text("change_tag"));
            Execute("UPDATE reminders SET dirty=1 WHERE id=@p0", id);
        }
        return true;
    });
    public JsonNode Counts
    {
        get { lock (gate) { long Count(string where, params object?[] args) => Query("SELECT COUNT(*) AS n FROM reminders WHERE " + where, args)[0].Number("n"); return J.Node(new { today = Count("deleted=0 AND completed=0 AND due_date IS NOT NULL AND due_date<@p0", TimeUtil.Tomorrow), upcoming = Count("deleted=0 AND completed=0 AND due_date IS NOT NULL"), all = Count("deleted=0 AND completed=0"), completed = Count("deleted=0 AND completed=1"), deleted = Count("deleted=1") }); } }
    }
    public JsonObject Settings
    {
        get
        {
            var defaults = (JsonObject)J.Node(new { theme = "system", sync_minutes = 1, notifications_enabled = true, stale_after_minutes = 60, max_individual_toasts = 3, default_list_id = (string?)null, search_scope = "list", onboarded = false, print_group_by = "due", print_include_notes = true, print_include_completed = false, remember_password = true, sort_by = new { }, list_order = Array.Empty<string>() });
            if (J.Parse(Meta("settings")) is JsonObject stored) foreach (var pair in stored) if (defaults.ContainsKey(pair.Key)) defaults[pair.Key] = pair.Value?.DeepClone();
            return defaults;
        }
    }
    public JsonNode SetSettings(JsonObject patch)
    {
        if (patch.ContainsKey("list_order") && (patch["list_order"] is not JsonArray order || order.Count > 10000 || order.Any(v => v is not JsonValue text || !text.TryGetValue<string>(out var id) || id.Length is 0 or > 2048)))
            throw CoreException.Bad("List order must contain list identifiers.");
        lock (gate) { var s = Settings; foreach (var p in patch) if (s.ContainsKey(p.Key)) s[p.Key] = p.Value?.DeepClone(); SetMeta("settings", s.ToJsonString()); return s; }
    }
    public void Enqueue(string id, string op, JsonNode payload, string? tag) => Execute("INSERT INTO outbox(reminder_id,op,payload,base_tag,created_at) VALUES(@p0,@p1,@p2,@p3,@p4)", id, op, payload.ToJsonString(), tag, TimeUtil.Now);
    public JsonArray Pending(int limit = 100) => Query("SELECT * FROM outbox ORDER BY seq LIMIT @p0", limit);
    // Keep edits to a single reminder in order, while allowing another reminder
    // past an earlier rejected upload. Successful creates may remap queued IDs.
    public JsonNode? NextUpload(long afterSeq) => Query("SELECT * FROM outbox AS next WHERE seq>@p0 AND NOT EXISTS (SELECT 1 FROM outbox AS earlier WHERE earlier.reminder_id=next.reminder_id AND earlier.seq<next.seq) ORDER BY seq LIMIT 1", afterSeq).FirstOrDefault();
    public void Acknowledge(long seq, string id, string? tag) => Transaction(() => { Execute("DELETE FROM outbox WHERE seq=@p0", seq); Execute("UPDATE outbox SET base_tag=@p0 WHERE reminder_id=@p1 AND seq>@p2", tag, id, seq); Execute("UPDATE reminders SET change_tag=@p0,dirty=EXISTS(SELECT 1 FROM outbox WHERE reminder_id=@p1) WHERE id=@p1", tag, id); return true; });
    public string ResolveId(string id) => Meta("id_alias:" + id) ?? id;
    public void ReplaceId(string oldId, string newId) => Transaction(() => { Execute("UPDATE reminders SET id=@p0 WHERE id=@p1", newId, oldId); foreach (var table in new[] { "tags", "outbox" }) Execute($"UPDATE {table} SET reminder_id=@p0 WHERE reminder_id=@p1", newId, oldId); SetMeta("id_alias:" + oldId, newId); return true; });
    public JsonArray Conflicts
    {
        get { var rows = Query("SELECT * FROM conflicts WHERE resolved=0 ORDER BY detected_at DESC"); foreach (var row in rows.OfType<JsonObject>()) { row["local"] = J.Parse(row.Text("local_json")); row["remote"] = J.Parse(row.Text("remote_json")); row.Remove("local_json"); row.Remove("remote_json"); } return rows; }
    }
    private void PurgeData()
    {
        foreach (var table in new[] { "lists", "reminders", "tags", "outbox", "conflicts", "related_records" }) Execute($"DELETE FROM {table}");
        foreach (var key in new[] { "sync_cursor", "last_sync", "last_full_sync", "cloudkit_read_protocol" }) SetMeta(key, null);
        Execute("DELETE FROM meta WHERE key GLOB 'id_alias:*'");
    }
    public void Purge() => Transaction(() => { PurgeData(); return true; });
    public JsonNode Clear(bool discardPending) => Transaction(() =>
    {
        var pending = Query("SELECT COUNT(*) AS count FROM outbox")[0].Number("count");
        var conflicts = Query("SELECT COUNT(*) AS count FROM conflicts WHERE resolved=0")[0].Number("count");
        if ((pending > 0 || conflicts > 0) && !discardPending)
            throw new CoreException("PENDING_CHANGES", "Confirm discarding unsynced changes and conflicts before clearing the cache.");
        PurgeData();
        return J.Node(new { cleared = true, discarded_pending = pending, discarded_conflicts = conflicts });
    });
    public void Dispose() { lock (gate) connection.Dispose(); }
}
