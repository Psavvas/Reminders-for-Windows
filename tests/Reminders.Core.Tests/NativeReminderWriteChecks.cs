using System.Net;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class NativeReminderWriteChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var due = DateTimeOffset.UtcNow.AddHours(-1);
        var records = new Dictionary<string, JsonObject>
        {
            ["Reminder/phone"] = new()
            {
                ["recordName"] = "Reminder/phone", ["recordType"] = "Reminder", ["recordChangeTag"] = "phone-revision",
                ["fields"] = new JsonObject
                {
                    ["TitleDocument"] = J.Node(new { type = "ENCRYPTED_BYTES", value = AppleDocumentEditingChecks.NativeDocument }),
                    ["NotesDocument"] = CloudKit.String(AppleDocumentEditingChecks.NativeDocument),
                    ["TitleDocumentAsset"] = J.Node(new { type = "ASSETID", value = new { downloadURL = "https://example.invalid/native-document" } }),
                    ["List"] = CloudKit.Reference("List/test"), ["TimeZone"] = CloudKit.String("UTC"),
                    ["DueDate"] = CloudKit.Timestamp(TimeUtil.ToFloating(due, "UTC")),
                    ["StartDate"] = CloudKit.Timestamp(TimeUtil.ToFloating(due.AddHours(-1), "UTC")),
                    ["Completed"] = CloudKit.Number(0), ["Priority"] = CloudKit.Number(5), ["Flagged"] = CloudKit.Number(1), ["AllDay"] = CloudKit.Number(0),
                    ["RecurrenceRuleIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "phone-rule" } }),
                    ["AlarmIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "phone-alarm" } }),
                    ["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "phone-tag" } }),
                    ["AttachmentIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "phone-attachment" } }),
                    ["AssignmentIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "phone-assignment" } }),
                    ["ParentReminder"] = CloudKit.Reference("Reminder/parent"),
                    ["UserActivity"] = J.Node(new { type = "BYTES", value = "AA==" }),
                    ["ResolutionTokenMap"] = CloudKit.String("{\"map\":{\"titleDocument\":{\"counter\":7,\"replicaID\":\"PHONE\"},\"dueDate\":{\"counter\":4}}}")
                }
            },
            ["RecurrenceRule/phone-rule"] = new()
            {
                ["recordName"] = "RecurrenceRule/phone-rule", ["recordType"] = "RecurrenceRule", ["recordChangeTag"] = "rule-revision",
                ["fields"] = new JsonObject
                {
                    ["Frequency"] = CloudKit.Number(1), ["Interval"] = CloudKit.Number(1),
                    ["DaysOfTheWeek"] = J.Node(new { type = "BYTES", value = "W10=" }),
                    ["MonthsOfTheYear"] = J.Node(new { type = "ENCRYPTED_BYTES", value = "W10=" }),
                    ["Reminder"] = CloudKit.Reference("Reminder/phone")
                }
            }
        };
        var writes = 0; var loseReply = false;
        var secrets = new SecretStore(new MemorySecrets()); secrets.Session("test@example.com", J.Node(new { session_token = "mock" }).ToJsonString());
        HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
        {
            var body = J.Parse(await request.Content!.ReadAsStringAsync())!; var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
            if (path.EndsWith("/records/lookup")) return Reply(new JsonObject { ["records"] = new JsonArray(body.Array("records").Select(row => records.TryGetValue(row!.Required("recordName"), out var record) ? record.DeepClone() : J.Node(new { recordName = row.Text("recordName"), serverErrorCode = "NOT_FOUND" })).ToArray()) });
            if (!path.EndsWith("/records/modify")) throw new InvalidOperationException("Unexpected endpoint");
            var operations = body.Array("operations");
            foreach (var operation in operations)
            {
                var supplied = operation!["record"]!; var fields = supplied["fields"]!.AsObject();
                if (operation.Text("operationType") != "create") continue;
                check(body.Flag("atomic"), "native repeat completion and history commit atomically");
                // A strict service mock rejects the previous clone-every-field
                // implementation, instead of silently accepting arbitrary types.
                if (fields.Any(p => p.Key is "TitleDocumentAsset" or "UserActivity" or "ParentReminder" or "AssignmentIDs" or "AlarmIDs" or "HashtagIDs" or "AttachmentIDs"
                    || p.Value.Text("type") is "ENCRYPTED_BYTES" or "ASSETID"))
                    return Reply(J.Node(new { records = new[] { new { recordName = supplied.Text("recordName"), serverErrorCode = "VALIDATION_FAILED", reason = "Unsupported native field in create" } } }));
                check(!records.ContainsKey(supplied.Required("recordName")), "native completion retry never duplicates history");
            }
            writes++; var result = new JsonArray();
            foreach (var operation in operations)
            {
                var supplied = operation!["record"]!; var id = supplied.Required("recordName");
                if (operation.Text("operationType") == "create") records[id] = supplied.DeepClone().AsObject();
                else
                {
                    check(supplied.Text("recordChangeTag") == records[id].Text("recordChangeTag"), "native reminder writes retain revision checks");
                    foreach (var field in supplied["fields"]!.AsObject()) records[id]["fields"]![field.Key] = field.Value?.DeepClone();
                }
                records[id]["recordChangeTag"] = writes.ToString();
                result.Add(new JsonObject { ["recordName"] = id, ["recordChangeTag"] = writes.ToString(), ["fields"] = supplied["fields"]!.DeepClone() });
            }
            if (loseReply) { loseReply = false; throw new HttpRequestException("Lost completion reply"); }
            return Reply(new JsonObject { ["records"] = result });
        }));
        check(await client.Restore(default), "native reminder write fixture signs in");
        var renamed = await client.Update("Reminder/phone", J.Node(new { title = "New_Name", description = "New notes" }).AsObject(), "phone-revision", default);
        check(renamed.Text("title") == "New_Name" && renamed.Text("description") == "New notes", "phone-created title and notes replace exactly on upload");
        var map = J.Parse(CloudKit.Text(records["Reminder/phone"], "ResolutionTokenMap"));
        check(map?["map"]?["titleDocument"].Number("counter") == 8 && map?["map"]?["dueDate"].Number("counter") == 4, "native text edits increment their tokens and preserve unrelated counters");
        var completion = J.Node(new { completed = true, completion_id = "Reminder/phone-history" }).AsObject();
        loseReply = true;
        try { await client.Update("Reminder/phone", completion, renamed.Text("change_tag"), default); check(false, "lost native completion reply must remain retryable"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "native completion is accepted before a simulated lost reply"); }
        var beforeRetry = writes;
        var active = await client.Update("Reminder/phone", completion, renamed.Text("change_tag"), default);
        check(writes == beforeRetry, "recover native completion before stale-revision comparison without advancing twice");
        check(!active.Flag("completed") && Math.Abs((TimeUtil.Parse(active.Required("due_date")) - due.AddDays(7)).TotalMilliseconds) < 1, "phone-created weekly recurrence advances by seven days");
        var history = active["completed_occurrence"]!;
        check(history.Flag("completed") && history.Text("title") == "New_Name" && history.Text("description") == "New notes", "native history retains the renamed title and notes");
        check(history.Number("priority") == 5 && history.Flag("flagged") && history.Text("list_id") == "List/test", "native history retains priority, flag and list");
        check(CloudKit.Field(records["Reminder/phone-history"], "DueDate")!.GetValue<long>() == TimeUtil.ToFloating(due, "UTC"), "history retains the completed occurrence's due date");
        check(CloudKit.Field(records["Reminder/phone"], "HashtagIDs")!.AsArray().Count == 1 && CloudKit.Field(records["Reminder/phone"], "AttachmentIDs")!.AsArray().Count == 1, "completion preserves native relationships on the active reminder");
        check(history.Array("recurrence_ids").Count == 0 && records["Reminder/phone-history"]["parent"].Text("recordName") == "List/test", "completed history has a list parent and no recurrence");
        var historyTokens = J.Parse(CloudKit.Text(records["Reminder/phone-history"], "ResolutionTokenMap"))!["map"]!.AsObject();
        check(!historyTokens.ContainsKey("alarmIDs") && !historyTokens.ContainsKey("attachmentIDs") && !historyTokens.ContainsKey("hashtagIDs"), "new history does not invent conflict-resolution tokens for linked records");
        var notes = await client.Update("Reminder/phone", J.Node(new { description = "Edited after completion" }).AsObject(), active.Text("change_tag"), default);
        check(notes.Text("title") == "New_Name" && notes.Text("description") == "Edited after completion", "native reminder remains editable after completing its occurrence");

        var constrained = records["RecurrenceRule/phone-rule"].DeepClone();
        constrained["fields"]!["DaysOfTheWeek"] = J.Node(new { type = "BYTES", value = "WzJd" }); // [2]
        check(!Recurrence.Describe(constrained).Flag("supported"), "nonempty native repeat constraints remain protected");
        constrained["fields"]!["DaysOfTheWeek"] = J.Node(new { type = "BYTES", value = "invalid!" });
        check(!Recurrence.Describe(constrained).Flag("supported"), "malformed native repeat constraints remain protected");
    }
}
