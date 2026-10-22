using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class CacheRecoveryChecks
{
    private static JsonNode Account => J.Node(new { dsInfo = new { dsid = "1", appleId = "test@example.com", hsaVersion = 2 }, hsaTrustedBrowser = true,
        webservices = new { ckdatabasews = new { url = "https://p01-ckdatabasews.icloud.com" } } });
    private static JsonNode RemoteList => J.Node(new { recordName = "list", recordType = "List", fields = new { Name = new { type = "STRING", value = "Fresh list" } } });
    private static JsonNode RemoteReminder => J.Node(new { recordName = "fresh", recordType = "Reminder", fields = new { Title = new { type = "STRING", value = "Fresh reminder" }, List = new { type = "REFERENCE", value = new { recordName = "list" } } } });
    private static HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
    private static async Task<JsonNode> Call(ReminderService service, string method, object? parameters = null) =>
        J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters ?? new { }))).GetRawText())!;

    public static async Task RunAsync(Action<bool, string> check)
    {
        try { CloudKit.Reject([J.Node(new { serverErrorCode = "INTERNAL_ERROR" })]); check(false, "transient server errors stay retryable"); }
        catch (CoreException error) { check(error.Code == "NETWORK", "transient errors retain the released restoration retry behavior"); }
        var root = Path.Combine(Path.GetTempPath(), "reminders-cache-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var vault = new MemorySecrets(); var secrets = new SecretStore(vault);
        secrets.Password("test@example.com", "retained-password"); secrets.Session("test@example.com", "retained-session");
        try
        {
            var protectedRoot = Path.Combine(root, "protected"); Directory.CreateDirectory(protectedRoot);
            using (var cache = new Cache(Path.Combine(protectedRoot, "cache.db")))
            {
                cache.ReplaceLists(new JsonArray(J.Node(new { id = "list", title = "Old list" })));
                cache.Upsert(J.Node(new { id = "dirty", list_id = "list", title = "Unsynced edit" }), true);
                cache.Enqueue("dirty", "update", J.Node(new { title = "Unsynced edit" }), "old");
                cache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES('dirty','{}','{}','now')");
                cache.Execute("INSERT INTO related_records(id,record_type,json) VALUES('rule','RecurrenceRule','{}')");
                cache.ReplaceTags("dirty", new JsonArray(J.Node(new { id = "tag", name = "school" })));
                foreach (var key in new[] { "sync_cursor", "last_sync", "last_full_sync", "cloudkit_read_protocol", "id_alias:old" }) cache.SetMeta(key, "old");
                cache.SetMeta("apple_id", "test@example.com"); cache.SetMeta("signed_out", "1");
                cache.SetSettings(J.Node(new { theme = "dark", list_order = new[] { "list" } }).AsObject());
            }
            var requests = 0;
            await using (var service = new ReminderService(protectedRoot, vault, () => new ScriptHandler(_ => { requests++; return Task.FromResult(Reply(new JsonObject())); })))
            {
                try { await Call(service, "clear_cache"); check(false, "cache reset must protect queued edits"); }
                catch (CoreException error) { check(error.Code == "PENDING_CHANGES", "cache reset requires explicit permission to discard unsynced changes"); }
                check((await Call(service, "sync_status")).Number("pending_pushes") == 1 && (await Call(service, "lists")).AsArray().Count == 1, "declined reset preserves the cache and outbox");
                var result = await Call(service, "clear_cache", new { discard_pending = true });
                check(result.Flag("cleared") && result.Number("discarded_pending") == 1 && result.Number("discarded_conflicts") == 1, "confirmed reset clears rejected edits and unresolved conflicts");
                check((await Call(service, "auth_status")).Flag("signed_out") && requests == 0, "cache reset preserves explicit sign-out and makes no cloud writes");
            }
            using (var cache = new Cache(Path.Combine(protectedRoot, "cache.db")))
            {
                foreach (var table in new[] { "lists", "reminders", "tags", "outbox", "conflicts", "related_records" })
                    check(cache.Query($"SELECT COUNT(*) AS count FROM {table}")[0].Number("count") == 0, "reset removes cached " + table);
                foreach (var key in new[] { "sync_cursor", "last_sync", "last_full_sync", "cloudkit_read_protocol", "id_alias:old" })
                    check(cache.Meta(key) is null, "reset removes stale sync metadata " + key);
                check(cache.Meta("apple_id") == "test@example.com" && cache.Settings.Text("theme") == "dark" && cache.Settings.Array("list_order").Count == 1, "reset preserves account identity and app preferences");
            }
            check(secrets.Password("test@example.com") == "retained-password" && secrets.Session("test@example.com") == "retained-session", "cache reset preserves saved sign-in credentials");

            var liveRoot = Path.Combine(root, "active"); Directory.CreateDirectory(liveRoot);
            var liveVault = new MemorySecrets(); new SecretStore(liveVault).Session("test@example.com", J.Node(new { session_token = "test-token" }).ToJsonString());
            using (var cache = new Cache(Path.Combine(liveRoot, "cache.db")))
            {
                cache.SetMeta("apple_id", "test@example.com"); cache.SetMeta("sync_cursor", "old-cursor"); cache.SetMeta("cloudkit_read_protocol", "4");
                cache.ReplaceLists(new JsonArray(J.Node(new { id = "stale", title = "Stale list" })));
            }
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloads = 0; var writes = 0;
            await using (var active = new ReminderService(liveRoot, liveVault, () => new RecoveryHandler(async (request, token) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/accountLogin")) return Reply(Account);
                if (!path.EndsWith("/changes/zone")) { writes++; throw new InvalidOperationException("unexpected write or request"); }
                var body = J.Parse(await request.Content!.ReadAsStringAsync(token));
                if (Interlocked.Increment(ref downloads) == 1) { blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                check(body.Array("zones")[0].Text("syncToken") is null, "reset starts a full download without the stale cursor");
                return Reply(J.Node(new { zones = new[] { new { records = new[] { RemoteList, RemoteReminder }, syncToken = "fresh-cursor" } } }));
            })))
            {
                active.EventReceived += (name, _) => { if (name == "sync_finished") refreshed.TrySetResult(); };
                await active.StartAsync(); await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Call(active, "clear_cache").WaitAsync(TimeSpan.FromSeconds(5));
                await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check((await Call(active, "auth_status")).Flag("authenticated") && writes == 0, "reset cancels an active download, keeps sign-in, and never deletes cloud records");
                var lists = (await Call(active, "lists")).AsArray();
                check(lists.Count == 1 && lists[0].Text("title") == "Fresh list", "only the fresh snapshot repopulates the cache after reset");
            }

            using var pushCache = new Cache(Path.Combine(root, "rejected.db"));
            pushCache.ReplaceLists(new JsonArray(J.Node(new { id = "list", title = "Old list" })));
            pushCache.Upsert(J.Node(new { id = "dirty", list_id = "list", title = "Keep this local edit" }), true);
            pushCache.Enqueue("dirty", "update", J.Node(new { title = "Keep this local edit" }), "old");
            var pushSecrets = new SecretStore(new MemorySecrets()); pushSecrets.Session("test@example.com", J.Node(new { session_token = "token" }).ToJsonString());
            using var client = new ICloudClient("test@example.com", pushSecrets, () => new ScriptHandler(request => Task.FromResult(Reply(
                request.RequestUri!.AbsolutePath.EndsWith("/accountLogin") ? Account : request.RequestUri.AbsolutePath.EndsWith("/records/lookup")
                    ? J.Node(new { records = new[] { new { recordName = "dirty", serverErrorCode = "UNKNOWN_ITEM" } } })
                    : J.Node(new { zones = new[] { new { records = new[] { RemoteList, RemoteReminder }, syncToken = "fresh" } } })))));
            await client.Restore(default);
            var events = new List<string>(); var sync = new SyncEngine(pushCache, client, new SemaphoreSlim(1), (name, _) => events.Add(name));
            var outcome = await sync.Sync(true, default).WaitAsync(TimeSpan.FromSeconds(5));
            check(outcome.Property("push").Number("failed") == 1 && events.Contains("sync_downloaded") && !events.Contains("sync_finished") && events.Last() == "sync_warning", "a rejected upload still downloads and ends with an actionable warning");
            check(pushCache.Reminder("fresh").Text("title") == "Fresh reminder", "a rejected queued edit does not block new iCloud reminders");
            check(pushCache.Pending().Count == 1 && pushCache.Reminder("dirty").Text("title") == "Keep this local edit", "rejected edits remain queued until explicitly discarded");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class RecoveryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
