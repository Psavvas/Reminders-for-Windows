using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class ReminderListEditingChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-list-editing-" + Guid.NewGuid().ToString("N"));
        var original = new JsonObject
        {
            ["recordName"] = "Reminder/task", ["recordType"] = "Reminder", ["recordChangeTag"] = "original",
            ["parent"] = J.Node(new { recordName = "List/source" }),
            ["fields"] = new JsonObject
            {
                ["List"] = CloudKit.Reference("List/source"), ["TitleDocument"] = CloudKit.String(AppleDocument.Encode("Flight plan")),
                ["NotesDocument"] = CloudKit.String(AppleDocument.Encode("Keep these notes")), ["Completed"] = CloudKit.Number(0),
                ["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "tag" } }),
                ["ResolutionTokenMap"] = CloudKit.String("{\"map\":{\"list\":{\"counter\":4},\"notesDocument\":{\"counter\":2}}}")
            }
        };
        async Task<JsonNode> Call(ReminderService service, string method, object parameters) =>
            J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
        try
        {
            using (var seed = new Cache(Path.Combine(root, "cache.db")))
            {
                seed.ReplaceLists(new JsonArray(J.Node(new { id = "List/source", title = "Source" }),
                    J.Node(new { id = "List/destination", title = "Destination" }), J.Node(new { id = "List/group", title = "Group", is_group = true })));
                seed.Upsert(ICloudClient.ToReminder(original)!);
            }
            string draftId;
            await using (var service = new ReminderService(root, new MemorySecrets()))
            {
                var moved = await Call(service, "update_reminder", new { id = "Reminder/task", list_id = "List/destination", title = "Updated flight plan" });
                check(moved.Text("list_id") == "List/destination" && moved.Text("description") == "Keep these notes", "offline list move preserves other reminder fields");
                check((await Call(service, "reminders", new { list_id = "List/source" })).AsArray().Count == 0
                    && (await Call(service, "reminders", new { list_id = "List/destination" })).AsArray().Count == 1, "moved task immediately appears only in its destination list");
                foreach (var invalid in new object?[] { "", null, 42, "List/missing", "List/group" })
                {
                    try { await Call(service, "update_reminder", new { id = "Reminder/task", list_id = invalid, title = "Must not save" }); check(false, "invalid destination rejected"); }
                    catch (CoreException error) { check(error.Code == "BAD_REQUEST", "missing, malformed and group destinations are rejected"); }
                }
                var unchanged = await Call(service, "reminder", new { id = "Reminder/task" });
                check(unchanged.Text("title") == "Updated flight plan" && unchanged.Text("list_id") == "List/destination", "invalid moves cannot partially save other edits");
                draftId = (await Call(service, "create_reminder", new { list_id = "List/source", title = "Offline draft" })).Required("id");
                await Call(service, "update_reminder", new { id = draftId, list_id = "List/destination" });
            }
            await using (var reopened = new ReminderService(root, new MemorySecrets()))
                check((await Call(reopened, "reminder", new { id = draftId })).Text("list_id") == "List/destination", "moving a never-synced draft survives restart");

            using var cache = new Cache(Path.Combine(root, "cache.db"));
            check(cache.Pending().Count == 3 && cache.Pending().Count(row => J.Parse(row.Text("payload")).Text("list_id") == "List/destination") == 2,
                "existing and never-synced task moves are durably queued");
            var records = new Dictionary<string, JsonObject> { ["Reminder/task"] = original.DeepClone().AsObject() };
            var secrets = new SecretStore(new MemorySecrets()); secrets.Session("test@example.com", J.Node(new { session_token = "test" }).ToJsonString());
            var writes = 0;
            HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
            using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath; var body = J.Parse(await request.Content!.ReadAsStringAsync())!;
                if (path.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
                if (path.EndsWith("/records/lookup")) return Reply(new JsonObject { ["records"] = new JsonArray(body.Array("records").Select(row => records.TryGetValue(row!.Required("recordName"), out var record)
                    ? record.DeepClone() : J.Node(new { recordName = row.Text("recordName"), serverErrorCode = "NOT_FOUND" })).ToArray()) });
                if (path.EndsWith("/changes/zone")) return Reply(J.Node(new { zones = new[] { new { records = records.Values.ToArray(), syncToken = "cursor" } } }));
                if (!path.EndsWith("/records/modify")) throw new InvalidOperationException("Unexpected endpoint");
                var results = new JsonArray(); writes++;
                foreach (var operation in body.Array("operations"))
                {
                    var supplied = operation!["record"]!; var id = supplied.Required("recordName");
                    if (supplied["fields"]?["List"] is not null)
                        check(supplied["parent"].Text("recordName") == CloudKit.Field(supplied, "List").Text("recordName"), "list writes also update the CloudKit parent");
                    if (operation.Text("operationType") == "create") records[id] = supplied.DeepClone().AsObject();
                    else
                    {
                        check(supplied.Text("recordChangeTag") == records[id].Text("recordChangeTag"), "queued moves use the latest revision after earlier uploads");
                        foreach (var pair in supplied["fields"]!.AsObject()) records[id]["fields"]![pair.Key] = pair.Value?.DeepClone();
                        if (supplied["parent"] is { } parent) records[id]["parent"] = parent.DeepClone();
                    }
                    records[id]["recordChangeTag"] = writes.ToString();
                    // Partial replies omit parent and all untouched fields.
                    results.Add(new JsonObject { ["recordName"] = id, ["recordChangeTag"] = writes.ToString(), ["fields"] = supplied["fields"]!.DeepClone() });
                }
                return Reply(new JsonObject { ["records"] = results });
            }));
            check(await client.Restore(default), "list editing mock account restores");
            var sync = new SyncEngine(cache, client, new SemaphoreSlim(1), (_, _) => { });
            await sync.Sync(true, default);
            check(cache.Pending().Count == 0 && cache.Reminders(J.Node(new { list_id = "List/destination" })).Count == 2, "queued moves sync for existing tasks and newly mapped draft IDs");
            var saved = records["Reminder/task"];
            var map = J.Parse(CloudKit.Text(saved, "ResolutionTokenMap"))!["map"]!;
            check(map["list"].Number("counter") == 5 && map["notesDocument"].Number("counter") == 2, "list moves advance their resolution token and preserve unrelated tokens");
            check(CloudKit.Field(saved, "HashtagIDs")!.AsArray().Count == 1 && cache.Reminder("Reminder/task").Text("description") == "Keep these notes", "syncing a move preserves notes and linked tags");

            var due = TimeUtil.Iso(DateTimeOffset.UtcNow.AddDays(1));
            var repeated = await client.Update("Reminder/task", J.Node(new { list_id = "List/source", due_date = due,
                recurrence = new { frequency = 2, interval = 1 }, recurrence_id = Guid.NewGuid().ToString() }).AsObject(), saved.Text("recordChangeTag"), default);
            check(repeated.Text("list_id") == "List/source" && repeated["cloud_record"]!["parent"].Text("recordName") == "List/source"
                && repeated.Array("related_records").Count == 1, "moving and editing a repeat together preserves the destination in a partial response");
            var beforeConflict = writes;
            try { await client.Update("Reminder/task", new() { ["list_id"] = "List/destination" }, "stale", default); check(false, "stale move rejected"); }
            catch (CoreException error) { check(error.Code == "CONFLICT" && writes == beforeConflict, "moving a reminder retains conflict protection"); }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
