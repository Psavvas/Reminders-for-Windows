using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class TagSyncChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(ReminderTags.Parse("#school, aviation  SCHOOL\nCAD-design").SequenceEqual(new[] { "school", "aviation", "CAD-design" }), "tag input accepts commas, spaces and optional hashes, and deduplicates names");
        check(ReminderTags.Parse("").Length == 0, "empty tag input removes every tag");
        foreach (var invalid in new[] { "#", "bad/tag", new string('x', 129) })
        {
            try { ReminderTags.Parse(invalid); check(false, "invalid tags must be rejected"); }
            catch (CoreException error) { check(error.Code == "BAD_REQUEST", "invalid tag has a useful validation error"); }
        }

        var records = new Dictionary<string, JsonObject>(); var writes = 0; var loseReply = false; var ignoreTagWrite = false;
        // Independent, redacted shape observed on Apple-created Hashtag records.
        // Do not construct this fixture with the production tag writer.
        var appleTag = JsonNode.Parse("""
            {"recordName":"Hashtag/apple","recordType":"Hashtag","recordChangeTag":"tag-original","fields":{
                "Name":{"type":"STRING","value":"school"},"Type":{"type":"INT64","value":1},
                "Imported":{"type":"INT64","value":0},"Deleted":{"type":"INT64","value":0},
                "Reminder":{"type":"REFERENCE","value":{"recordName":"Reminder/task","action":"VALIDATE"}},
                "CreationDate":{"type":"TIMESTAMP","value":1728000000000}}}
            """)!.AsObject();
        records["Hashtag/apple"] = appleTag;
        records["Reminder/task"] = new() { ["recordName"] = "Reminder/task", ["recordType"] = "Reminder", ["recordChangeTag"] = "original", ["fields"] = new JsonObject
        {
            ["TitleDocument"] = CloudKit.String(AppleDocument.Encode("A tagged task")), ["List"] = CloudKit.Reference("List/test"),
            ["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "apple" } }), ["Completed"] = CloudKit.Number(0)
        } };
        var secrets = new SecretStore(new MemorySecrets()); secrets.Session("test@example.com", J.Node(new { session_token = "test" }).ToJsonString());
        HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath; var body = J.Parse(await request.Content!.ReadAsStringAsync())!;
            if (path.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
            if (path.EndsWith("/records/lookup")) return Reply(new JsonObject { ["records"] = new JsonArray(body.Array("records").Select(row => records.TryGetValue(row!.Required("recordName"), out var found) ? found.DeepClone() : J.Node(new { recordName = row.Text("recordName"), serverErrorCode = "NOT_FOUND" })).ToArray()) });
            if (path.EndsWith("/changes/zone")) return Reply(J.Node(new { zones = new[] { new { records = records.Values.ToArray(), syncToken = "cursor" } } }));
            if (!path.EndsWith("/records/modify")) throw new InvalidOperationException("Unexpected tag test endpoint");
            check(body.Flag("atomic"), "tag relationships commit atomically");
            var staged = new Dictionary<string, JsonObject>();
            foreach (var operation in body.Array("operations"))
            {
                var supplied = operation!["record"]!; var id = supplied.Required("recordName");
                var create = operation.Text("operationType") == "create";
                check(create ? !records.ContainsKey(id) : supplied.Text("recordChangeTag") == records[id].Text("recordChangeTag"), "tag writes use stable identities and current revisions");
                var target = create ? supplied.DeepClone().AsObject() : records[id].DeepClone().AsObject();
                foreach (var field in supplied["fields"]!.AsObject()) target["fields"]![field.Key] = field.Value?.DeepClone();
                if (id.StartsWith("Hashtag/"))
                {
                    check(target["fields"]!["Name"].Text("type") == "STRING", "Apple hashtag Name is a STRING, not encoded bytes");
                    check(target["fields"]!["Type"].Text("type") == "INT64" && target["fields"]!["Imported"].Text("type") == "INT64", "tags include Apple's Type and Imported metadata");
                    check(CloudKit.Field(target, "Reminder").Text("recordName") is not null, "tag has a valid reminder back-reference");
                }
                if (id.StartsWith("Reminder/")) check(J.Parse(CloudKit.Text(target, "ResolutionTokenMap"))?["map"]?["hashtagIDs"] is null, "tag edits never emit the unsupported hashtagIDs token");
                target["recordChangeTag"] = (writes + 1).ToString(); staged[id] = target;
            }
            writes++;
            foreach (var pair in staged) if (!ignoreTagWrite || !pair.Key.StartsWith("Hashtag/")) records[pair.Key] = pair.Value;
            ignoreTagWrite = false;
            if (loseReply) { loseReply = false; throw new HttpRequestException("Lost reply after atomic tag commit"); }
            return Reply(new JsonObject { ["records"] = new JsonArray(staged.Values.Select(r => r.DeepClone()).ToArray()) });
        }));
        check(await client.Restore(default), "tag sync test account restores");
        JsonObject Desired(params (string Id, string Name)[] values) => new()
        {
            ["tags"] = new JsonArray(values.Select(v => J.Node(new { id = "Hashtag/" + v.Id, name = v.Name })).ToArray()),
            ["previous_ids"] = new JsonArray("Hashtag/apple", "Hashtag/windows")
        };
        var add = Desired(("apple", "school"), ("windows", "aviation"));
        var tagged = await client.EditTags("Reminder/task", add, "original", default);
        check(CloudKit.Int(records["Hashtag/windows"], "Type") == 0 && JsonNode.DeepEquals(records["Hashtag/apple"], appleTag), "Windows creates a normal tag without altering Apple Type=1 tags");
        var due = "2026-11-02T14:00:00Z";
        var updated = await client.Update("Reminder/task", J.Node(new { due_date = due }).AsObject(), tagged.Text("change_tag"), default);
        check(updated.Text("due_date") == TimeUtil.Iso(TimeUtil.Parse(due)) && CloudKit.Field(records["Reminder/task"], "HashtagIDs")!.AsArray().Count == 2, "a due-date edit still works after adding a tag");
        var before = writes;
        await client.EditTags("Reminder/task", add, "original", default);
        check(writes == before, "retrying a successful tag set neither duplicates tags nor conflicts on the old revision");
        var remove = Desired(("apple", "school")); loseReply = true;
        try { await client.EditTags("Reminder/task", remove, updated.Text("change_tag"), default); check(false, "lost tag-removal reply must be retryable"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "lost removal reply is retained for retry"); }
        before = writes; var removed = await client.EditTags("Reminder/task", remove, updated.Text("change_tag"), default);
        check(writes == before && CloudKit.Int(records["Hashtag/windows"], "Deleted") == 1 && removed.Array("related_records").Any(r => r.Text("recordName") == "Hashtag/windows"), "removal retry recovers both the relationship and tombstone");
        var none = await client.EditTags("Reminder/task", Desired(), removed.Text("change_tag"), default);
        check(CloudKit.Field(records["Reminder/task"], "HashtagIDs")!.AsArray().Count == 0 && CloudKit.Int(records["Hashtag/apple"], "Deleted") == 1, "removing the last tag clears the parent and retires the child");
        before = writes; await client.EditTags("Reminder/task", Desired(), removed.Text("change_tag"), default);
        check(writes == before, "removing the last tag is idempotent");

        records["Hashtag/legacy"] = new() { ["recordName"] = "Hashtag/legacy", ["recordType"] = "Hashtag", ["recordChangeTag"] = "legacy", ["fields"] = new JsonObject
        {
            ["Name"] = J.Node(new { type = "ENCRYPTED_BYTES", value = "c3RlbQ==" }), ["Reminder"] = CloudKit.Reference("Reminder/task"),
            ["CreationDate"] = CloudKit.Timestamp(1728000000000), ["Deleted"] = CloudKit.Number(0)
        } };
        records["Reminder/task"]["fields"]!["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "legacy" } });
        records["Reminder/task"]["fields"]!["ResolutionTokenMap"] = CloudKit.ResolutionTokens(["hashtagIDs", "dueDate"]);
        var root = Path.Combine(Path.GetTempPath(), "reminders-tags-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var cache = new Cache(Path.Combine(root, "repair.db")))
            {
                cache.ApplyRecords(new JsonArray(records.Values.Select(r => r.DeepClone()).ToArray()), true);
                var events = new List<string>(); var sync = new SyncEngine(cache, client, new SemaphoreSlim(1), (name, _) => events.Add(name));
                await sync.Sync(false, default).WaitAsync(TimeSpan.FromSeconds(10));
                check(!ReminderTags.IsLegacy(records["Hashtag/legacy"]) && CloudKit.Text(records["Hashtag/legacy"], "Name") == "stem", "sync repairs previously uploaded malformed Windows tags without changing their identities or names");
                check(cache.Pending().Count == 0 && events.Last() == "sync_finished", "repair drains before sync reports completion");
                check(J.Parse(CloudKit.Text(records["Reminder/task"], "ResolutionTokenMap"))?["map"]?["dueDate"] is not null, "repair preserves unrelated resolution tokens");
            }

            // Persist a create, tags, repeat, removal and later date edit while offline.
            // Then restart before uploading, exercising aliases and queue rebasing.
            var offline = Path.Combine(root, "offline"); string localId;
            using (var seed = new Cache(Path.Combine(offline, "cache.db"))) seed.SetMeta("signed_out", "1");
            await using (var service = new ReminderService(offline, new MemorySecrets()))
            {
                async Task<JsonNode> Call(string method, object args) => JsonNode.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(args))).GetRawText())!;
                var created = await Call("create_reminder", new { list_id = "List/test", title = "Offline tagged reminder", due_date = due, recurrence = new { frequency = 2, interval = 1 }, tags = new[] { "#school", "aviation" } });
                localId = created.Required("id");
                check(created.Array("tags").Count == 2, "new reminders accept tags immediately while offline");
                await Call("update_reminder", new { id = localId, tags = new[] { "aviation" }, due_date = "2026-11-09T14:00:00Z" });
                var edited = await Call("reminder", new { id = localId });
                check(edited.Array("tags").Count == 1 && edited.Array("tags")[0]!.ToString() == "aviation", "removing a tag is staged with the rest of the form");
                await Call("create_tag", new { id = localId, name = "#CAD" });
                check((await Call("reminder", new { id = localId })).Array("tags").Count == 2, "legacy create_tag callers use the corrected editor path");
            }
            using (var cache = new Cache(Path.Combine(offline, "cache.db")))
            {
                var events = new List<string>(); var sync = new SyncEngine(cache, client, new SemaphoreSlim(1), (name, _) => events.Add(name));
                await sync.Sync(true, default).WaitAsync(TimeSpan.FromSeconds(10));
                var id = cache.ResolveId(localId); var saved = cache.Reminder(id)!;
                check(cache.Pending().Count == 0 && saved.Array("tags").Select(v => v!.ToString()).ToHashSet().SetEquals(new[] { "CAD", "aviation" }), "offline tag additions and removals survive restart and sync");
                check(saved.Text("due_date") == TimeUtil.Iso(TimeUtil.Parse("2026-11-09T14:00:00Z")) && saved["recurrence"].Number("frequency") == 2, "tag operations preserve later due dates and the original repeat");

                var currentTags = cache.Query("SELECT id,name FROM tags WHERE reminder_id=@p0", id);
                cache.ReplaceTags(id, new()); cache.Execute("UPDATE reminders SET dirty=1 WHERE id=@p0", id);
                cache.Enqueue(id, "tags", new JsonObject { ["tags"] = new JsonArray(), ["previous_ids"] = new JsonArray(currentTags.Select(t => JsonValue.Create(t.Text("id"))).ToArray()) }, saved.Text("change_tag"));
                cache.ApplyRecords(new JsonArray(records.Values.Select(r => r.DeepClone()).ToArray()), true);
                check(cache.Reminder(id)!.Array("tags").Count == 0, "a download cannot resurrect tags removed locally while their upload is pending");
                ignoreTagWrite = true; events.Clear();
                await sync.Sync(false, default).WaitAsync(TimeSpan.FromSeconds(10));
                check(cache.Pending().Count == 1 && events.Last() == "sync_warning" && !events.Contains("sync_finished"), "HTTP success without persisted tag changes stays queued and never reports successful sync");
            }

            // A real parent revision conflict must preserve the local intent and
            // show the cloud tags until the user explicitly chooses this PC.
            using (var cache = new Cache(Path.Combine(root, "conflict", "cache.db")))
            {
                cache.SetMeta("signed_out", "1"); cache.ApplyRecords(new JsonArray(records.Values.Select(r => r.DeepClone()).ToArray()), true);
                cache.ReplaceTags("Reminder/task", new JsonArray(J.Node(new { id = "Hashtag/local", name = "local" })));
                cache.Execute("UPDATE reminders SET dirty=1 WHERE id=@p0", "Reminder/task");
                cache.Enqueue("Reminder/task", "tags", Desired(("local", "local")), "stale");
                var sync = new SyncEngine(cache, client, new SemaphoreSlim(1), (_, _) => { }); await sync.Sync(false, default);
                check(cache.Conflicts.Count == 1 && cache.Conflicts[0]!["local"].Flag("tags_edit"), "tag conflicts preserve explicit local tag-edit intent");
                check(cache.Reminder("Reminder/task")!.Array("tags").All(v => v!.ToString() != "local"), "conflict recovery restores cloud tags instead of leaving an unsynced local tag displayed");
            }
            await using (var service = new ReminderService(Path.Combine(root, "conflict"), new MemorySecrets()))
            {
                var conflicts = JsonNode.Parse((await service.CallAsync("conflicts", JsonSerializer.SerializeToElement(new { }))).GetRawText())!;
                await service.CallAsync("resolve_conflict", JsonSerializer.SerializeToElement(new { id = conflicts[0].Number("id"), keep = "local" }));
            }
            using (var cache = new Cache(Path.Combine(root, "conflict", "cache.db")))
                check(cache.Pending().Any(p => p.Text("op") == "tags"), "Keep this PC requeues the intended tags");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
