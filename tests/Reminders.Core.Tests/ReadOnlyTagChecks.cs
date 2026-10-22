using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class ReadOnlyTagChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        JsonObject Reminder(string id) => new() { ["recordName"] = id, ["recordType"] = "Reminder", ["recordChangeTag"] = "old", ["fields"] = new JsonObject
        {
            ["TitleDocument"] = CloudKit.String(AppleDocument.Encode("Existing task")), ["List"] = CloudKit.Reference("List/test"),
            ["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "apple" } }), ["Completed"] = CloudKit.Number(0)
        } };
        var records = new Dictionary<string, JsonObject>
        {
            ["Reminder/task"] = Reminder("Reminder/task"), ["Reminder/legacy"] = Reminder("Reminder/legacy"),
            ["Hashtag/apple"] = new() { ["recordName"] = "Hashtag/apple", ["recordType"] = "Hashtag", ["recordChangeTag"] = "apple", ["fields"] = new JsonObject
            {
                ["Name"] = CloudKit.String("school"), ["Type"] = CloudKit.Number(0), ["Imported"] = CloudKit.Number(0),
                ["Reminder"] = CloudKit.Reference("Reminder/task"), ["Deleted"] = CloudKit.Number(0)
            } },
            ["Hashtag/legacy"] = new() { ["recordName"] = "Hashtag/legacy", ["recordType"] = "Hashtag", ["recordChangeTag"] = "legacy", ["fields"] = new JsonObject
            {
                ["Name"] = J.Node(new { type = "ENCRYPTED_BYTES", value = "c3RlbQ==" }), ["Reminder"] = CloudKit.Reference("Reminder/legacy"),
                ["CreationDate"] = CloudKit.Timestamp(1728000000000), ["Deleted"] = CloudKit.Number(0)
            } }
        };
        records["Reminder/legacy"]["fields"]!["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "legacy" } });
        var originalAppleTag = records["Hashtag/apple"].DeepClone(); var originalLegacyTag = records["Hashtag/legacy"].DeepClone();
        var root = Path.Combine(Path.GetTempPath(), "reminders-readonly-tags-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "cache.db");
            using (var cache = new Cache(path))
            {
                cache.SetMeta("signed_out", "1");
                cache.ApplyRecords(new JsonArray(records.Values.Select(r => r.DeepClone()).ToArray()), true);
                cache.ReplaceTags("Reminder/task", new JsonArray(J.Node(new { id = "Hashtag/unsent", name = "local-only" })));
                cache.Edit("Reminder/task", new() { ["due_date"] = "2026-11-02T14:00:00Z" });
                cache.Enqueue("Reminder/task", "tag", J.Node(new { name = "local-only", tag_id = "unsent" }), "old");
                cache.Enqueue("Reminder/task", "update", J.Node(new { due_date = "2026-11-02T14:00:00Z" }), "old");
                cache.Enqueue("Reminder/task", "tags", J.Node(new { tags = Array.Empty<string>() }), "old");
                cache.Enqueue("Reminder/legacy", "repair_tags", J.Node(new { repair_only = true }), "old");
                cache.Execute("UPDATE reminders SET dirty=1 WHERE id=@p0", "Reminder/legacy");
                cache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES(@p0,@p1,@p2,@p3)",
                    "Reminder/legacy", J.Node(new { tags_edit = true, tags = new[] { "unsent-conflict" } }).ToJsonString(), "{}", TimeUtil.Now);
                cache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES(@p0,@p1,@p2,@p3)",
                    "Reminder/task", J.Node(new { title = "Unrelated conflict" }).ToJsonString(), "{}", TimeUtil.Now);
            }
            using (var cache = new Cache(path))
            {
                check(cache.Pending().Count == 1 && cache.Pending()[0].Text("op") == "update", "upgrade retires old tag additions, replacements and repairs while preserving the later task edit");
                check(J.Parse(cache.Meta("disabled_tag_writes"))!.AsArray().Count == 4, "retired tag requests and tag-only conflicts are retained locally");
                check(cache.Reminder("Reminder/task")!.Array("tags")[0]!.ToString() == "school", "upgrade restores cloud tags instead of displaying a speculative local tag");
                check(cache.Reminder("Reminder/task")!.Number("dirty") == 1 && cache.Reminder("Reminder/legacy")!.Number("dirty") == 0, "only reminders with ordinary pending uploads remain dirty");
                check(cache.Conflicts.Count == 1 && cache.Conflicts[0]!.Text("reminder_id") == "Reminder/task", "retiring tag conflicts preserves unrelated task conflicts");
            }
            await using (var service = new ReminderService(root, new MemorySecrets()))
            {
                async Task<JsonNode> Call(string method, object args) => JsonNode.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(args))).GetRawText())!;
                var rowsBefore = (await Call("reminders", new { include_completed = true })).AsArray().Count;
                foreach (var (method, args) in new (string, object)[]
                {
                    ("create_tag", new { id = "Reminder/task", name = "aviation" }),
                    ("update_reminder", new { id = "Reminder/task", tags = Array.Empty<string>(), due_date = "2026-12-01T09:00:00Z" }),
                    ("create_reminder", new { list_id = "List/test", title = "Must not create", tags = new[] { "aviation" } })
                })
                {
                    try { await Call(method, args); check(false, "tag-writing API must be unavailable"); }
                    catch (CoreException error) { check(error.Code == "UNSUPPORTED" && error.Message.Contains("Apple Reminders"), "tag-writing API explains that tags must be managed in Apple Reminders"); }
                }
                check((await Call("reminders", new { include_completed = true })).AsArray().Count == rowsBefore, "rejecting tags at creation leaves no partial task");
                check((await Call("reminder", new { id = "Reminder/task" })).Text("due_date") == "2026-11-02T14:00:00Z", "rejecting a combined tag edit leaves other fields unchanged");
                check((await Call("tags", new { })).AsArray().Any(t => t.Text("name") == "school"), "existing cloud tags remain available in navigation");
                check((await Call("reminders", new { tag = "school" })).AsArray().Count == 1, "existing tags still filter reminders");
                await Call("create_reminder", new { list_id = "List/test", title = "Ordinary new task" });
            }
            var writes = 0;
            var secrets = new SecretStore(new MemorySecrets()); secrets.Session("test@example.com", J.Node(new { session_token = "test" }).ToJsonString());
            HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
            using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
            {
                var url = request.RequestUri!.AbsolutePath; var body = J.Parse(await request.Content!.ReadAsStringAsync())!;
                if (url.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
                if (url.EndsWith("/records/lookup")) return Reply(new JsonObject { ["records"] = new JsonArray(body.Array("records").Select(row => records.TryGetValue(row!.Required("recordName"), out var found) ? found.DeepClone() : J.Node(new { recordName = row.Text("recordName"), serverErrorCode = "NOT_FOUND" })).ToArray()) });
                if (url.EndsWith("/changes/zone")) return Reply(J.Node(new { zones = new[] { new { records = records.Values.ToArray(), syncToken = "cursor" } } }));
                if (!url.EndsWith("/records/modify")) throw new InvalidOperationException("Unexpected request");
                writes++; var result = new JsonArray();
                foreach (var operation in body.Array("operations"))
                {
                    var supplied = operation!["record"]!; var id = supplied.Required("recordName");
                    check(!id.StartsWith("Hashtag/"), "sync never creates, updates, removes or repairs a tag record");
                    if (operation.Text("operationType") == "create") records[id] = supplied.DeepClone().AsObject();
                    else
                    {
                        check(supplied.Text("recordChangeTag") == records[id].Text("recordChangeTag"), "ordinary edits retain their cloud revision checks");
                        foreach (var field in supplied["fields"]!.AsObject()) records[id]["fields"]![field.Key] = field.Value?.DeepClone();
                    }
                    records[id]["recordChangeTag"] = writes.ToString(); result.Add(records[id].DeepClone());
                }
                return Reply(new JsonObject { ["records"] = result });
            }));
            check(await client.Restore(default), "read-only-tag test account restores");
            using (var cache = new Cache(path))
            {
                check(J.Parse(cache.Meta("disabled_tag_writes"))!.AsArray().Count == 4, "reopening does not duplicate the archived requests");
                var sync = new SyncEngine(cache, client, new SemaphoreSlim(1), (_, _) => { });
                await sync.Sync(true, default).WaitAsync(TimeSpan.FromSeconds(10));
                check(cache.Pending().Count == 0 && writes == 2, "old tag uploads do not block ordinary task creation or later due-date edits");
                check(cache.Reminder("Reminder/task")!.Text("due_date") == TimeUtil.Iso(TimeUtil.Parse("2026-11-02T14:00:00Z")), "ordinary due-date edits reach the server and survive a download");
                check(JsonNode.DeepEquals(records["Hashtag/apple"], originalAppleTag) && JsonNode.DeepEquals(records["Hashtag/legacy"], originalLegacyTag), "sync preserves both native tags and old malformed tags without automatic repair");
                check(CloudKit.Field(records["Reminder/task"], "HashtagIDs")!.AsArray()[0]!.ToString() == "apple", "ordinary edits preserve existing tag links");
                records["Hashtag/apple"]["fields"]!["Name"] = CloudKit.String("engineering");
                await sync.Sync(false, default).WaitAsync(TimeSpan.FromSeconds(10));
                check(writes == 2 && cache.Reminder("Reminder/task")!.Array("tags")[0]!.ToString() == "engineering", "tag changes made in Apple Reminders still download without uploads");

                // Retire old requests even if encountered after this cache opened.
                cache.Enqueue("Reminder/task", "tag", J.Node(new { name = "obsolete", tag_id = "obsolete" }), cache.Reminder("Reminder/task").Text("change_tag"));
                cache.Edit("Reminder/task", new() { ["flagged"] = true });
                cache.Enqueue("Reminder/task", "update", J.Node(new { flagged = true }), cache.Reminder("Reminder/task").Text("change_tag"));
                await sync.Sync(false, default).WaitAsync(TimeSpan.FromSeconds(10));
                check(cache.Pending().Count == 0 && CloudKit.Int(records["Reminder/task"], "Flagged") == 1, "sync retires a late old tag request and continues the following ordinary edit");
                check(J.Parse(cache.Meta("disabled_tag_writes"))!.AsArray().Count == 5, "late tag requests are archived instead of silently discarded");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
