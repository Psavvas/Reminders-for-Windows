using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class SignOutChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string account = "test@example.com";
        var root = Path.Combine(Path.GetTempPath(), "reminders-sign-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vault = new MemorySecrets(); var secrets = new SecretStore(vault);
        var state = J.Node(new { browser_cookie_session = true, session_token = "saved-token", trust_token = "saved-trust", cookies = new[]
        {
            new { name = "X-APPLE-WEBAUTH-TOKEN", value = "test-cookie", domain = ".icloud.com", path = "/", expires = 0L }
        } }).AsObject();
        secrets.Password(account, "saved-password"); secrets.Session(account, state.ToJsonString());
        var requests = 0;
        HttpMessageHandler Handler() => new ScriptHandler(request =>
        {
            Interlocked.Increment(ref requests);
            var response = request.RequestUri!.AbsolutePath.EndsWith("/validate")
                ? J.Node(new { dsInfo = new { dsid = "1", appleId = account, hsaVersion = 2 }, hsaTrustedBrowser = true, hsaChallengeRequired = false })
                : J.Node(new { records = Array.Empty<object>() });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) });
        });
        async Task<JsonNode> Call(ReminderService service, string method, object? parameters = null) =>
            J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters ?? new { }))).GetRawText())!;
        try
        {
            using (var cache = new Cache(Path.Combine(root, "cache.db")))
            {
                cache.SetMeta("apple_id", account);
                cache.ReplaceLists(new JsonArray(J.Node(new { id = "list", title = "Test" })));
                cache.Upsert(J.Node(new { id = "queued", list_id = "list", title = "Offline edit", due_date = "2026-01-01T12:00:00Z", notified = 0 }), true);
                cache.Enqueue("queued", "update", J.Node(new { title = "Offline edit" }), "old");
            }
            await using (var service = new ReminderService(root, vault, Handler))
            {
                check((await Call(service, "sign_out")).Flag("signed_out"), "sign out returns a completed result");
                check(secrets.Password(account) is null && secrets.Session(account) is null, "sign out removes the saved password, session cookies and trust tokens");
                var status = await Call(service, "auth_status");
                check(status.Flag("signed_out") && !status.Flag("authenticated") && !status.Flag("has_cache"), "explicit sign out requires login despite a retained reminder cache");
                check(requests == 0, "sign out does not contact Apple or upload queued edits");
                check((await Call(service, "due_notifications")).Array("toasts").Count == 0, "signed-out reminders do not produce notifications");
                check((await Call(service, "browser_login", new { state })).Flag("authenticated"), "same service can sign in again after cancelling background work");
                await Call(service, "sign_out");
                check(secrets.Session(account) is null, "signing out again clears the replacement session");
            }
            // Even an independently retained credential must not override the
            // user's explicit sign-out choice on the next launch.
            secrets.Session(account, state.ToJsonString());
            var beforeRestart = requests;
            await using (var restarted = new ReminderService(root, vault, Handler))
            {
                await restarted.StartAsync();
                var status = await Call(restarted, "auth_status");
                check(status.Flag("signed_out") && !status.Flag("restoring"), "restart honors explicit sign out instead of silently restoring");
                check(!(await Call(restarted, "sync")).Flag("queued") && requests == beforeRestart, "signed-out service does not queue background sync");
                check((await Call(restarted, "sync_status")).Number("pending_pushes") == 1, "queued edits survive sign out and restart");
                var signedIn = await Call(restarted, "browser_login", new { state });
                status = await Call(restarted, "auth_status");
                check(signedIn.Flag("authenticated") && !status.Flag("signed_out") && status.Flag("has_cache"), "a successful new sign-in unlocks retained reminders");
            }
            using var retained = new Cache(Path.Combine(root, "cache.db"));
            check(retained.Lists.Count == 1 && retained.Reminder("queued").Text("title") == "Offline edit", "signing out and back in preserves local reminders");
            secrets.Session(account, state.ToJsonString());
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var restoring = new ReminderService(root, vault, () => new BlockingHandler(started));
            await restoring.StartAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check((await Call(restoring, "sign_out").WaitAsync(TimeSpan.FromSeconds(5))).Flag("signed_out"),
                "sign out cancels a blocked background restore instead of waiting for its HTTP timeout");
            check(!(await Call(restoring, "auth_status")).Flag("authenticated") && secrets.Session(account) is null,
                "cancelled background restore cannot bring back the signed-out session");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
