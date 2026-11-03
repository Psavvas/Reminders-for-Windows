using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reminders.Core;

internal static class DemoAccountChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // Read the public fixture from its only plaintext home; never print it.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "demo-account.md"))) directory = directory.Parent;
        var docs = File.ReadAllText(Path.Combine(directory!.FullName, "docs", "demo-account.md"));
        var password = Regex.Match(docs, @"- Password: `([^`]+)`").Groups[1].Value;
        check(password.Length >= 20, "documented demo password has random credential length");
        var root = Path.Combine(Path.GetTempPath(), "reminders-demo-tests-" + Guid.NewGuid());
        async Task<JsonNode> Call(ReminderService service, string method, object? p = null) =>
            JsonNode.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(p ?? new { }))).GetRawText())!;
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); check(false, label); }
            catch (CoreException error) { check(error.Code == "AUTH_REQUIRED", label); }
        }
        try
        {
            check(DemoAccount.IsDemoUsername(" DEMO ") && !DemoAccount.IsDemoUsername("demo@example.com"), "only reserved username routes to local account");
            foreach (var invalid in new string?[] { null, "", "wrong", password + "x" })
                await Reject(() => Task.Run(() => ReminderService.OpenDemo(root, invalid)), "invalid demo credential rejected");
            check(!Directory.Exists(root), "invalid credentials cannot create demo storage");
            Directory.CreateDirectory(root);
            using (var real = new Cache(Path.Combine(root, "cache.db")))
            {
                real.SetMeta("apple_id", "existing@example.com");
                real.Upsert(J.Node(new { id = "real", list_id = "real-list", title = "Private cloud reminder" }), true);
                real.Enqueue("real", "create", new JsonObject(), null);
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var realBefore = File.ReadAllBytes(Path.Combine(root, "cache.db"));
            string id;
            await using (var demo = ReminderService.OpenDemo(root, password))
            {
                await demo.StartAsync();
                var auth = await Call(demo, "auth_status");
                check(demo.IsLocalDemo && auth.Flag("authenticated") && auth.Flag("local_only") && !auth.Flag("needs_2fa"), "local demo authenticates without two factor");
                check((await Call(demo, "lists")).AsArray().Count == 3, "demo seeds three lists");
                try { await Call(demo, "clear_cache", new { discard_pending = true }); check(false, "local-only data cannot be cleared as a cache"); }
                catch (CoreException error) { check(error.Code == "BAD_REQUEST" && (await Call(demo, "lists")).AsArray().Count == 3, "cache reset refuses to erase a local-only account"); }
                check(await Call(demo, "reminder", new { id = "real" }) is null, "demo cannot read cloud reminders");
                foreach (var scope in new[] { "today", "upcoming", "completed", "deleted" })
                    check((await Call(demo, "reminders", new { scope })).AsArray().Count > 0, "sample data covers " + scope);
                check((await Call(demo, "reminders", new { tag = "errands" })).AsArray().Count == 1, "sample tag filtering");
                var row = await Call(demo, "create_reminder", new { list_id = "demo-work", title = "Local flight plan", description = "Test notes", due_date = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"), priority = 1, flagged = true });
                id = row.Required("id");
                check(row.Number("dirty") == 0 && row.Flag("flagged") && row.Number("priority") == 1, "local create saves clean reminder fields");
                await Call(demo, "update_reminder", new { id, title = "Edited flight plan", description = "Updated notes" });
                await Call(demo, "update_reminder", new { id, list_id = "demo-personal" });
                check((await Call(demo, "reminder", new { id })).Text("list_id") == "demo-personal", "local demo can move existing tasks between lists");
                check((await Call(demo, "reminders", new { search = "Edited flight", sort = "title" })).AsArray().Count == 1, "local edit and search");
                var notifications = await Call(demo, "due_notifications");
                check(notifications.Array("notified_ids").Any(n => n?.GetValue<string>() == id), "local due notifications");
                await Call(demo, "update_reminder", new { id, completed = true });
                check((await Call(demo, "reminders", new { scope = "completed" })).AsArray().Any(n => n.Text("id") == id && n.Text("completed_date") is not null), "local completion date saved");
                await Call(demo, "delete_reminder", new { id });
                check((await Call(demo, "reminders", new { scope = "deleted" })).AsArray().Any(n => n.Text("id") == id), "local delete");
                await Call(demo, "restore_reminder", new { id });
                check(!(await Call(demo, "reminder", new { id })).Flag("deleted"), "local restore");
                await Call(demo, "update_reminder", new { id, completed = false });
                check((await Call(demo, "reminder", new { id })).Text("completed_date") is null, "local uncomplete clears completion date");
                await Call(demo, "set_settings", new { theme = "dark", default_list_id = "demo-work" });
                check(!(await Call(demo, "sync", new { full = true })).Flag("queued"), "demo never schedules cloud sync");
                check((await Call(demo, "sync_status")).Number("pending_pushes") == 0, "demo mutations never queue uploads");
                await Reject(() => Call(demo, "request_2fa", new { method = "sms" }), "demo cannot request Apple verification");
                await Reject(() => Call(demo, "submit_2fa", new { code = "123456" }), "demo cannot submit Apple verification");
            }
            await using (var reopened = ReminderService.OpenDemo(root, password))
            {
                check((await Call(reopened, "reminder", new { id })).Text("title") == "Edited flight plan", "local edits persist across sessions");
                check((await Call(reopened, "reminder", new { id })).Text("list_id") == "demo-personal", "local list moves persist across sessions");
                check((await Call(reopened, "settings")).Text("theme") == "dark", "local settings persist across sessions");
                check((await Call(reopened, "reminders", new { include_completed = true })).AsArray().Count == 6, "reopening does not duplicate or reset samples");
            }
            check(realBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "cache.db"))), "demo leaves cloud database byte-for-byte unchanged");
            var requests = 0;
            await using var cloud = new ReminderService(Path.Combine(root, "normal"), new MemorySecrets(), () => new RejectHandler(() => requests++));
            await Reject(() => Call(cloud, "login", new { apple_id = "demo", password }), "cloud engine rejects reserved username");
            check(requests == 0, "reserved username never reaches Apple HTTP");
            await Reject(() => Call(cloud, "login", new { apple_id = "demo@example.com", password = "test" }), "ordinary sign-in still uses Apple authentication");
            check(requests > 0, "ordinary Apple ID routes to HTTP");

            await using var frontend = new Reminders.Windows.Services.SyncClient(Path.Combine(root, "frontend"));
            Reminders.Windows.Services.SyncEventArgs? lastEvent = null;
            frontend.EventReceived += (_, args) => lastEvent = args;
            await frontend.StartAsync();
            check(frontend.IsRunning && !frontend.IsLocalDemo, "frontend starts with normal account service");
            var cloudEvent = lastEvent!;
            try { await frontend.SignInDemoAsync("wrong"); check(false, "frontend rejects wrong password"); }
            catch (Reminders.Windows.SyncException error) { check(error.Code == "AUTH_REQUIRED", "frontend rejects wrong password"); }
            check(!frontend.IsLocalDemo && frontend.IsCurrentEvent(cloudEvent), "failed local login preserves normal session");
            await frontend.SignInDemoAsync(password);
            var localEvent = lastEvent!;
            check(frontend.IsLocalDemo && frontend.IsCurrentEvent(localEvent) && !frontend.IsCurrentEvent(cloudEvent), "frontend switches sessions and rejects stale events");
            check((await frontend.CallAsync("lists")).GetArrayLength() == 3, "frontend uses local service for data requests");
            await frontend.DisposeAsync();
            await frontend.StartAsync();
            check(!frontend.IsLocalDemo && !frontend.IsCurrentEvent(localEvent), "leaving local account restores normal service");
            check((await frontend.CallAsync("lists")).GetArrayLength() == 0, "demo lists never leak into normal account");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RejectHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        }
    }
}
