using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class SubtaskSyncChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-subtask-sync-" + Guid.NewGuid().ToString("N"));
        var records = new Dictionary<string, JsonObject>(); var rejectParent = true; var loseChildReply = true; var revision = 0; var childCreates = 0;
        var parentId = ""; var childId = ""; var otherId = "";
        async Task<JsonNode> Call(ReminderService service, string method, object parameters) => J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
        try
        {
            using (var seed = new Cache(Path.Combine(root, "cache.db"))) seed.ReplaceLists(new JsonArray(J.Node(new { id = "List/a", title = "A" })));
            await using (var service = new ReminderService(root, new MemorySecrets()))
            {
                parentId = (await Call(service, "create_reminder", new { list_id = "List/a", title = "Parent" })).Required("id");
                childId = (await Call(service, "create_reminder", new { list_id = "List/a", parent_id = parentId, title = "Child" })).Required("id");
                otherId = (await Call(service, "create_reminder", new { list_id = "List/a", title = "Unrelated" })).Required("id");
                await Call(service, "update_reminder", new { id = parentId, completed = true });
            }
            using var cache = new Cache(Path.Combine(root, "cache.db"));
            var secrets = new SecretStore(new MemorySecrets()); secrets.Session("test@example.com", J.Node(new { session_token = "test" }).ToJsonString());
            HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
            using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath; var body = J.Parse(await request.Content!.ReadAsStringAsync())!;
                if (path.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
                if (path.EndsWith("/records/lookup")) return Reply(new JsonObject { ["records"] = new JsonArray(body.Array("records").Select(row => records.TryGetValue(row!.Required("recordName"), out var found)
                    ? found.DeepClone() : J.Node(new { recordName = row.Text("recordName"), serverErrorCode = "NOT_FOUND" })).ToArray()) });
                if (path.EndsWith("/changes/zone")) return Reply(J.Node(new { zones = new[] { new { records = records.Values.ToArray(), syncToken = "cursor" } } }));
                if (!path.EndsWith("/records/modify")) throw new InvalidOperationException("Unexpected endpoint");
                var results = new JsonArray();
                foreach (var operation in body.Array("operations"))
                {
                    var supplied = operation!["record"]!; var id = supplied.Required("recordName");
                    if (operation.Text("operationType") == "create")
                    {
                        var title = ICloudClient.ToReminder(supplied)!.Text("title");
                        if (title == "Parent" && rejectParent) return Reply(J.Node(new { records = new[] { new { recordName = id, serverErrorCode = "ACCESS_DENIED" } } }));
                        if (CloudKit.Field(supplied, "ParentReminder").Text("recordName") is { } parent)
                        {
                            childCreates++;
                            check(records.ContainsKey(parent) && !parent.StartsWith("local/"), "subtask uploads wait for the real parent identity");
                            check(CloudKit.Int(records[parent], "Completed") == 0, "queued parent completion never overtakes its earlier child creation");
                            check(supplied["parent"].Text("recordName") == "List/a", "CloudKit list parent is distinct from the subtask's ParentReminder reference");
                            check(J.Parse(CloudKit.Text(supplied, "ResolutionTokenMap"))!["map"]!["parentReminder"].Number("counter") == 1, "subtask creation uses Apple's parentReminder resolution token");
                        }
                        check(!records.ContainsKey(id), "subtask retry cannot create a duplicate record");
                        records[id] = supplied.DeepClone().AsObject();
                    }
                    else
                    {
                        check(supplied.Text("recordChangeTag") == records[id].Text("recordChangeTag"), "subtask edits retain revision protection");
                        foreach (var pair in supplied["fields"]!.AsObject()) records[id]["fields"]![pair.Key] = pair.Value?.DeepClone();
                    }
                    records[id]["recordChangeTag"] = (++revision).ToString();
                    results.Add(new JsonObject { ["recordName"] = id, ["recordChangeTag"] = revision.ToString(), ["fields"] = supplied["fields"]!.DeepClone() });
                    if (operation.Text("operationType") == "create" && CloudKit.Field(supplied, "ParentReminder") is not null && loseChildReply)
                    { loseChildReply = false; throw new HttpRequestException("Child saved, response lost"); }
                }
                return Reply(new JsonObject { ["records"] = results });
            }));
            check(await client.Restore(default), "subtask sync fixture restores");
            var events = new List<string>(); var sync = new SyncEngine(cache, client, new SemaphoreSlim(1), (name, _) => events.Add(name));
            await sync.Sync(true, default).WaitAsync(TimeSpan.FromSeconds(5));
            check(childCreates == 0 && cache.ResolveId(otherId) != otherId && cache.Reminder(parentId).Flag("dirty"), "a rejected parent holds its children while unrelated tasks still upload");
            rejectParent = false;
            try { await sync.Sync(false, default).WaitAsync(TimeSpan.FromSeconds(5)); check(false, "lost child reply must leave its upload retryable"); }
            catch (CoreException error) { check(error.Code == "NETWORK", "lost subtask reply retains the queued create"); }
            var remoteParent = cache.ResolveId(parentId);
            check(cache.Reminder(childId).Text("parent_id") == remoteParent, "parent ID remapping updates its cached children");
            await sync.Sync(false, default).WaitAsync(TimeSpan.FromSeconds(5));
            var remoteChild = cache.ResolveId(childId);
            check(childCreates == 1 && records.Count == 3 && cache.PendingCount == 0, "retry recovers a saved child without duplicates and drains later edits");
            check(cache.Reminder(remoteChild).Flag("completed") && cache.Reminder(remoteParent).Flag("completed")
                && cache.Reminder(remoteChild).Text("parent_id") == remoteParent, "offline family completion round-trips with its hierarchy intact");
            var moved = await client.Update(remoteChild, new() { ["list_id"] = "List/b", ["parent_id"] = null, ["description"] = "Moved child notes" }, records[remoteChild].Text("recordChangeTag"), default);
            check(moved.Text("parent_id") is null && moved.Text("list_id") == "List/b" && moved.Text("description") == "Moved child notes"
                && records[remoteChild]["fields"]!["ParentReminder"].Text("type") == "REFERENCE", "moving a child clears its native reference and survives a partial reply");
            var revisionBeforeConflict = revision;
            try { await client.Update(remoteChild, new() { ["description"] = "Stale notes" }, "stale", default); check(false, "stale subtask edit rejected"); }
            catch (CoreException error) { check(error.Code == "CONFLICT" && revision == revisionBeforeConflict, "subtask conflicts cannot overwrite newer cloud edits"); }
            cache.Enqueue("cycle-a", "create", J.Node(new { parent_id = "cycle-b" }), null);
            cache.Enqueue("cycle-b", "create", J.Node(new { parent_id = "cycle-a" }), null);
            // Malformed cached creates must remain recoverable without trapping sync in a loop.
            cache.Execute("UPDATE outbox SET payload=json_set(payload,'$.parent_id',reminder_id) WHERE reminder_id LIKE 'cycle-%'");
            await sync.Sync(false, default).WaitAsync(TimeSpan.FromSeconds(5));
            check(events.Contains("sync_warning") && cache.PendingCount == 2, "blocked dependency queues report a warning without dropping work or spinning");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
