using System.Net;
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
            try { await candidate.ImportBrowserSession("test@example.com", state, default); check(false, "unverified browser session is refused: " + rejected); }
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
