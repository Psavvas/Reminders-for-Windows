using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class RecurrenceEditingChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var records = new Dictionary<string, JsonObject>(); var writes = 0; var loseReply = false; var rejectRule = false;
        var due = TimeUtil.Iso(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()));
        records["Reminder/task"] = new JsonObject { ["recordName"] = "Reminder/task", ["recordType"] = "Reminder", ["recordChangeTag"] = "original", ["fields"] = new JsonObject
        {
            ["TitleDocument"] = CloudKit.String(AppleDocument.Encode("Keep this task")), ["List"] = CloudKit.Reference("List/test"),
            ["DueDate"] = CloudKit.Timestamp(TimeUtil.ToFloating(TimeUtil.Parse(due), "UTC")), ["TimeZone"] = CloudKit.String("UTC"),
            ["Completed"] = CloudKit.Number(0), ["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "tag" } })
        } };
        var secrets = new SecretStore(new MemorySecrets()); secrets.Session("test@example.com", J.Node(new { session_token = "test" }).ToJsonString());
        HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath; var body = J.Parse(await request.Content!.ReadAsStringAsync())!;
            if (path.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
            if (path.EndsWith("/records/lookup")) return Reply(new JsonObject { ["records"] = new JsonArray(body.Array("records").Select(row => records.TryGetValue(row!.Required("recordName"), out var found) ? found.DeepClone() : J.Node(new { recordName = row.Text("recordName"), serverErrorCode = "NOT_FOUND" })).ToArray()) });
            if (path.EndsWith("/changes/zone")) return Reply(J.Node(new { zones = new[] { new { records = records.Values.ToArray(), syncToken = "cursor" } } }));
            if (!path.EndsWith("/records/modify")) throw new InvalidOperationException("Unexpected request");
            var operations = body.Array("operations"); check(body.Flag("atomic"), "repeat edits commit parent and rule atomically");
            if (rejectRule)
            {
                rejectRule = false;
                return Reply(J.Node(new { records = new[] { new { recordName = operations[1]!["record"].Text("recordName"), recordType = "RecurrenceRule", serverErrorCode = "CONFLICT" } } }));
            }
            foreach (var operation in operations)
            {
                var supplied = operation!["record"]!; var id = supplied.Required("recordName");
                if (operation.Text("operationType") == "create") check(!records.ContainsKey(id), "a retried repeat edit never duplicates its rule");
                else check(supplied.Text("recordChangeTag") == records[id].Text("recordChangeTag"), "repeat edits use the latest parent and rule revisions");
            }
            writes++; var results = new JsonArray();
            foreach (var operation in operations)
            {
                var supplied = operation!["record"]!; var id = supplied.Required("recordName");
                if (operation.Text("operationType") == "create") records[id] = supplied.DeepClone().AsObject();
                else foreach (var field in supplied["fields"]!.AsObject()) records[id]["fields"]![field.Key] = field.Value?.DeepClone();
                records[id]["recordChangeTag"] = writes.ToString();
                results.Add(new JsonObject { ["recordName"] = id, ["recordChangeTag"] = writes.ToString(), ["fields"] = supplied["fields"]!.DeepClone() });
            }
            if (loseReply) { loseReply = false; throw new HttpRequestException("Lost reply after atomic commit"); }
            return Reply(new JsonObject { ["records"] = results });
        }));
        check(await client.Restore(default), "repeat editing account restores");
        JsonObject Edit(int? frequency, int interval = 1) => new() { ["recurrence_id"] = Guid.NewGuid().ToString().ToUpperInvariant(), ["recurrence"] = frequency is null ? null : J.Node(new { frequency, interval }) };
        var add = Edit(2, 2);
        var added = await client.Update("Reminder/task", add, "original", default);
        var ruleId = "RecurrenceRule/" + add.Required("recurrence_id");
        check(CloudKit.Int(records[ruleId], "Frequency") == 1 && CloudKit.Int(records[ruleId], "Interval") == 2, "add a two-week repeat to an existing non-repeating reminder using Apple's weekly value");
        check(added.Text("title") == "Keep this task" && added.Text("due_date") == due && CloudKit.Field(records["Reminder/task"], "HashtagIDs")!.AsArray().Count == 1, "adding a repeat preserves the task, due date and tags");
        check(added.Array("related_records").Count == 1, "changed rules are available to the cache before the next download");
        var beforeRetry = writes; await client.Update("Reminder/task", add, "original", default);
        check(writes == beforeRetry, "replaying an added schedule recovers before checking the old parent revision");

        records[ruleId]["fields"]!["OccurrenceCount"] = CloudKit.Number(8);
        records[ruleId]["fields"]!["EndDate"] = CloudKit.Timestamp(TimeUtil.ToFloating(DateTimeOffset.UtcNow.AddYears(2), "UTC"));
        var change = Edit(3, 3); change["title"] = "Changed together";
        var changed = await client.Update("Reminder/task", change, added.Text("change_tag"), default);
        check(CloudKit.Int(records[ruleId], "Frequency") == 2 && CloudKit.Int(records[ruleId], "Interval") == 3, "change an existing repeat to every three months");
        check(records.Keys.Count(id => id.StartsWith("RecurrenceRule/")) == 1 && CloudKit.Int(records[ruleId], "OccurrenceCount") == 8 && CloudKit.Field(records[ruleId], "EndDate") is not null, "editing reuses the existing rule and preserves its end conditions");
        check(changed.Text("title") == "Changed together", "task fields save in the same atomic edit as the repeat");
        var weekly = Edit(2); loseReply = true;
        try { await client.Update("Reminder/task", weekly, changed.Text("change_tag"), default); check(false, "lost schedule-change reply must be retryable"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "ambiguous schedule change retains the queued edit"); }
        beforeRetry = writes; var recovered = await client.Update("Reminder/task", weekly, changed.Text("change_tag"), default);
        check(writes == beforeRetry && CloudKit.Int(recovered.Array("related_records")[0]!, "Frequency") == 1, "lost-reply recovery does not repeat a schedule change");
        var remove = Edit(null); remove["due_date"] = null; loseReply = true;
        try { await client.Update("Reminder/task", remove, recovered.Text("change_tag"), default); check(false, "lost remove-repeat reply must be retryable"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "ambiguous removal retains the queued edit"); }
        beforeRetry = writes; var removed = await client.Update("Reminder/task", remove, recovered.Text("change_tag"), default);
        check(writes == beforeRetry && removed.Array("recurrence_ids").Count == 0 && removed["due_date"] is null, "Never and clearing the due date recover without a new repeat or duplicate write");
        check(CloudKit.Int(records[ruleId], "Deleted") == 1 && records["Reminder/task"].Text("recordName") == "Reminder/task", "Never retires the linked rule without deleting the reminder");
        var again = Edit(4); again["due_date"] = due;
        var renewed = await client.Update("Reminder/task", again, removed.Text("change_tag"), default);
        var newRuleId = "RecurrenceRule/" + again.Required("recurrence_id");
        check(newRuleId != ruleId && CloudKit.Int(records[newRuleId], "Frequency") == 3, "add a yearly repeat again after removing the old schedule");
        beforeRetry = writes;
        try { await client.Update("Reminder/task", Edit(1), "stale-parent", default); check(false, "stale parent must conflict"); }
        catch (CoreException error) { check(error.Code == "CONFLICT" && writes == beforeRetry, "a stale parent revision cannot overwrite a newer repeat"); }
        rejectRule = true;
        try { await client.Update("Reminder/task", Edit(1), renewed.Text("change_tag"), default); check(false, "linked rule revision conflict must fail atomically"); }
        catch (CoreException error) { check(error.Code == "CONFLICT" && J.Parse(error.Detail).Text("id") == "Reminder/task", "rule conflicts report the parent task to conflict recovery"); }
        check(CloudKit.Int(records[newRuleId], "Frequency") == 3, "a rejected batch leaves the previous schedule intact");
        records[newRuleId]["fields"]!["DaysOfTheWeek"] = CloudKit.String("custom");
        beforeRetry = writes;
        try { await client.Update("Reminder/task", Edit(1), renewed.Text("change_tag"), default); check(false, "custom rules must not be overwritten"); }
        catch (CoreException error) { check(error.Code == "BAD_REQUEST" && writes == beforeRetry, "custom rules are protected before any write"); }

        var root = Path.Combine(Path.GetTempPath(), "reminders-edit-repeat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var offlineRoot = Path.Combine(root, "offline");
            using (var offlineCache = new Cache(Path.Combine(offlineRoot, "cache.db"))) offlineCache.SetMeta("signed_out", "1");
            string offlineId;
            await using (var offline = new ReminderService(offlineRoot, new MemorySecrets()))
            {
                async Task<JsonNode> Call(string method, object parameters) => J.Parse((await offline.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
                offlineId = (await Call("create_reminder", new { list_id = "List/test", title = "Offline edits", due_date = due })).Required("id");
                await Call("update_reminder", new { id = offlineId, recurrence = new { frequency = 2, interval = 2 } });
                await Call("update_reminder", new { id = offlineId, recurrence = new { frequency = 3, interval = 3 } });
                await Call("update_reminder", new { id = offlineId, recurrence = (object?)null, due_date = (string?)null });
                check((await Call("reminder", new { id = offlineId }))["recurrence"] is null, "offline Never immediately clears the visible schedule");
            }
            using (var offlineCache = new Cache(Path.Combine(offlineRoot, "cache.db")))
            {
                var pending = offlineCache.Pending();
                check(pending.Count == 4 && pending.Skip(1).All(row => Guid.TryParse(J.Parse(row.Text("payload")).Text("recurrence_id"), out _)), "each queued schedule edit retains a stable retry identity across restart");
                var sync = new SyncEngine(offlineCache, client, new SemaphoreSlim(1), (_, _) => { });
                var outcome = await sync.Sync(true, default);
                check(outcome["push"].Number("pushed") == 4 && offlineCache.Pending().Count == 0, "queued create and add/change/remove repeat edits upload in order after restart");
                var remoteId = offlineCache.ResolveId(offlineId);
                check(offlineCache.Reminder(remoteId).Property("recurrence") is null && offlineCache.Reminder(remoteId).Array("recurrence_ids").Count == 0, "acknowledgements and downloads do not resurrect a removed repeat");
                var local = offlineCache.Reminder(remoteId)!;
                local["recurrence"] = J.Node(new { frequency = 2, interval = 3 }); local["recurrence_edit"] = true; local["due_date"] = due;
                offlineCache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES(@p0,@p1,'{}','now')", remoteId, local.ToJsonString());
                local["id"] = "Reminder/custom"; local["due_date"] = due;
                local["recurrence"] = J.Node(new { supported = false }); local["recurrence_edit"] = false;
                offlineCache.Upsert(local);
                offlineCache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES(@p0,@p1,'{}','now')", "Reminder/custom", local.ToJsonString());
            }
            await using (var recovery = new ReminderService(offlineRoot, new MemorySecrets()))
            {
                async Task<JsonNode> Call(string method, object parameters) => J.Parse((await recovery.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
                var conflicts = (await Call("conflicts", new { })).AsArray();
                // Keep-local for an actual schedule conflict carries its intent.
                // An unrelated title conflict must leave a custom rule alone.
                var repeatConflict = conflicts.First(c => c.Text("reminder_id") != "Reminder/custom")!;
                await Call("resolve_conflict", new { id = repeatConflict.Number("id"), keep = "local" });
                var customConflict = conflicts.First(c => c.Text("reminder_id") == "Reminder/custom")!;
                await Call("resolve_conflict", new { id = customConflict.Number("id"), keep = "local" });
            }
            using (var resolved = new Cache(Path.Combine(offlineRoot, "cache.db")))
            {
                var pending = resolved.Pending();
                check(J.Parse(pending[0].Text("payload")).Property("recurrence").Number("interval") == 3, "keep-local conflict recovery preserves an explicit recurrence edit");
                check(J.Parse(pending[1].Text("payload"))?.AsObject().ContainsKey("recurrence") == false, "resolving an unrelated edit does not replace a custom repeat");
            }
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "demo-account.md"))) directory = directory.Parent;
            var password = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "demo-account.md")), @"- Password: `([^`]+)`").Groups[1].Value;
            string localId;
            await using (var demo = ReminderService.OpenDemo(root, password))
            {
                async Task<JsonNode> Call(string method, object parameters) => J.Parse((await demo.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
                var lists = await Call("lists", new { });
                localId = (await Call("create_reminder", new { list_id = lists[0].Text("id"), title = "Edit my repeat", due_date = due })).Required("id");
                var localAdded = await Call("update_reminder", new { id = localId, recurrence = new { frequency = 2, interval = 2 } });
                check(localAdded["recurrence"].Number("frequency") == 2, "local task can start repeating after creation");
                var localChanged = await Call("update_reminder", new { id = localId, recurrence = new { frequency = 3, interval = 3 } });
                check(localChanged["recurrence"].Number("interval") == 3, "local task repeat interval can be changed");
                try { await Call("update_reminder", new { id = localId, recurrence = new { frequency = 2, interval = 2.5 } }); check(false, "fractional repeat interval must fail"); }
                catch (CoreException error) { check(error.Code == "BAD_REQUEST", "fractional intervals cannot silently become every one week"); }
                try { await Call("update_reminder", new { id = localId, recurrence = new { frequency = 2 }, completed = true }); check(false, "combined repeat edit and completion must fail"); }
                catch (CoreException error) { check(error.Code == "BAD_REQUEST", "repeat changes are saved before completing the task"); }
                try { await Call("update_reminder", new { id = localId, due_date = (string?)null }); check(false, "repeat needs a due date"); }
                catch (CoreException error) { check(error.Code == "BAD_REQUEST", "cannot clear the date while leaving a repeat active"); }
                var localRemoved = await Call("update_reminder", new { id = localId, recurrence = (object?)null, due_date = (string?)null });
                check(localRemoved["recurrence"] is null && localRemoved["due_date"] is null && localRemoved.Array("recurrence_ids").Count == 0, "Never clears the local repeat and permits removing its due date");
                try { await Call("update_reminder", new { id = localId, recurrence = new { frequency = 2 } }); check(false, "adding a repeat needs a due date"); }
                catch (CoreException error) { check(error.Code == "BAD_REQUEST", "validation rejects a new repeat without a due date"); }
                await Call("update_reminder", new { id = localId, due_date = due, recurrence = new { frequency = 2 } });
                var completion = await Call("update_reminder", new { id = localId, completed = true });
                check(!completion.Flag("completed") && TimeUtil.Parse(completion.Required("due_date")) > TimeUtil.Parse(due), "completion uses the edited local weekly schedule");
            }
            await using (var reopened = ReminderService.OpenDemo(root, password))
            {
                var saved = J.Parse((await reopened.CallAsync("reminder", JsonSerializer.SerializeToElement(new { id = localId }))).GetRawText());
                check(saved.Property("recurrence").Number("frequency") == 2, "edited repeat survives restart");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
