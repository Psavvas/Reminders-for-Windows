using System.Net;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class AuthVerificationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var saved = J.Node(new { session_token = "saved", trust_token = "trusted", client_id = "stable-client", cookies = new[]
        {
            new { name = "X-APPLE-WEBAUTH-TRUST", value = "cookie", domain = ".icloud.com", path = "/", expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds() },
            new { name = "ignored", value = "foreign", domain = ".example.com", path = "/", expires = 0L }
        } }).AsObject();
        using (var cookieAuth = new AppleAuth((JsonObject)saved.DeepClone()))
        {
            cookieAuth.SaveCookies();
            check(cookieAuth.State.Array("cookies").Count == 1, "only Apple authentication cookies persist");
            check(cookieAuth.State.Array("cookies")[0].Text("value") == "cookie", "trust cookie survives client reconstruction");
        }
        var vault = new MemorySecrets(); var secrets = new SecretStore(vault); secrets.Session("test@example.com", saved.ToJsonString());
        var restores = 0;
        HttpMessageHandler Handler() => new ScriptHandler(async request =>
        {
            check(request.RequestUri!.AbsolutePath.EndsWith("/accountLogin"), "restored session avoids a fresh password or 2FA request");
            var body = J.Parse(await request.Content!.ReadAsStringAsync());
            check(body.Text("trustToken") == "trusted" && body.Flag("extended_login"), "restoration supplies remembered trust and extended login");
            restores++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(J.Node(new { dsInfo = new { dsid = "1", appleId = "test@example.com", hsaVersion = 2 }, hsaTrustedBrowser = true }).ToJsonString()) };
            response.Headers.Add("X-Apple-Session-Token", "rotated"); return response;
        });
        using (var firstLaunch = new ICloudClient("test@example.com", secrets, Handler)) check(await firstLaunch.Restore(default), "first launch restores trusted session");
        using (var secondLaunch = new ICloudClient("test@example.com", secrets, Handler))
        {
            check(await secondLaunch.Restore(default), "second launch restores without asking for 2FA");
            check(J.Parse(secrets.Session("test@example.com")).Text("session_token") == "rotated", "rotated session token is saved for the next launch");
            check(await secondLaunch.ImportBrowserSession("test@example.com", saved, default) is not null, "Apple-hosted sign-in imports a verified trusted session");
            try { await secondLaunch.ImportBrowserSession("wrong@example.com", saved, default); check(false, "wrong browser account is rejected"); }
            catch (CoreException e) { check(e.Code == "AUTH_REQUIRED" && secondLaunch.Account == "test@example.com", "account mismatch retains the previous authenticated account"); }
        }
        check(restores == 4, "each imported or resumed session is verified with Apple");
        var browserState = (JsonObject)saved.DeepClone(); browserState.Remove("session_token");
        browserState["browser_cookie_session"] = true;
        browserState["user_agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";
        var cookieRestores = 0;
        HttpMessageHandler CookieHandler() => new ScriptHandler(async request =>
        {
            check(request.RequestUri!.AbsolutePath.EndsWith("/validate") && await request.Content!.ReadAsStringAsync() == "null", "browser cookies validate without requiring an accountLogin POST body");
            check(request.Headers.UserAgent.ToString() == browserState.Text("user_agent"), "browser session preserves its user agent during transfer and restore");
            cookieRestores++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(J.Node(new { dsInfo = new { dsid = "1", appleId = "cookie@example.com", hsaVersion = 2 }, hsaTrustedBrowser = true }).ToJsonString()) };
        });
        using (var browserImport = new ICloudClient("", secrets, CookieHandler))
        {
            check((await browserImport.ImportBrowserSession("cookie@example.com", browserState, default)).Flag("authenticated"), "completed browser sign-in can import a cookie session with no exposed web token");
            check(J.Parse(secrets.Session("cookie@example.com")).Flag("browser_cookie_session"), "browser-cookie restoration mode is persisted");
        }
        using (var browserRestart = new ICloudClient("cookie@example.com", secrets, CookieHandler))
            check(await browserRestart.Restore(default), "browser cookies restore after restarting without password or another verification handshake");
        check(cookieRestores == 2, "cookie session is checked with Apple during both import and restart");
        using (var offlineBrowser = new ICloudClient("cookie@example.com", secrets, () => new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") })))
            check(!await offlineBrowser.Restore(default) && offlineBrowser.RestoreRetryable, "temporary network failure retries a browser-cookie session without asking for a new sign-in");
        var challenged = (JsonObject)browserState.DeepClone();
        using (var challengeImport = new ICloudClient("existing@example.com", secrets, () => new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(J.Node(new { dsInfo = new { dsid = "1", appleId = "cookie@example.com", hsaVersion = 2 }, hsaTrustedBrowser = true, hsaChallengeRequired = true }).ToJsonString()) })))
        {
            try { await challengeImport.ImportBrowserSession("cookie@example.com", challenged, default); check(false, "challenged cookie session is rejected"); }
            catch (CoreException error) { check(error.Code == "2FA_REQUIRED" && challengeImport.Account == "existing@example.com", "cookie import cannot bypass unfinished verification"); }
        }
        var importDiagnostics = new List<string>();
        using (var failedImport = new ICloudClient("existing@example.com", secrets, () => new ResponseHandler(() =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") })))
        {
            failedImport.Diagnostic += importDiagnostics.Add;
            try { await failedImport.ImportBrowserSession("test@example.com", saved, default); check(false, "failed import is rejected"); }
            catch (CoreException error) { check(error.Code == "NETWORK" && failedImport.Account == "existing@example.com", "failed import preserves the previous account"); }
            check(importDiagnostics.Any(line => line.Contains("HTTP 503")), "failed browser-session verification is included in diagnostics");
            check(!importDiagnostics.Any(line => line.Contains("saved") || line.Contains("test@example.com")), "failed import diagnostics omit tokens and account identifiers");
        }
        using (var cancelled = new CancellationTokenSource())
        using (var cancelledImport = new ICloudClient("existing@example.com", secrets, () => new ScriptHandler(_ =>
        {
            cancelled.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(J.Node(new { dsInfo = new { dsid = "1", appleId = "new@example.com", hsaVersion = 2 }, hsaTrustedBrowser = true }).ToJsonString()) });
        })))
        {
            try { await cancelledImport.ImportBrowserSession("new@example.com", saved, cancelled.Token); check(false, "cancelled import is rejected"); }
            catch (OperationCanceledException) { check(cancelledImport.Account == "existing@example.com" && secrets.Session("new@example.com") is null, "cancelling browser sign-in does not save or switch accounts"); }
        }
        foreach (var accountStatus in new[] { 200, 500 })
        {
            var verified = false;
            var diagnostics = new List<string>();
            using var auth = new AppleAuth(J.Node(new { session_token = "mock-token", hsa_version = 2 }).AsObject(),
                () => new ScriptHandler(async request =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    var body = request.Content is null ? null : J.Parse(await request.Content.ReadAsStringAsync());
                    var status = 200;
                    JsonNode response = new JsonObject();
                    if (path.EndsWith("/verify/phone"))
                    {
                        check(body.Text("mode") == "sms", "delivery explicitly requests SMS");
                        status = 409;
                    }
                    else if (path.EndsWith("/verify/phone/securitycode"))
                    {
                        check(body.Text("mode") == "sms", "verification uses the requested SMS mode despite phone metadata");
                        verified = true;
                        status = 409;
                        response = J.Node(new { securityCode = new { valid = true } });
                    }
                    else if (path.EndsWith("/accountLogin"))
                    {
                        check(verified, "account login follows accepted verification");
                        status = accountStatus;
                        response = J.Node(new { dsInfo = new { dsid = "123", hsaVersion = 2 }, hsaTrustedBrowser = true });
                    }
                    return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(response.ToJsonString()) };
                }));
            auth.Diagnostic += diagnostics.Add;
            // The app chooses SMS even when Apple's preferred phone mode differs.
            auth.NoteOptions(J.Node(new { trustedPhoneNumber = new { id = 7, pushMode = "voice", numberWithDialCode = "••123" } }));
            await auth.RequestCode(true, default);
            try
            {
                await auth.SubmitCode("123456", default);
                check(accountStatus == 200 && !auth.RequiresTwoFactor, "SMS finishes account sign-in");
            }
            catch (CoreException error)
            {
                check(accountStatus == 500 && error.Code == "NETWORK", "account setup failure remains a network error");
                check(error.Message.Contains("accepted the verification code") && error.Message.Contains("HTTP 500"),
                    "account setup failure distinguishes accepted code from failed account sign-in");
                check(auth.RequiresTwoFactor, "account setup failure does not authenticate the session");
            }
            check(diagnostics.Any(message => message.Contains("code_accepted=True")), "diagnostics record verification outcome");
            check(!diagnostics.Any(message => message.Contains("123456") || message.Contains("mock-token")), "diagnostics exclude codes and tokens");
        }
    }
}
