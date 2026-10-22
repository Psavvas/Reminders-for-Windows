using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class UnsyncedRecoveryChecks
{
    private static async Task<JsonNode> Call(ReminderService service, string method, object? args = null, CancellationToken token = default) =>
        J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(args ?? new { }), token)).GetRawText())!;
    private static JsonArray Targets(JsonArray rows, params string[] ids) => new(rows.Where(row => ids.Contains(row.Text("id")))
        .Select(row => J.Node(new { id = row!.Required("id"), version = row!.Required("version") })).ToArray());

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-targeted-recovery-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "cache.db");
        JsonObject Record(string id, string title) => new() { ["recordName"] = id, ["recordType"] = "Reminder", ["recordChangeTag"] = "old", ["fields"] = new JsonObject
        {
            ["TitleDocument"] = CloudKit.String(AppleDocument.Encode(title)), ["List"] = CloudKit.Reference("List/test"), ["Completed"] = CloudKit.Number(0)
        } };
        var records = new Dictionary<string, JsonObject>
        {
            ["List/test"] = new() { ["recordName"] = "List/test", ["recordType"] = "List", ["recordChangeTag"] = "list", ["fields"] = new JsonObject { ["Name"] = CloudKit.String("Keep my list") } },
            ["Reminder/edit"] = Record("Reminder/edit", "Original title"), ["Reminder/conflict"] = Record("Reminder/conflict", "Cloud conflict"),
            ["Reminder/other"] = Record("Reminder/other", "Unselected cloud task"), ["Reminder/clean"] = Record("Reminder/clean", "Keep this synced task"),
            ["Hashtag/school"] = new() { ["recordName"] = "Hashtag/school", ["recordType"] = "Hashtag", ["recordChangeTag"] = "tag", ["fields"] = new JsonObject
            { ["Name"] = CloudKit.String("school"), ["Reminder"] = CloudKit.Reference("Reminder/edit"), ["Deleted"] = CloudKit.Number(0) } },
            ["RecurrenceRule/weekly"] = new() { ["recordName"] = "RecurrenceRule/weekly", ["recordType"] = "RecurrenceRule", ["recordChangeTag"] = "rule", ["fields"] = new JsonObject
            { ["Frequency"] = CloudKit.Number(1), ["Interval"] = CloudKit.Number(1), ["Reminder"] = CloudKit.Reference("Reminder/edit") } }
        };
        records["Reminder/child"] = Record("Reminder/child", "Keep this subtask");
        records["Reminder/child"]["parent"] = CloudKit.Reference("Reminder/edit");
        records["Reminder/edit"]["fields"]!["HashtagIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "school" } });
        records["Reminder/edit"]["fields"]!["RecurrenceRuleIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { "weekly" } });
        records["Reminder/edit"]["fields"]!["DueDate"] = CloudKit.Timestamp(TimeUtil.ToFloating(TimeUtil.Parse("2026-11-05T14:00:00Z"), "UTC"));
        records["Reminder/edit"]["fields"]!["TimeZone"] = CloudKit.String("UTC");
        var vault = new MemorySecrets(); var secrets = new SecretStore(vault);
        secrets.Session("test@example.com", J.Node(new { session_token = "test-token" }).ToJsonString());
        var cloudWrites = 0; var omitParent = false; var omitChild = false; var deny = false;
        var blockPull = false; var blockLookup = false; Action? duringLookup = null;
        var pullStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        try
        {
            using (var cache = new Cache(path))
            {
                cache.SetMeta("apple_id", "test@example.com");
                cache.SetSettings(J.Node(new { theme = "dark", list_order = new[] { "List/test" } }).AsObject());
                cache.ApplyRecords(new JsonArray(records.Values.Select(r => r.DeepClone()).ToArray()), true);
                cache.ReplaceLists(new JsonArray(J.Node(new { id = "List/test", title = "Keep my list" })));
                cache.Edit("Reminder/edit", new() { ["title"] = "Local title" });
                cache.Enqueue("Reminder/edit", "update", J.Node(new { title = "Local title" }), "old");
                cache.Edit("Reminder/other", new() { ["title"] = "Keep this unsynced edit" });
                for (var i = 0; i < 120; i++) cache.Enqueue("Reminder/other", "update", J.Node(new { title = "Keep this unsynced edit" }), "old");
                cache.Edit("Reminder/conflict", new() { ["title"] = "Local conflict title" });
                cache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES(@p0,@p1,@p2,'now')", "Reminder/conflict", cache.Reminder("Reminder/conflict")!.ToJsonString(), ICloudClient.ToReminder(records["Reminder/conflict"])!.ToJsonString());
                var draft = J.Node(new { id = "local/draft", cloud_id = "Reminder/draft", list_id = "List/test", title = "Never uploaded" });
                cache.Upsert(draft, true); cache.Enqueue("local/draft", "create", draft, null);
            }
            await using var service = new ReminderService(root, vault, () => new Handler(async (request, token) =>
            {
                var endpoint = request.RequestUri!.AbsolutePath;
                var body = J.Parse(await request.Content!.ReadAsStringAsync(token))!;
                if (endpoint.EndsWith("/accountLogin")) return Reply(J.Node(new { dsInfo = new { dsid = "1", hsaVersion = 2 }, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }));
                if (endpoint.EndsWith("/records/modify"))
                {
                    cloudWrites++;
                    return Reply(J.Node(new { records = new[] { new { recordName = body.Array("operations")[0]!["record"].Text("recordName"), serverErrorCode = "INVALID_ARGUMENTS" } } }));
                }
                if (endpoint.EndsWith("/changes/zone"))
                {
                    if (blockPull) { blockPull = false; pullStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                    return Reply(J.Node(new { zones = new[] { new { records = records.Values.ToArray(), syncToken = "preserved-cursor" } } }));
                }
                if (!endpoint.EndsWith("/records/lookup")) throw new InvalidOperationException("Unexpected recovery endpoint");
                if (blockLookup) { blockLookup = false; lookupStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                duringLookup?.Invoke(); duringLookup = null;
                if (omitParent) { omitParent = false; return Reply(J.Node(new { records = Array.Empty<object>() })); }
                var rows = new JsonArray();
                foreach (var item in body.Array("records"))
                {
                    var id = item!.Required("recordName");
                    if (omitChild && id.StartsWith("Hashtag/")) continue;
                    if (deny) rows.Add(J.Node(new { recordName = id, serverErrorCode = "ACCESS_DENIED" }));
                    else rows.Add(records.TryGetValue(id, out var record) ? record.DeepClone() : J.Node(new { recordName = id, serverErrorCode = "NOT_FOUND" }));
                }
                return Reply(new JsonObject { ["records"] = rows });
            }));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.EventReceived += (name, _) => { if (name == "sync_warning") ready.TrySetResult(); };
            await service.StartAsync(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var preview = (await Call(service, "unsynced_reminders")).AsArray();
            check(preview.Count == 4 && preview.Any(r => r.Flag("is_new") && r.Text("id") == "local/draft"), "recovery lists distinct queued tasks and unresolved conflicts, including local drafts");
            check((await Call(service, "sync_status")).Number("pending_pushes") > 100, "Settings reports all pending uploads rather than a capped first page");
            var selected = Targets(preview, "local/draft", "Reminder/edit", "Reminder/conflict");
            var writesBefore = cloudWrites;
            async Task ExpectFailure(string code, object args, string message, CancellationToken token = default)
            {
                try { await Call(service, "discard_unsynced", args, token); check(false, message); }
                catch (CoreException error) { check(error.Code == code, message); }
            }
            await ExpectFailure("BAD_REQUEST", new { targets = selected }, "discard requires explicit confirmation");
            await ExpectFailure("BAD_REQUEST", new { confirmed = true, targets = new object[0] }, "discard requires a nonempty selection");
            await ExpectFailure("BAD_REQUEST", new { confirmed = true, targets = new[] { selected[0]!.DeepClone(), selected[0]!.DeepClone() } }, "duplicate selected tasks cannot be discarded twice");
            omitParent = true;
            await ExpectFailure("NETWORK", new { confirmed = true, targets = selected }, "an unacknowledged lookup cannot be interpreted as a deleted cloud task");
            omitChild = true;
            await ExpectFailure("NETWORK", new { confirmed = true, targets = selected }, "missing child acknowledgements leave the whole selection intact");
            omitChild = false; deny = true;
            await ExpectFailure("OPERATION_REJECTED", new { confirmed = true, targets = selected }, "access errors do not discard local changes");
            deny = false;
            check((await Call(service, "reminder", new { id = "local/draft" })) is JsonObject && (await Call(service, "conflicts")).AsArray().Count == 1, "failed recovery leaves drafts and conflicts intact");

            blockLookup = true;
            using (var cancel = new CancellationTokenSource())
            {
                var work = Call(service, "discard_unsynced", new { confirmed = true, targets = selected }, cancel.Token);
                await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
                try { await work; check(false, "cancelled recovery must stop"); }
                catch (OperationCanceledException) { check(true, "cancelling recovery stops reads before changing the cache"); }
            }
            duringLookup = () =>
            {
                using var cache = new Cache(path);
                cache.Transaction(() => { cache.Edit("Reminder/edit", new() { ["title"] = "Newer local edit" }); cache.Enqueue("Reminder/edit", "update", J.Node(new { title = "Newer local edit" }), "old"); return true; });
            };
            await ExpectFailure("STALE_SELECTION", new { confirmed = true, targets = selected }, "an edit arriving during cloud lookup invalidates the confirmation atomically");
            check((await Call(service, "reminder", new { id = "Reminder/edit" })).Text("title") == "Newer local edit" && (await Call(service, "reminder", new { id = "local/draft" })) is JsonObject, "stale recovery preserves both the newer edit and the other selected draft");
            await ExpectFailure("STALE_SELECTION", new { confirmed = true, targets = new[] { new { id = "Reminder/clean", version = "not-reviewed" } } }, "a synced unselected task cannot be discarded");
            check(cloudWrites == writesBefore, "review, failed confirmation and cancellation make no cloud writes");

            records["Reminder/edit"]["fields"]!["TitleDocument"] = CloudKit.String(AppleDocument.Encode("Latest iCloud title")); records["Reminder/edit"]["recordChangeTag"] = "fresh";
            records["Reminder/conflict"]["fields"]!["TitleDocument"] = CloudKit.String(AppleDocument.Encode("Latest conflict copy")); records["Reminder/conflict"]["recordChangeTag"] = "fresh";
            preview = (await Call(service, "unsynced_reminders")).AsArray(); selected = Targets(preview, "local/draft", "Reminder/edit", "Reminder/conflict");
            JsonNode cleanBefore; string? cursorBefore; string? otherPayloads; string? childBefore;
            using (var cache = new Cache(path)) { cleanBefore = cache.Reminder("Reminder/clean")!.DeepClone(); cursorBefore = cache.Meta("sync_cursor"); childBefore = cache.Query("SELECT json FROM related_records WHERE id='Reminder/child'")[0].Text("json"); otherPayloads = cache.Query("SELECT * FROM outbox WHERE reminder_id='Reminder/other' ORDER BY seq").ToJsonString(); }
            var result = await Call(service, "discard_unsynced", new { confirmed = true, targets = selected });
            check(result.Number("removed_local") == 1 && result.Number("restored_from_icloud") == 2, "selected local drafts are removed and cloud-backed tasks are restored");
            check((await Call(service, "reminder", new { id = "local/draft" })) is null && (await Call(service, "reminder", new { id = "Reminder/edit" })).Text("title") == "Latest iCloud title", "recovery restores the latest server copy instead of an older cached title");
            check((await Call(service, "conflicts")).AsArray().Count == 0, "selected merge conflicts are resolved without clearing other cache data");
            using (var cache = new Cache(path))
            {
                check(cache.Reminder("Reminder/edit")!.Array("tags")[0]!.ToString() == "school" && cache.Reminder("Reminder/edit")!["recurrence"].Number("frequency") == 2, "restored tasks retain their downloaded tags and recurrence");
                check(JsonNode.DeepEquals(cleanBefore, cache.Reminder("Reminder/clean")) && cache.Meta("sync_cursor") == cursorBefore && cache.Query("SELECT json FROM related_records WHERE id='Reminder/child'")[0].Text("json") == childBefore, "synced tasks, subtask records and the delta cursor survive targeted recovery unchanged");
                check(cache.Query("SELECT * FROM outbox WHERE reminder_id='Reminder/other' ORDER BY seq").ToJsonString() == otherPayloads && cache.Reminder("Reminder/other")!.Text("title") == "Keep this unsynced edit", "unselected uploads and their local task remain untouched");
                check(cache.Settings.Text("theme") == "dark" && cache.Settings.Array("list_order").Count == 1 && cache.Lists.Count == 1, "targeted recovery retains app preferences and sidebar order");
            }
            check(cloudWrites == writesBefore, "successful targeted recovery never modifies or deletes iCloud records");

            // A stable create may already be committed when its reply was lost.
            records["Reminder/committed"] = Record("Reminder/committed", "Already saved in iCloud");
            using (var cache = new Cache(path))
            {
                var draft = J.Node(new { id = "local/committed", cloud_id = "Reminder/committed", list_id = "List/test", title = "Ambiguous local create" });
                cache.Upsert(draft, true); cache.Enqueue("local/committed", "create", draft, null);
            }
            preview = (await Call(service, "unsynced_reminders")).AsArray();
            result = await Call(service, "discard_unsynced", new { confirmed = true, targets = Targets(preview, "local/committed") });
            check(result.Number("restored_from_icloud") == 1 && (await Call(service, "reminder", new { id = "local/committed" })).Text("title") == "Already saved in iCloud", "ambiguous creates recover the cloud copy and old-id alias instead of deleting a synced task");

            foreach (var gone in new[] { "missing", "soft-deleted" })
            {
                var id = "Reminder/" + gone;
                using (var cache = new Cache(path))
                {
                    cache.Upsert(ICloudClient.ToReminder(Record(id, "Gone from iCloud"))!, true);
                    cache.Enqueue(id, "update", J.Node(new { title = "Unsynced edit" }), "old");
                }
                if (gone == "soft-deleted") { records[id] = Record(id, "Gone from iCloud"); records[id]["fields"]!["Deleted"] = CloudKit.Number(1); }
                preview = (await Call(service, "unsynced_reminders")).AsArray();
                result = await Call(service, "discard_unsynced", new { confirmed = true, targets = Targets(preview, id) });
                check(result.Number("removed_local") == 1 && (await Call(service, "reminder", new { id })) is null,
                    "a cloud task confirmed " + gone + " is removed locally without another cloud deletion");
            }

            // An in-flight background download must finish cancelling before
            // the recovery snapshot can replace any selected local row.
            blockPull = true; await Call(service, "sync"); await pullStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writesBefore = cloudWrites; preview = (await Call(service, "unsynced_reminders")).AsArray();
            result = await Call(service, "discard_unsynced", new { confirmed = true, targets = Targets(preview, "Reminder/other") }).WaitAsync(TimeSpan.FromSeconds(5));
            check(result.Number("restored_from_icloud") == 1 && (await Call(service, "sync_status")).Number("pending_pushes") == 0, "discard cancels an active sync and drains only the selected task's upload chain");
            check(cloudWrites == writesBefore && (await Call(service, "reminder", new { id = "Reminder/clean" })) is JsonObject, "recovery after cancellation keeps the rest of the cache without cloud deletion");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
