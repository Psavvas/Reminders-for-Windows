using System.Net;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class WriteCompatibilityChecks
{
    private static JsonNode Account => J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2 }, hsaTrustedBrowser = true,
        webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } });
    private static HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };

    public static async Task RunAsync(Action<bool, string> check)
    {
        // A missing optional record is normal only for the per-record lookup
        // result. Required records, permission failures and missing zones must
        // still fail; none of those justify blindly submitting a create.
        foreach (var code in new[] { "NOT_FOUND", "UNKNOWN_ITEM", "ACCESS_DENIED", "ZONE_NOT_FOUND", "AUTHENTICATION_REQUIRED" })
        {
            using var auth = new AppleAuth(J.Node(new { webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }).AsObject(),
                () => new ResponseHandler(() => Reply(J.Node(new { records = new[] { new { recordName = "Reminder/new", serverErrorCode = code } } }))));
            var cloud = new CloudKit(auth);
            var missing = code is "NOT_FOUND" or "UNKNOWN_ITEM";
            try { check((await cloud.LookupMany(["Reminder/new"], default, true)).Count == 0 && missing, "only optional missing records are ignored: " + code); }
            catch (CoreException error) { check(!missing && error.Code == (code == "AUTHENTICATION_REQUIRED" ? "AUTH_REQUIRED" : "OPERATION_REJECTED"), "lookup retains real access errors: " + code); }
            if (missing)
            {
                try { await cloud.Lookup("Reminder/new", default); check(false, "required lookup must reject missing records"); }
                catch (CoreException error) { check(error.Code == "OPERATION_REJECTED", "required lookup still rejects " + code); }
            }
        }

        var diagnostics = new List<string>();
        using (var auth = new AppleAuth(J.Node(new { webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }).AsObject(),
            () => new ResponseHandler(() => Reply(J.Node(new { serverErrorCode = "NOT_FOUND", reason = "private reason", recordName = "private identity" })))))
        {
            auth.Diagnostic += diagnostics.Add;
            try { await new CloudKit(auth).LookupMany(["Reminder/new"], default, true); check(false, "top-level lookup failure must not become an empty successful lookup"); }
            catch (CoreException error) { check(error.Code == "OPERATION_REJECTED", "optional lookup never swallows top-level failure"); }
        }
        check(diagnostics.Contains("icloud: records/lookup server_error=NOT_FOUND"), "diagnostics identify endpoint and record error code");
        check(!diagnostics.Any(line => line.Contains("private identity") || line.Contains("private reason")), "diagnostics omit record identities and server reason text");
        using (var auth = new AppleAuth(J.Node(new { webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }).AsObject(),
            () => new ResponseHandler(() => Reply(J.Node(new { records = new[] { new { serverErrorCode = "private\naccount@example.com", reason = "private" } } })))))
        {
            auth.Diagnostic += diagnostics.Add;
            try { await new CloudKit(auth).Lookup("Reminder/test", default); } catch (CoreException) { }
        }
        check(diagnostics.Last() == "icloud: records/lookup server_error=UNKNOWN", "unexpected error codes cannot inject private text into diagnostics");

        var root = Path.Combine(Path.GetTempPath(), "reminders-write-compatibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cache = new Cache(Path.Combine(root, "cache.db"));
            var secrets = new SecretStore(new MemorySecrets()); secrets.Session("test@example.com", J.Node(new { session_token = "test" }).ToJsonString());
            var records = new Dictionary<string, JsonObject>(); var writes = 0; var rejectedLookups = 0;
            JsonObject Record(string id) => J.Node(new { recordName = id, recordType = "Reminder", recordChangeTag = "old", fields = new { List = new { type = "REFERENCE", value = new { recordName = "List/test" } }, Completed = new { type = "INT64", value = 0 } } }).AsObject();
            records["Reminder/good"] = Record("Reminder/good");
            using var client = new ICloudClient("test@example.com", secrets, () => new ScriptHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath; var body = J.Parse(await request.Content!.ReadAsStringAsync())!;
                if (path.EndsWith("/accountLogin")) return Reply(Account);
                if (path.EndsWith("/records/lookup"))
                {
                    var results = new JsonArray();
                    foreach (var row in body.Array("records"))
                    {
                        var id = row!.Required("recordName");
                        if (id == "Reminder/rejected") rejectedLookups++;
                        results.Add(records.TryGetValue(id, out var record) ? record.DeepClone() : J.Node(new { recordName = id, serverErrorCode = "NOT_FOUND" }));
                    }
                    return Reply(new JsonObject { ["records"] = results });
                }
                if (path.EndsWith("/records/modify"))
                {
                    writes++; var results = new JsonArray();
                    foreach (var operation in body.Array("operations"))
                    {
                        var supplied = operation!["record"]!; var id = supplied.Required("recordName");
                        if (operation.Text("operationType") == "create") records[id] = supplied.DeepClone().AsObject();
                        else foreach (var field in supplied["fields"]!.AsObject()) records[id]["fields"]![field.Key] = field.Value?.DeepClone();
                        records[id]["recordChangeTag"] = writes.ToString(); results.Add(records[id].DeepClone());
                    }
                    return Reply(new JsonObject { ["records"] = results });
                }
                if (path.EndsWith("/changes/zone")) return Reply(J.Node(new { zones = new[] { new { records = records.Values.ToArray(), syncToken = "cursor" } } }));
                throw new InvalidOperationException("Unexpected endpoint");
            }));
            check(await client.Restore(default), "write compatibility account restores");
            cache.Upsert(ICloudClient.ToReminder(Record("Reminder/rejected"))!, true);
            cache.Enqueue("Reminder/rejected", "update", J.Node(new { title = "Rejected edit" }), "old");
            cache.Enqueue("Reminder/rejected", "update", J.Node(new { completed = true }), "old");
            for (var i = 0; i < 120; i++) cache.Enqueue("Reminder/rejected", "update", J.Node(new { flagged = true }), "old");
            cache.Upsert(ICloudClient.ToReminder(records["Reminder/good"])!, true);
            cache.Enqueue("Reminder/good", "update", J.Node(new { completed = true }), "old");
            var create = J.Node(new { id = "local/new", cloud_id = "Reminder/new", list_id = "List/test", title = "New reminder" });
            cache.Upsert(create, true); cache.Enqueue("local/new", "create", create, null);
            cache.Enqueue("local/new", "update", J.Node(new { title = "Edited before upload" }), null);
            var events = new List<string>(); var sync = new SyncEngine(cache, client, new SemaphoreSlim(1), (name, _) => events.Add(name));
            var result = await sync.Sync(true, default).WaitAsync(TimeSpan.FromSeconds(5));
            check(result["push"].Number("pushed") == 3 && result["push"].Number("failed") == 1, "a rejected head allows unrelated completion, creation and following edit to upload");
            check(CloudKit.Int(records["Reminder/good"], "Completed") == 1 && ICloudClient.ToReminder(records["Reminder/new"])!.Text("title") == "Edited before upload", "valid writes reach iCloud with creation aliases rebased");
            check(cache.Pending(1000).Count == 122 && cache.Pending(1000).All(row => row.Text("reminder_id") == "Reminder/rejected"), "rejected edits remain queued without starving unrelated writes behind more than 100 dependent edits");
            check(cache.Pending()[0].Number("attempts") == 1 && cache.Pending()[1].Number("attempts") == 0 && rejectedLookups == 1, "failed items retry once per sync and later edits on the same reminder do not overtake them");
            check(events.Last() == "sync_warning" && events.Contains("sync_finished"), "partial upload reports a warning after downloading current data");
            var retry = await sync.Sync(false, default).WaitAsync(TimeSpan.FromSeconds(5));
            check(retry["push"].Number("pushed") == 0 && rejectedLookups == 2 && writes == 3, "next sync retries retained edits without repeating successful writes or spinning");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
