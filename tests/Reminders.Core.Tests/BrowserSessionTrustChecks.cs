using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class BrowserSessionTrustChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var state = J.Node(new { browser_cookie_session = true, client_id = "browser-client", cookies = new[]
        {
            new { name = "X-APPLE-WEBAUTH-TOKEN", value = "test-cookie", domain = ".icloud.com", path = "/", expires = 0L }
        } }).AsObject();
        JsonNode Account(bool challenge = false, string account = "test@example.com") => J.Node(new
        {
            dsInfo = new { dsid = "1", appleId = account, hsaVersion = 2 },
            hsaTrustedBrowser = false, hsaChallengeRequired = challenge,
            webservices = new { ckdatabasews = new { url = "https://p01-ckdatabasews.icloud.com" } }
        });
        var secrets = new SecretStore(new MemorySecrets()); var reads = 0;
        HttpMessageHandler Handler() => new ScriptHandler(async request =>
        {
            JsonNode response;
            if (request.RequestUri!.AbsolutePath.EndsWith("/validate")) response = Account();
            else
            {
                check(request.RequestUri.AbsolutePath.EndsWith("/records/query"), "unremembered browser verification uses a read-only Reminders request");
                var body = J.Parse(await request.Content!.ReadAsStringAsync());
                check(body.Number("resultsLimit") == 1 && body.Property("query").Text("recordType") == "reminderList", "access check does not download the full reminder history");
                reads++; response = J.Node(new { records = Array.Empty<object>() });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        });
        using (var imported = new ICloudClient("", secrets, Handler))
        {
            var result = await imported.ImportBrowserSession("test@example.com", state, default);
            check(result.Flag("authenticated") && !result.Flag("needs_2fa"), "verified web sign-in without remembered trust connects the native app");
            check(!result.Flag("trusted_session"), "import does not falsely claim Apple granted remembered browser trust");
            check(secrets.Session("test@example.com") is not null && reads == 1, "session is saved only after Reminders access succeeds");
        }
        using (var restarted = new ICloudClient("test@example.com", secrets, Handler))
            check(await restarted.Restore(default) && reads == 2, "restart revalidates the usable session and Reminders access without a password handshake");
        using (var discovered = new ICloudClient("", secrets, Handler))
        {
            var result = await discovered.ImportBrowserSession(null, state, default);
            check(result.Flag("authenticated") && discovered.Account == "test@example.com", "direct cookie handoff learns the account from native validation without a captured browser response");
        }
        var guardedSecrets = new SecretStore(new MemorySecrets());
        using (var guarded = new ICloudClient("existing@example.com", guardedSecrets, Handler))
        {
            var guardCalled = false; var before = reads;
            try
            {
                await guarded.ImportBrowserSession(null, state, default, account =>
                {
                    guardCalled = account == "test@example.com";
                    throw CoreException.Bad("Queued edits belong to another account.");
                });
                check(false, "account guard must reject the discovered session");
            }
            catch (CoreException)
            {
                check(guardCalled && reads == before, "account guard receives Apple's validated identity before accessing reminder data");
                check(guarded.Account == "existing@example.com" && guardedSecrets.Session("test@example.com") is null,
                    "blocked cookie handoff preserves the previous account and does not persist the new session");
            }
        }
        var root = Path.Combine(Path.GetTempPath(), "reminders-browser-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var cache = new Cache(Path.Combine(root, "cache.db")))
            {
                cache.SetMeta("apple_id", "existing@example.com");
                cache.Upsert(J.Node(new { id = "queued", list_id = "list", title = "Unsynced edit" }), true);
                cache.Enqueue("queued", "update", J.Node(new { title = "Unsynced edit" }), "old");
            }
            var serviceVault = new MemorySecrets(); var before = reads;
            await using (var service = new ReminderService(root, serviceVault, Handler))
            {
                try
                {
                    await service.CallAsync("browser_login", JsonSerializer.SerializeToElement(new { state }));
                    check(false, "RPC must refuse to switch a discovered account with pending edits");
                }
                catch (CoreException error) { check(error.Code == "BAD_REQUEST" && reads == before, "RPC checks queued edits before committing the discovered account"); }
            }
            using var retained = new Cache(Path.Combine(root, "cache.db"));
            check(retained.Meta("apple_id") == "existing@example.com" && retained.Pending(1).Count == 1 && retained.Reminder("queued").Text("title") == "Unsynced edit",
                "failed direct handoff preserves the previous cache and outbox");
            check(new SecretStore(serviceVault).Session("test@example.com") is null, "RPC refuses account switching before saving browser credentials");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        foreach (var rejected in new[] { "challenge", "wrong_account", "denied", "record_error", "network" })
        {
            var targetSecrets = new SecretStore(new MemorySecrets()); var probes = 0;
            using var candidate = new ICloudClient("existing@example.com", targetSecrets, () => new ScriptHandler(request =>
            {
                var status = HttpStatusCode.OK; JsonNode response;
                if (request.RequestUri!.AbsolutePath.EndsWith("/validate"))
                    response = Account(rejected == "challenge", rejected == "wrong_account" ? "different@example.com" : "test@example.com");
                else
                {
                    probes++;
                    status = rejected == "network" ? HttpStatusCode.ServiceUnavailable : rejected == "denied" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
                    response = rejected == "record_error" ? J.Node(new { records = new[] { new { serverErrorCode = "AUTHENTICATION_REQUIRED" } } }) : new JsonObject();
                }
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response.ToJsonString()) });
            }));
            try { await candidate.ImportBrowserSession(rejected == "wrong_account" ? "test@example.com" : null, state, default); check(false, "unverified browser session is refused: " + rejected); }
            catch (CoreException error)
            {
                var expected = rejected == "challenge" ? "2FA_REQUIRED" : rejected == "network" ? "NETWORK" : "AUTH_REQUIRED";
                check(error.Code == expected, "handoff preserves the actual failure: " + rejected);
                check(candidate.Account == "existing@example.com" && !candidate.Connected && targetSecrets.Session("test@example.com") is null,
                    "failed verification does not save or switch the account: " + rejected);
                if (rejected is "challenge" or "wrong_account") check(probes == 0, "challenge and account identity are checked before Reminders access");
            }
        }
    }
}
