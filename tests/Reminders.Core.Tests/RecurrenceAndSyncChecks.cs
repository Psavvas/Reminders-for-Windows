using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reminders.Core;

internal static class RecurrenceAndSyncChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        JsonObject Rule(int frequency, int interval = 1) => new() { ["fields"] = Recurrence.Fields("Reminder/test", J.Node(new { frequency, interval })) };
        check(Recurrence.Next(Rule(1), "2026-03-07T14:00:00Z", "America/New_York", TimeUtil.Parse("2026-03-07T15:00:00Z")) == TimeUtil.Iso(TimeUtil.Parse("2026-03-08T13:00:00Z")), "daily repeats retain 9 AM across spring DST");
        check(Recurrence.Next(Rule(2), "2026-10-31T13:00:00Z", "America/New_York", TimeUtil.Parse("2026-10-31T14:00:00Z")) == TimeUtil.Iso(TimeUtil.Parse("2026-11-07T14:00:00Z")), "weekly repeats retain wall time across fall DST");
        check(Recurrence.Next(Rule(3), "2026-01-31T09:00:00Z", "UTC", TimeUtil.Parse("2026-02-01T00:00:00Z")) == TimeUtil.Iso(TimeUtil.Parse("2026-03-31T09:00:00Z")), "monthly repeats skip missing days without drifting");
        check(Recurrence.Next(Rule(4), "2024-02-29T09:00:00Z", "UTC", TimeUtil.Parse("2024-03-01T00:00:00Z")) == TimeUtil.Iso(TimeUtil.Parse("2028-02-29T09:00:00Z")), "yearly repeats preserve leap-day anchor");
        var counted = Rule(1); counted["fields"]!["OccurrenceCount"] = CloudKit.Number(2);
        check(Recurrence.Next(counted, "2026-01-01T09:00:00Z", "UTC", TimeUtil.Parse("2026-01-03T00:00:00Z")) is null, "exhausted repeat count ends the sequence");
        var ended = Rule(1); ended["fields"]!["EndDate"] = CloudKit.Timestamp(TimeUtil.ToFloating(TimeUtil.Parse("2026-01-01T23:00:00Z"), "UTC"));
        check(Recurrence.Next(ended, "2026-01-01T09:00:00Z", "UTC", TimeUtil.Parse("2026-01-01T10:00:00Z")) is null, "repeat end date is honored");
        var custom = Rule(2); custom["fields"]!["DaysOfTheWeek"] = CloudKit.String("MO,WE");
        try { Recurrence.Next(custom, "2026-01-01T09:00:00Z", "UTC", DateTimeOffset.UtcNow); check(false, "custom recurrence must not be silently replaced"); }
        catch (CoreException e) { check(e.Code == "BAD_REQUEST", "custom recurrence refuses destructive completion"); }

        var records = new Dictionary<string, JsonObject>(); var writes = 0; var loseReply = false; var linkedWrites = 0;
        var vault = new MemorySecrets(); var secrets = new SecretStore(vault);
        secrets.Session("test@example.com", J.Node(new { session_token = "mock-token" }).ToJsonString());
        using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
        {
            var body = request.Content is null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync())!;
            JsonNode response;
            if (request.RequestUri!.AbsolutePath.EndsWith("/accountLogin"))
                response = J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2, appleId = "test@example.com" }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } });
            else if (request.RequestUri.AbsolutePath.EndsWith("records/lookup"))
                response = new JsonObject { ["records"] = new JsonArray(body.Array("records").OfType<JsonObject>().Select(r => records.TryGetValue(r.Required("recordName"), out var found) ? found.DeepClone() : J.Node(new { recordName = r.Text("recordName"), serverErrorCode = "UNKNOWN_ITEM" })).ToArray()) };
            else if (request.RequestUri.AbsolutePath.EndsWith("records/modify"))
            {
                writes++;
                var operations = body.Array("operations");
                if (operations.Count > 1) { check(body.Flag("atomic"), "parent and linked records commit atomically"); linkedWrites++; }
                var result = new JsonArray();
                foreach (var operation in operations)
                {
                    var supplied = operation!["record"]!.AsObject(); var id = supplied.Required("recordName");
                    if (operation.Text("operationType") == "create")
                    {
                        check(!records.ContainsKey(id), "retries never create duplicate records");
                        records[id] = (JsonObject)supplied.DeepClone();
                    }
                    else
                    {
                        check(supplied.Text("recordChangeTag") == records[id].Text("recordChangeTag"), "linked update uses current revision");
                        foreach (var pair in supplied["fields"]!.AsObject()) records[id]["fields"]![pair.Key] = pair.Value?.DeepClone();
                    }
                    records[id]["recordChangeTag"] = writes.ToString();
                    result.Add(new JsonObject { ["recordName"] = id, ["recordChangeTag"] = writes.ToString(), ["fields"] = supplied["fields"]!.DeepClone() });
                }
                if (loseReply) { loseReply = false; throw new HttpRequestException("Simulated lost reply after commit"); }
                response = new JsonObject { ["records"] = result };
            }
            else throw new Exception("Unexpected endpoint");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }));
        check(await client.Restore(default), "mock account restores before relation writes");
        var payload = J.Node(new { cloud_id = "Reminder/recurring", recurrence_id = "rule", list_id = "List/test", title = "Preflight", description = "Checklist", due_date = TimeUtil.Iso(DateTimeOffset.UtcNow.AddDays(-1)), recurrence = new { frequency = 1, interval = 1 } }).AsObject();
        var created = await client.Create(payload, default);
        check(created.Text("title") == "Preflight" && created.Array("recurrence_ids").Count == 1, "recurring creation hydrates parent and repeat IDs");
        check(records["RecurrenceRule/rule"]["parent"].Text("recordName") == "Reminder/recurring", "repeat record has CloudKit parent");
        check(CloudKit.Int(records["RecurrenceRule/rule"], "Frequency") == 1, "repeat frequency uses Apple wire enum");
        var beforeRetry = writes; await client.Create(payload, default); check(writes == beforeRetry, "create retry recovers existing stable identity");
        // An iPhone-created tag must survive adding a new Windows tag.
        records["Reminder/recurring"]["fields"]!["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "existing" } });
        var tagged = await client.AddTag("Reminder/recurring", J.Node(new { name = "aviation", tag_id = "new-tag" }).AsObject(), created.Text("change_tag"), default);
        check(CloudKit.Field(records["Reminder/recurring"], "HashtagIDs")!.AsArray().Count == 2, "adding tags preserves existing tag IDs");
        check(CloudKit.Text(records["Hashtag/new-tag"], "Name") == "aviation", "tag name uses UTF-8 encrypted-bytes wrapper");
        check(records["Hashtag/new-tag"]["parent"].Text("recordName") == "Reminder/recurring", "tag has parent and reminder back-reference");
        var completion = J.Node(new { completed = true, completion_id = "Reminder/history" }).AsObject();
        var previousDue = CloudKit.Field(records["Reminder/recurring"], "DueDate")!.GetValue<long>();
        records["Reminder/recurring"]["fields"]!["StartDate"] = CloudKit.Timestamp(previousDue - 3600000);
        var active = await client.Update("Reminder/recurring", completion, tagged.Text("change_tag"), default);
        check(!active.Flag("completed") && TimeUtil.Parse(active.Required("due_date")) > DateTimeOffset.UtcNow, "completion advances active repeat to a future date");
        check(CloudKit.Field(records["Reminder/recurring"], "DueDate")!.GetValue<long>() - CloudKit.Field(records["Reminder/recurring"], "StartDate")!.GetValue<long>() == 3600000, "completion advances the start date with the same wall-clock offset");
        check(active.Array("recurrence_ids").Count == 1 && CloudKit.Field(records["Reminder/recurring"], "HashtagIDs")!.AsArray().Count == 2, "completion retains repeat and tag associations");
        check(active["completed_occurrence"].Flag("completed") && CloudKit.Field(records["Reminder/history"], "RecurrenceRuleIDs")!.AsArray().Count == 0, "completed history cannot spawn another repeat");
        beforeRetry = writes; await client.Update("Reminder/recurring", completion, tagged.Text("change_tag"), default); check(writes == beforeRetry, "completion retry does not advance schedule twice");
        var second = J.Node(new { completed = true, completion_id = "Reminder/history-2" }).AsObject();
        loseReply = true;
        try { await client.Update("Reminder/recurring", second, active.Text("change_tag"), default); check(false, "lost reply is reported"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "lost reply leaves operation retryable"); }
        beforeRetry = writes; await client.Update("Reminder/recurring", second, active.Text("change_tag"), default); check(writes == beforeRetry, "ambiguous completion retry recovers committed history");
        check(linkedWrites >= 3, "recurrence, tags and completion use linked atomic writes");

        var root = Path.Combine(Path.GetTempPath(), "reminders-feature-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var cache = new Cache(Path.Combine(root, "order.db")))
            {
                cache.ReplaceLists(new JsonArray(J.Node(new { id = "a", title = "A" }), J.Node(new { id = "b", title = "B" })));
                cache.SetSettings(J.Node(new { list_order = new[] { "b", "a" } }).AsObject());
                cache.ReplaceLists(new JsonArray(J.Node(new { id = "a", title = "A" }), J.Node(new { id = "c", title = "New" }), J.Node(new { id = "b", title = "B" })));
                check(cache.Lists[0].Text("id") == "b" && cache.Lists[2].Text("id") == "c", "sync preserves saved sidebar order and appends new lists");
            }
            using (var reopened = new Cache(Path.Combine(root, "order.db"))) check(reopened.Lists[0].Text("id") == "b", "sidebar order survives restart");
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "demo-account.md"))) directory = directory.Parent;
            var password = Regex.Match(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "demo-account.md")), @"- Password: `([^`]+)`").Groups[1].Value;
            await using var demo = ReminderService.OpenDemo(root, password);
            async Task<JsonNode> Call(string method, object parameters) => JsonNode.Parse((await demo.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
            var lists = await Call("lists", new { });
            var local = await Call("create_reminder", new { list_id = lists[0].Text("id"), title = "Daily", due_date = TimeUtil.Iso(DateTimeOffset.UtcNow), recurrence = new { frequency = 1, interval = 1 } });
            var localId = local.Required("id");
            await Call("create_tag", new { id = localId, name = "#STEM" });
            await Call("create_tag", new { id = localId, name = "stem" });
            check((await Call("reminder", new { id = localId })).Array("tags").Count == 1, "tag creation deduplicates names ignoring case");
            var rolled = await Call("update_reminder", new { id = localId, completed = true });
            check(!rolled.Flag("completed") && rolled["recurrence"] is not null, "local account repeats use the same calendar behavior");
            try { await Call("create_reminder", new { list_id = "a", title = "invalid", recurrence = new { frequency = 1 } }); check(false, "repeat needs a date"); }
            catch (CoreException e) { check(e.Code == "BAD_REQUEST", "reject repeating reminders without a due date"); }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
