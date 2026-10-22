using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed class AppleAuth : IDisposable
{
    internal const string AuthEndpoint = "https://idmsa.apple.com/appleauth/auth";
    private const string SetupEndpoint = "https://setup.icloud.com/setup/ws/1";
    private const string Widget = "d39ba9916b7251055b22c7f910e2ea796ee65e98b2ddecea8f5dde8d9d1a815d";
    private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3.1 Safari/605.1.15";
    private readonly Func<HttpMessageHandler> handlerFactory;
    private CookieContainer cookies = new();
    internal HttpClient Http { get; private set; }
    internal event Action<string>? Diagnostic;
    internal void ReportCloudKitError(string operation, string code)
    {
        var safeCode = code.Length is > 0 and <= 64 && code.All(c => c is >= 'A' and <= 'Z' or '_') ? code : "UNKNOWN";
        Diagnostic?.Invoke($"icloud: {operation} server_error={safeCode}");
    }
    internal JsonObject State { get; private set; }
    private readonly List<JsonNode> phones = [];
    private readonly List<(string Method, long Id, string Mode)> sent = [];
    private (string Method, long Id, string Mode) route = ("unknown", 0, "sms");
    private bool optionsLoaded;
    private bool browserSessionVerified;
    private string? notice;
    public AppleAuth(JsonObject? state = null, Func<HttpMessageHandler>? handlerFactory = null)
    {
        State = state ?? new(); State["client_id"] ??= Guid.NewGuid().ToString();
        foreach (var saved in State.Array("cookies").OfType<JsonObject>())
        {
            var domain = saved.Text("domain")?.TrimStart('.');
            if (domain is null || !AppleDomain(domain)) continue;
            try
            {
                var cookie = new Cookie(saved.Required("name"), saved.Required("value", 16384), saved.Text("path") ?? "/", saved.Required("domain")) { Secure = true, HttpOnly = saved.Flag("http_only") };
                if (saved.Number("expires") > 0) cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(saved.Number("expires")).UtcDateTime;
                if (!cookie.Expired) cookies.Add(cookie);
            }
            catch (Exception ex) when (ex is CookieException or CoreException or ArgumentException) { }
        }
        this.handlerFactory = handlerFactory ?? (() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = true, CookieContainer = cookies, ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All });
        Http = CreateHttp();
    }
    internal static bool AppleDomain(string host) => host == "apple.com" || host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase) || host == "icloud.com" || host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase);
    internal void SaveCookies() => State["cookies"] = new JsonArray(cookies.GetAllCookies().Cast<Cookie>().Where(c => !c.Expired && AppleDomain(c.Domain.TrimStart('.')))
        .Select(c => J.Node(new { name = c.Name, value = c.Value, domain = c.Domain, path = c.Path, http_only = c.HttpOnly, expires = c.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds() })).ToArray());
    private HttpClient CreateHttp()
    {
        var c = new HttpClient(handlerFactory()) { Timeout = TimeSpan.FromSeconds(60) };
        var browserAgent = State.Text("user_agent");
        if (browserAgent is not { Length: > 0 and <= 2048 } || browserAgent.Contains('\r') || browserAgent.Contains('\n') || !c.DefaultRequestHeaders.UserAgent.TryParseAdd(browserAgent))
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }
    public bool RequiresTwoFactor => State.Number("hsa_version") >= 2 && (State.Flag("challenge_required") || !State.Flag("trusted_session") && !browserSessionVerified);
    public JsonNode TwoFactorStatus => J.Node(new { method = route.Method, number = phones.FirstOrDefault(p => p.Number("id") == route.Id).Text("numberWithDialCode") ?? phones.FirstOrDefault(p => p.Number("id") == route.Id).Text("obfuscatedNumber"), notice, can_sms = phones.Count > 0, sent = sent.Count > 0 });
    internal string Query => string.Join("&", new Dictionary<string, string?> { ["clientBuildNumber"] = "2534Project66", ["clientMasteringNumber"] = "2534B22", ["ckjsBuildVersion"] = "17DProjectDev77", ["clientId"] = State.Text("client_id"), ["dsid"] = State.Text("dsid") }.Where(p => p.Value is not null).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!)));
    internal HttpRequestMessage Request(HttpMethod method, string url, JsonNode? body, bool auth, string accept = "application/json, text/javascript")
    {
        var uri = new Uri(url); if (uri.Scheme != "https") throw new CoreException("ERROR", "An insecure iCloud URL was rejected");
        var request = new HttpRequestMessage(method, uri);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        void Add(string name, string? value) { if (value is not null) request.Headers.Add(name, value); }
        Add("Origin", "https://www.icloud.com"); Add("Referer", auth ? "https://idmsa.apple.com" : "https://www.icloud.com/"); Add("Accept", accept);
        if (auth)
        {
            foreach (var p in new Dictionary<string, string> { ["x-apple-oauth-client-id"] = Widget, ["x-apple-oauth-client-type"] = "firstPartyAuth", ["x-apple-oauth-redirect-uri"] = "https://www.icloud.com", ["x-apple-oauth-require-grant-code"] = "true", ["x-apple-oauth-response-mode"] = "web_message", ["x-apple-oauth-response-type"] = "code", ["x-apple-widget-key"] = Widget, ["x-apple-fd-client-info"] = J.Node(new { U = UserAgent, L = "en-US", Z = "GMT+00:00", V = "1.1", F = "" }).ToJsonString() }) Add(p.Key, p.Value);
            Add("x-apple-oauth-state", State.Text("client_id")); Add("x-apple-frame-id", State.Text("client_id")); Add("x-apple-auth-attributes", State.Text("auth_attributes"));
        }
        else { Add("x-apple-webauth-token", State.Text("session_token")); Add("x-apple-id-account-country", State.Text("account_country")); }
        Add("x-apple-id-session-id", State.Text("session_id")); Add("scnt", State.Text("scnt")); return request;
    }
    internal async Task<(int Status, string Text, bool Token)> SendAsync(HttpRequestMessage request, int maxBytes, CancellationToken token)
    {
        using (request)
        {
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var issuedToken = false;
                foreach (var (header, key) in new[] { ("x-apple-id-session-id", "session_id"), ("x-apple-session-token", "session_token"), ("scnt", "scnt"), ("x-apple-auth-attributes", "auth_attributes"), ("x-apple-id-account-country", "account_country"), ("x-apple-twosv-trust-token", "trust_token") })
                    if (response.Headers.TryGetValues(header, out var values)) { var v = values.FirstOrDefault(); if (v is not null) State[key] = v; if (key == "session_token" && !string.IsNullOrWhiteSpace(v)) issuedToken = true; }
                if (response.Content.Headers.ContentLength > maxBytes) throw new CoreException("NETWORK", "iCloud returned an unexpectedly large response");
                using var stream = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var buffer = new byte[8192];
                while (true) { var count = await stream.ReadAsync(buffer, token); if (count == 0) break; if (output.Length + count > maxBytes) throw new CoreException("NETWORK", "iCloud returned an unexpectedly large response"); output.Write(buffer, 0, count); }
                Diagnostic?.Invoke($"icloud: {request.Method} {request.RequestUri!.AbsolutePath} HTTP {(int)response.StatusCode} bytes={output.Length} elapsed_ms={started.ElapsedMilliseconds}");
                return ((int)response.StatusCode, Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length), issuedToken);
            }
            catch (HttpRequestException) { throw new CoreException("NETWORK", "Could not reach iCloud", "connection failed"); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new CoreException("NETWORK", "Could not reach iCloud", "request timed out"); }
        }
    }
    private Task<(int Status, string Text, bool Token)> Auth(HttpMethod method, string suffix, JsonNode? body, CancellationToken token, string accept = "application/json, text/javascript") => SendAsync(Request(method, AuthEndpoint + suffix, body, true, accept), 2 * 1024 * 1024, token);
    internal static string Detail(JsonNode? v)
    {
        var message = v.Text("errorMessage") ?? v.Text("reason") ?? v.Array("serviceErrors").FirstOrDefault().Text("message") ?? v.Array("serviceErrors").FirstOrDefault().Text("code") ?? "Apple returned an error without a public reason";
        return message[..Math.Min(300, message.Length)];
    }
    private static string? Message(JsonNode? v) => v.Array("service_errors").FirstOrDefault().Text("message") ?? v.Array("serviceErrors").FirstOrDefault().Text("message") ?? v.Text("errorMessage");
    internal static CoreException Classify(int status, string text)
    {
        var value = J.Parse(text); var detail = Detail(value);
        if (text.Contains("termsUpdateNeeded")) return new("TERMS_REQUIRED", "Apple requires you to accept updated iCloud terms.", detail);
        if (status == 409 || value.Text("authType")?.Contains("hsa", StringComparison.OrdinalIgnoreCase) == true || value.Flag("hsaChallengeRequired")) return new("2FA_REQUIRED", "Two-factor authentication required", detail);
        if (status is 401 or 403 or 421) return new("AUTH_REQUIRED", "iCloud rejected the sign-in", detail);
        return new("NETWORK", "iCloud sign-in failed", $"HTTP {status}: {detail}");
    }
    public async Task SignIn(string account, string password, bool acceptTerms, CancellationToken token)
    {
        browserSessionVerified = false;
        // A new password handshake must behave like the released native flow,
        // without cookies or a user agent inherited from a browser session.
        Http.Dispose(); cookies = new();
        State.Remove("cookies"); State.Remove("browser_cookie_session"); State.Remove("user_agent");
        Http = CreateHttp(); State["session_id"] = null; State["scnt"] = null; State["auth_attributes"] = null;
        var id = Uri.EscapeDataString(State.Required("client_id"));
        var bootstrap = await Auth(HttpMethod.Get, $"/authorize/signin?frame_id={id}&skVersion=7&iframeid={id}&client_id={Widget}&response_type=code&redirect_uri=https%3A%2F%2Fwww.icloud.com&response_mode=web_message&state={id}&authVersion=latest", null, token);
        if (bootstrap.Status is < 200 or >= 300) throw Classify(bootstrap.Status, bootstrap.Text);
        var privateBytes = RandomNumberGenerator.GetBytes(32); var a = Srp.Integer(privateBytes); CryptographicOperations.ZeroMemory(privateBytes);
        var init = await Auth(HttpMethod.Post, "/signin/init", J.Node(new { accountName = account, a = Convert.ToBase64String(Srp.Public(a)), protocols = new[] { "s2k", "s2k_fo" } }), token);
        if (init.Status is < 200 or >= 300) throw Classify(init.Status, init.Text);
        var challenge = JsonNode.Parse(init.Text)!; var passwordBytes = Encoding.UTF8.GetBytes(password); (byte[] M1, byte[] M2) proof;
        try { proof = Srp.Proof(account, passwordBytes, a, challenge); } finally { CryptographicOperations.ZeroMemory(passwordBytes); }
        var completeBody = new JsonObject { ["accountName"] = account, ["c"] = challenge.Text("c"), ["m1"] = Convert.ToBase64String(proof.M1), ["m2"] = Convert.ToBase64String(proof.M2), ["rememberMe"] = true, ["trustTokens"] = State.Text("trust_token") is { } trust ? new JsonArray(trust) : new JsonArray() };
        CryptographicOperations.ZeroMemory(proof.M1); CryptographicOperations.ZeroMemory(proof.M2);
        var complete = await Auth(HttpMethod.Post, "/signin/complete?isRememberMeEnabled=true", completeBody, token);
        if (complete.Status is < 200 or >= 300)
        {
            var error = Classify(complete.Status, complete.Text);
            if (error.Code == "2FA_REQUIRED")
            {
                route = ("unknown", 0, "sms"); phones.Clear(); sent.Clear(); optionsLoaded = false; NoteOptions(J.Parse(complete.Text)); notice = Message(J.Parse(complete.Text));
                try { await LoadOptions(token); } catch (CoreException) { }
                try { await AccountLogin(token); } catch (CoreException) { }
            }
            throw error;
        }
        try { await AccountLogin(token); }
        catch (CoreException ex) when (ex.Code == "TERMS_REQUIRED" && acceptTerms)
        {
            var accepted = await SendAsync(Request(HttpMethod.Post, SetupEndpoint + "/acceptTermsOfService?" + Query, new JsonObject(), false), 2 * 1024 * 1024, token);
            if (accepted.Status is < 200 or >= 300) throw new CoreException("TERMS_REQUIRED", "Apple did not accept the updated iCloud terms.");
            await AccountLogin(token);
        }
    }
    public async Task<bool> Resume(CancellationToken token)
    {
        if (State.Flag("browser_cookie_session") && State.Array("cookies").Count > 0)
        {
            browserSessionVerified = false;
            try
            {
                var request = Request(HttpMethod.Post, SetupEndpoint + "/validate?" + Query, null, false);
                request.Content = new StringContent("null", Encoding.UTF8, "application/json");
                var response = await SendAsync(request, 2 * 1024 * 1024, token);
                if (response.Status is < 200 or >= 300) throw Classify(response.Status, response.Text);
                NoteAccount(JsonNode.Parse(response.Text)!);
                browserSessionVerified = !State.Flag("challenge_required") &&
                    !string.IsNullOrWhiteSpace(State.Text("apple_id")) && !string.IsNullOrWhiteSpace(State.Text("dsid"));
                return browserSessionVerified;
            }
            catch (CoreException ex) when (ex.Code is "AUTH_REQUIRED" or "2FA_REQUIRED") { }
        }
        if (State.Text("session_token") is null) return false;
        try { await AccountLogin(token); return !RequiresTwoFactor; } catch (CoreException ex) when (ex.Code is "AUTH_REQUIRED" or "2FA_REQUIRED") { return false; }
    }
    private async Task AccountLogin(CancellationToken token)
    {
        var webToken = State.Text("session_token") ?? throw new CoreException("AUTH_REQUIRED", "Apple did not issue a web authentication token");
        var body = new JsonObject { ["accountCountryCode"] = State.Text("account_country") ?? "USA", ["dsWebAuthToken"] = webToken, ["extended_login"] = true };
        if (State.Text("trust_token") is { } trust) body["trustToken"] = trust;
        var response = await SendAsync(Request(HttpMethod.Post, SetupEndpoint + "/accountLogin?" + Query, body, false), 2 * 1024 * 1024, token);
        if (response.Status is < 200 or >= 300) throw Classify(response.Status, response.Text);
        NoteAccount(JsonNode.Parse(response.Text)!);
    }
    private void NoteAccount(JsonNode v)
    {
        State["apple_id"] = v["dsInfo"].Text("appleId") ?? v["dsInfo"].Text("primaryEmail");
        State["dsid"] = v["dsInfo"]?["dsid"]?.ToString(); State["hsa_version"] = v["dsInfo"].Number("hsaVersion"); State["challenge_required"] = v.Flag("hsaChallengeRequired"); State["trusted_session"] = v.Flag("hsaTrustedBrowser"); State["webservices"] = v["webservices"]?.DeepClone() ?? new JsonObject();
        if (v.Flag("termsUpdateNeeded") || v["dsInfo"].Flag("termsUpdateNeeded")) throw new CoreException("TERMS_REQUIRED", "Apple requires you to accept updated iCloud terms.");
    }
    internal void NoteOptions(JsonNode? value)
    {
        if (value is null) return;
        var singular = value["trustedPhoneNumber"] ?? value["phoneNumberVerification"]?["trustedPhoneNumber"] ?? value["phoneNumber"];
        var listed = value["phoneNumberVerification"]?["trustedPhoneNumbers"] as JsonArray ?? value.Array("trustedPhoneNumbers");
        // Keep only the id, routing mode and Apple's masked rendering; full
        // phone details do not belong in the app's challenge state.
        JsonNode Sanitize(JsonNode phone) => new JsonObject { ["id"] = phone.Number("id"), ["pushMode"] = phone.Text("pushMode") ?? "sms", ["nonFTEU"] = phone["nonFTEU"]?.DeepClone(), ["numberWithDialCode"] = phone.Text("numberWithDialCode") ?? phone.Text("obfuscatedNumber") ?? "" };
        var ordered = new List<JsonNode>();
        if (singular is JsonObject && singular["id"] is not null) ordered.Add(Sanitize(singular));
        foreach (var phone in listed.Concat(phones)) if (phone is JsonObject && phone["id"] is not null && !ordered.Any(p => p.Number("id") == phone.Number("id"))) ordered.Add(Sanitize(phone));
        if (ordered.Count > 0) { phones.Clear(); phones.AddRange(ordered); }
    }
    private async Task LoadOptions(CancellationToken token)
    {
        var response = await Auth(HttpMethod.Get, "", null, token, "text/html"); optionsLoaded = true;
        var v = J.Parse(response.Text);
        if (v is null) { response = await Auth(HttpMethod.Get, "", null, token, "application/json"); v = J.Parse(response.Text); }
        NoteOptions(v);
    }
    private static JsonObject PhonePayload(JsonNode phone)
    {
        var payload = new JsonObject { ["id"] = phone.Number("id") }; if (phone["nonFTEU"] is not null) payload["nonFTEU"] = phone["nonFTEU"]!.DeepClone(); return payload;
    }
    public async Task<JsonNode> RequestCode(bool sms, CancellationToken token)
    {
        if (!optionsLoaded) await LoadOptions(token);
        (int Status, string Text, bool Token) result;
        if (phones.Count > 0)
        {
            // Verification must echo the mode we request, rather than the
            // phone's preferred delivery mode from Apple's account metadata.
            var phone = phones[0]; route = ("sms", phone.Number("id"), "sms");
            result = await Auth(HttpMethod.Put, "/verify/phone", new JsonObject { ["phoneNumber"] = PhonePayload(phone), ["mode"] = "sms" }, token, "application/json");
        }
        else
        {
            if (sms) throw new CoreException("AUTH_REQUIRED", "Apple has no trusted phone number for this account. Add one at appleid.apple.com.");
            route = ("trusted_device", 0, "sms"); result = await Auth(HttpMethod.Get, "/verify/trusteddevice", null, token, "application/json");
        }
        notice = Message(J.Parse(result.Text)); NoteOptions(J.Parse(result.Text));
        if (result.Status is 401 or 403 or 421) throw new CoreException("AUTH_REQUIRED", "That sign-in attempt has expired. Enter your password again.", Detail(J.Parse(result.Text)));
        if (!sent.Contains(route)) sent.Add(route);
        return TwoFactorStatus;
    }
    internal static bool CodeAccepted(int status, JsonNode? value, bool issuedToken) => !value.Flag("hasError") && value.Array("service_errors").Count == 0 && value.Array("serviceErrors").Count == 0 && value.Property("securityCode").Property("valid")?.ToString() != "false" && (status is >= 200 and < 300 || status == 409 && (issuedToken || value.Property("securityCode").Flag("valid")));
    public async Task SubmitCode(string code, CancellationToken token)
    {
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) throw CoreException.Bad("The verification code must contain six digits");
        var routes = new[] { route.Method == "unknown" ? ("trusted_device", 0L, "sms") : route }.Concat(sent).Distinct().ToArray();
        for (var i = 0; i < routes.Length; i++)
        {
            var r = routes[i]; var isSms = r.Item1 == "sms";
            var body = new JsonObject { ["securityCode"] = new JsonObject { ["code"] = code } };
            if (isSms) { body["phoneNumber"] = PhonePayload(phones.FirstOrDefault(p => p.Number("id") == r.Item2) ?? new JsonObject { ["id"] = r.Item2 }); body["mode"] = r.Item3; }
            var response = await Auth(HttpMethod.Post, isSms ? "/verify/phone/securitycode" : "/verify/trusteddevice/securitycode", body, token, isSms ? "application/json, plain/text" : "application/json"); var value = J.Parse(response.Text);
            var accepted = CodeAccepted(response.Status, value, response.Token);
            Diagnostic?.Invoke($"icloud verification: method={r.Item1} HTTP {response.Status} code_accepted={accepted} token_issued={response.Token}");
            if (accepted) break;
            NoteOptions(value);
            if (!isSms && response.Status == 409) throw new CoreException("2FA_REQUIRED", "Apple won't verify a device code from this app. Choose Text me a code instead.", Detail(value));
            if (i == routes.Length - 1)
            {
                var error = value.Array("service_errors").FirstOrDefault() ?? value.Array("serviceErrors").FirstOrDefault();
                var wrong = error?["code"]?.ToString() == "-21669";
                throw new CoreException("2FA_REQUIRED", wrong ? "Incorrect verification code. Check the six digits, or ask for a new code." : (Message(value) ?? $"Apple refused that code without saying why (HTTP {response.Status}).") + " Use the code from the most recent message.", Detail(value));
            }
        }
        var trust = await Auth(HttpMethod.Get, "/2sv/trust", null, token);
        if (trust.Status is < 200 or >= 300) throw new CoreException("2FA_REQUIRED", "Apple accepted the code but did not establish session trust. Please sign in again.");
        try { await AccountLogin(token); }
        catch (CoreException error) when (error.Code == "NETWORK")
        {
            // The digits and trust step succeeded. A setup-service failure is
            // a separate stage, and resending codes will not resolve it.
            var status = error.Detail.Split(':', 2)[0];
            var suffix = status.StartsWith("HTTP ", StringComparison.Ordinal) ? $" ({status})" : "";
            throw new CoreException(error.Code, $"Apple accepted the verification code, but iCloud account sign-in failed{suffix}. Try again, or sign in at iCloud.com to check your account.", error.Detail);
        }
        if (!State.Flag("trusted_session") || RequiresTwoFactor) throw new CoreException("2FA_REQUIRED", "Apple has not finished verifying this session. Please try signing in again.");
        route = ("unknown", 0, "sms"); sent.Clear(); notice = null;
    }
    public void Dispose() => Http.Dispose();
}
