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
