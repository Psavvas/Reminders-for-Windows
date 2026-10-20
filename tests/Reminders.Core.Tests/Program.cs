using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
void Equal<T>(T expected, T actual, string name) => Check(Equals(expected, actual), $"{name}: expected {expected}, got {actual}");
async Task Error(Func<Task> action, string code, string name) { try { await action(); throw new Exception(name + ": expected error"); } catch (CoreException e) { Equal(code, e.Code, name); } }
string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
var salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
var serverSeed = SHA256.HashData(Encoding.UTF8.GetBytes("server-public-seed"));
var serverPublic = Enumerable.Range(0, 8).SelectMany(_ => serverSeed).ToArray();
var privateValue = Srp.Integer(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
foreach (var (protocol, m1, m2) in new[] { ("s2k", "65d959dcd99bb14ebe4cdf8d57fee4fc15fd3375df8f06f8a7fd73ea83bc933f", "02927bdb75a3a2322e789cc4d0c7e90d3b8c08c449ccf9d380e9dcc19ea6fb92"), ("s2k_fo", "082a47581fdec08156bc4135d3d8d171920c3126cf307f966d459ea5043bafdf", "add0f31339cc14e4618d8d47ee1032c23897122b475b600046d96aaf7cf45579") })
{
    var challenge = J.Node(new { protocol, iteration = 1000, salt = Convert.ToBase64String(salt), b = Convert.ToBase64String(serverPublic) });
    var proof = Srp.Proof("vector@example.com", Encoding.UTF8.GetBytes("correct horse battery staple"), privateValue, challenge);
    Equal(m1, Hex(proof.M1), protocol + " M1 reference"); Equal(m2, Hex(proof.M2), protocol + " M2 reference");
    foreach (var bad in new JsonObject[] { new() { ["protocol"] = "unknown" }, new() { ["iteration"] = 0 }, new() { ["b"] = Convert.ToBase64String([0]) }, new() { ["salt"] = "" } })
    {
        var c = challenge.DeepClone(); foreach (var p in bad) c[p.Key] = p.Value?.DeepClone();
        await Error(() => Task.Run(() => Srp.Proof("u", [1], privateValue, c)), "AUTH_REQUIRED", "invalid SRP parameters");
    }
}
Equal("6b7cc6edb94620dcf9811c616742ca428fe81b5bede8478a876a895345ded185", Hex(Srp.Derive(Encoding.UTF8.GetBytes("correct horse battery staple"), Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), 1000, "s2k")), "PBKDF reference");
foreach (var text in new[] { "", "Hello", "café • 日本語 🛫", new string('x', 100000) }) Equal(text, AppleDocument.Decode(AppleDocument.Encode(text)), "Apple document round trip");
Equal<string?>(null, AppleDocument.Decode("not base64!"), "malformed document");
// Saved fixture from the previous connector, independent of this encoder.
Equal("Hello", AppleDocument.Decode("EgkaBxIFSGVsbG8="), "uncompressed protobuf fixture");
var ny = TimeUtil.Zone("America/New_York");
Equal(TimeUtil.Parse("2026-11-01T05:30:00Z"), TimeUtil.Anchor(new DateTime(2026, 11, 1, 1, 30, 0), ny), "DST overlap chooses first instant");
Equal(TimeUtil.Parse("2026-03-08T07:00:00Z"), TimeUtil.Anchor(new DateTime(2026, 3, 8, 2, 30, 0), ny), "DST gap advances");
var instant = TimeUtil.Parse("2026-07-10T00:00:00Z"); Equal(instant, TimeUtil.FromFloating(TimeUtil.ToFloating(instant, "America/New_York"), "America/New_York"), "floating date round trip");
var vault = new MemorySecrets(); var secrets = new SecretStore(vault); var session = new string('a', 999) + "🛫" + new string('b', 2300);
secrets.Session("test", session); Equal(session, secrets.Session("test"), "chunked session");
Check(SecretStore.Chunks(session).All(c => c.Length <= 1000 && !char.IsHighSurrogate(c[^1])), "chunks preserve surrogate pairs");
vault.FailWriteAfter = vault.Writes + 1;
try { secrets.Session("test", new string('x', 3100)); } catch (IOException) { }
Equal(session, secrets.Session("test"), "interrupted save retains prior manifest"); vault.FailWriteAfter = int.MaxValue;
secrets.Delete("test"); Equal<string?>(null, secrets.Session("test"), "sign out clears session");
var root = Path.Combine(Path.GetTempPath(), "reminders-core-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    using (var cache = new Cache(Path.Combine(root, "cache-test.db")))
    {
        cache.SetMeta("cursor", "token"); cache.SetMeta("cursor", null); Equal<string?>(null, cache.Meta("cursor"), "meta clears");
        cache.ReplaceLists(new JsonArray(J.Node(new { id = "list", title = "Test", is_group = false })));
        Check(cache.Lists[0]!["is_group"]!.GetValue<bool>() == false, "cached list boolean");
        var r = J.Node(new { id = "r", list_id = "list", title = "Original", description = "", created = TimeUtil.Now }); cache.Upsert(r, true);
        cache.Enqueue("r", "update", J.Node(new { completed = true }), "old"); var first = cache.Pending()[0].Number("seq");
        cache.Edit("r", new() { ["title"] = "Newer" }); cache.Enqueue("r", "update", J.Node(new { title = "Newer" }), "old");
        cache.Acknowledge(first, "r", "new"); Equal("new", cache.Pending()[0].Text("base_tag"), "queued edits rebased");
        cache.Upsert(J.Node(new { id = "r", list_id = "list", title = "Old response" })); Equal("Newer", cache.Reminder("r").Text("title"), "upload cannot overwrite newer edit");
        Equal(1L, cache.Reminder("r").Number("dirty"), "new edit stays dirty"); cache.Acknowledge(cache.Pending()[0].Number("seq"), "r", "final"); Equal(0L, cache.Reminder("r").Number("dirty"), "last upload clears dirty");
        try { cache.Transaction<bool>(() => { cache.Edit("r", new() { ["title"] = "Failed" }); throw new IOException(); }); } catch (IOException) { }
        Equal("Newer", cache.Reminder("r").Text("title"), "edit rollback");
        Equal(1, cache.Reminders(J.Node(new { scope = "all", search = (string?)null })).Count, "null search");
        Equal(0, cache.Reminders(J.Node(new { search = "unmatched" })).Count, "search filters");
        await Error(() => Task.Run(() => cache.Reminders(J.Node(new { search = 42 }))), "BAD_REQUEST", "reject invalid search");
        for (var i = 0; i < 55; i++) cache.Upsert(J.Node(new { id = "c" + i, list_id = "list", title = "done", completed = true, completed_date = TimeUtil.Now }));
        Equal(50, cache.Reminders(J.Node(new { scope = "completed", limit = 1000 })).Count, "completed cap");
    }
    await using (var service = new ReminderService(root, new MemorySecrets()))
    {
        Task<JsonElement> Call(string method, object p) => service.CallAsync(method, JsonSerializer.SerializeToElement(p));
        await service.StartAsync(); Check((await Call("ping", new { })).GetProperty("pong").GetBoolean(), "in-process ping");
        var created = await Call("create_reminder", new { list_id = "list", title = "Offline 🛫" }); var id = created.GetProperty("id").GetString()!;
        await Call("update_reminder", new { id, title = "Updated" });
        Equal("Updated", (await Call("reminder", new { id })).GetProperty("title").GetString(), "offline CRUD");
        await Call("delete_reminder", new { id }); Check((await Call("reminder", new { id })).GetProperty("deleted").GetBoolean(), "offline delete");
        await Call("restore_reminder", new { id }); Check(!(await Call("reminder", new { id })).GetProperty("deleted").GetBoolean(), "offline restore");
        await Error(() => Call("unknown", new { }), "NO_METHOD", "unknown method"); await Error(() => Call("create_reminder", new { title = "no list" }), "BAD_REQUEST", "required list");
        await Error(() => Call("create_reminder", new { list_id = "l", title = new string('x', 4097) }), "BAD_REQUEST", "title cap");
        await Call("create_reminder", new { list_id = "list", title = "Due", due_date = "2026-01-01T00:00:00Z" });
        Equal(1, (await Call("due_notifications", new { now = "2026-01-01T00:30:00Z" })).GetProperty("toasts").GetArrayLength(), "due notification");
        Equal(0, (await Call("due_notifications", new { now = "2026-01-01T00:30:00Z" })).GetProperty("toasts").GetArrayLength(), "notification deduplication");
    }
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
using (var auth = new AppleAuth(J.Node(new { session_id = "session", session_token = "secret", scnt = "scnt", account_country = "USA" }).AsObject()))
{
    using var request = auth.Request(HttpMethod.Post, AppleAuth.AuthEndpoint, new JsonObject(), true);
    Check(!request.Headers.Contains("x-apple-webauth-token") && !request.Headers.Contains("x-apple-id-account-country"), "auth does not receive setup credentials");
    Equal("https://idmsa.apple.com/", request.Headers.Referrer!.ToString(), "auth referer");
    Check(request.Headers.Contains("scnt") && request.Headers.Contains("x-apple-id-session-id"), "challenge headers");
    auth.NoteOptions(J.Node(new { trustedPhoneNumber = new { id = 2, numberWithDialCode = "••123" }, trustedPhoneNumbers = new[] { new { id = 1 } } }));
    Check(auth.TwoFactorStatus.Flag("can_sms"), "phone options retained");
}
Check(!AppleAuth.CodeAccepted(409, new JsonObject(), false), "bare 409 not accepted");
Check(AppleAuth.CodeAccepted(409, J.Node(new { securityCode = new { valid = true } }), false), "verified 409 may continue to trust");
Check(!AppleAuth.CodeAccepted(200, J.Node(new { securityCode = new { valid = false } }), true), "explicit invalid code wins");
// HTTP mock verifies response rotation and bounded body reading without accounts.
using (var auth = new AppleAuth(handlerFactory: () => new ResponseHandler(() => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }; r.Headers.Add("scnt", "rotated"); return r; })))
{
    await auth.SendAsync(auth.Request(HttpMethod.Get, AppleAuth.AuthEndpoint, null, true), 100, default); Equal("rotated", auth.State.Text("scnt"), "rotated challenge captured");
}
using (var auth = new AppleAuth(handlerFactory: () => new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 101)) })))
    await Error(() => auth.SendAsync(auth.Request(HttpMethod.Get, AppleAuth.AuthEndpoint, null, true), 100, default), "NETWORK", "response cap");
var patchFields = ICloudClient.UpdateFields(new JsonObject(), new() { ["completed"] = true, ["title"] = "🛫" });
Check(CloudKit.Field(new JsonObject { ["fields"] = patchFields }, "CompletionDate") is not null, "completion timestamp");
Check(patchFields.Text("ResolutionTokenMap") is null && patchFields["ResolutionTokenMap"] is JsonObject, "merge metadata wrapper");
var verificationSent = false; var trusted = false; var handshakeCount = 0; var loginVault = new MemorySecrets();
using (var client = new ICloudClient("vector@example.com", new SecretStore(loginVault), () => new ScriptHandler(async request =>
{
    var path = request.RequestUri!.AbsolutePath;
    var body = request.Content is null ? null : J.Parse(await request.Content.ReadAsStringAsync());
    JsonNode response = new JsonObject(); var status = 200;
    if (path.EndsWith("/signin/init"))
    {
        handshakeCount++; Equal("vector@example.com", body.Text("accountName"), "Apple accountName casing");
        Check(body?["a"] is not null && body.Array("protocols").Count == 2, "SRP init payload");
        response = J.Node(new { protocol = "s2k", iteration = 1000, salt = Convert.ToBase64String(salt), b = Convert.ToBase64String(serverPublic), c = "challenge" });
    }
    else if (path.EndsWith("/signin/complete"))
    {
        Check(body?["m1"] is not null && body?["m2"] is not null && body.Flag("rememberMe"), "SRP complete payload"); status = 409;
        response = J.Node(new { trustedPhoneNumber = new { id = 7, pushMode = "sms", nonFTEU = true, numberWithDialCode = "••123" } });
    }
    else if (path.EndsWith("/accountLogin"))
    {
        Equal("web-token", body.Text("dsWebAuthToken"), "setup token payload");
        response = J.Node(new { dsInfo = new { dsid = "123", hsaVersion = 2 }, hsaChallengeRequired = !trusted, hsaTrustedBrowser = trusted, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } });
    }
    else if (path.EndsWith("/verify/phone"))
    {
        verificationSent = true; Equal("sms", body.Text("mode"), "SMS delivery mode"); Equal(7L, body?["phoneNumber"].Number("id"), "SMS phone id"); Check(body?["phoneNumber"].Flag("nonFTEU") == true, "nonFTEU echoed"); status = 409;
    }
    else if (path.EndsWith("/verify/phone/securitycode"))
    {
        Check(verificationSent, "code was requested first"); Equal("123456", body?["securityCode"].Text("code"), "SMS verification payload");
        Check(!request.Headers.Contains("x-apple-webauth-token"), "verifier excludes webauth token"); status = 409; response = J.Node(new { securityCode = new { valid = true } });
    }
    else if (path.EndsWith("/2sv/trust")) trusted = true;
    var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(response.ToJsonString()) }; result.Headers.Add("scnt", "rotated"); result.Headers.Add("x-apple-session-token", "web-token"); return result;
})))
{
    await Error(() => client.Connect("correct horse battery staple", true, false, default), "2FA_REQUIRED", "mock SRP challenge");
    Check(client.PendingTwoFactor && !client.Connected, "pending code state");
    Check(!await client.Restore(default), "restore preserves outstanding challenge"); Equal(1, handshakeCount, "restore did not reissue challenge");
    var delivery = await client.RequestCode(true, default); Check(delivery.Flag("sent") && delivery.Text("method") == "sms", "409 delivery accepted");
    Check((await client.SubmitCode("123456", default)).Flag("authenticated"), "SMS trust/accountLogin completes");
    Check(new SecretStore(loginVault).Session("vector@example.com") is not null, "session persisted in vault");
    client.Invalidate(); Check(await client.Restore(default), "trusted token restore"); Equal(1, handshakeCount, "token restore skips SRP");
    client.SignOut(); Check(new SecretStore(loginVault).Password("vector@example.com") is null, "sign out removes remembered password");
}
// A server that repeats its continuation token must fail instead of spinning.
using (var auth = new AppleAuth(J.Node(new { webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }).AsObject(), () => new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"records\":[],\"continuationMarker\":\"same\"}") })))
    await Error(() => new CloudKit(auth).Query("Hashtag", new(), default), "ERROR", "pagination repeated marker");
using (var auth = new AppleAuth(J.Node(new { webservices = new { ckdatabasews = new { url = "https://evil.example" } } }).AsObject()))
    await Error(() => new CloudKit(auth).Query("Hashtag", new(), default), "ERROR", "unsafe service endpoint");
using (var auth = new AppleAuth(J.Node(new { webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } }).AsObject(), () => new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"reason\":\"record type is not queryable\"}") })))
{
    try { await new CloudKit(auth).Query("Hashtag", new(), default); throw new Exception("expected query rejection"); }
    catch (CoreException ex) { Equal("ERROR", ex.Code, "HTTP 400 is a request rejection"); Check(ex.Message.Contains("HTTP 400") && ex.Detail.Contains("not queryable"), "request rejection exposes useful reason"); }
}
var syncRoot = Path.Combine(Path.GetTempPath(), "reminders-sync-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(syncRoot);
try
{
    var syncVault = new MemorySecrets(); var syncSecrets = new SecretStore(syncVault);
    syncSecrets.Session("test@example.com", J.Node(new { session_token = "saved", client_id = "test" }).ToJsonString());
    JsonObject? serverRecord = null; var changeTag = 0; var events = new List<string>(); var tagReads = 0; var changedTags = false;
    using var cache = new Cache(Path.Combine(syncRoot, "cache.db"));
    using var client = new ICloudClient("test@example.com", syncSecrets, () => new ScriptHandler(async request =>
    {
        var body = request.Content is null ? new JsonObject() : J.Parse(await request.Content.ReadAsStringAsync())!;
        var path = request.RequestUri!.AbsolutePath; JsonNode response;
        if (path.EndsWith("accountLogin")) response = J.Node(new { dsInfo = new { dsid = "123", hsaVersion = 2 }, hsaChallengeRequired = false, hsaTrustedBrowser = true, webservices = new { ckdatabasews = new { url = "https://p01.icloud.com" } } });
        else if (path.EndsWith("records/modify"))
        {
            var operation = body.Array("operations")[0]!; var record = operation["record"]!.AsObject();
            if (operation.Text("operationType") == "create") serverRecord = (JsonObject)record.DeepClone();
            else foreach (var field in record["fields"]!.AsObject()) serverRecord!["fields"]![field.Key] = field.Value?.DeepClone();
            serverRecord!["recordChangeTag"] = (++changeTag).ToString();
            // A partial update response must not blank the untouched list/title.
            response = new JsonObject { ["records"] = new JsonArray(operation.Text("operationType") == "create" ? serverRecord.DeepClone() : new JsonObject { ["recordName"] = serverRecord.Text("recordName"), ["recordChangeTag"] = serverRecord.Text("recordChangeTag"), ["fields"] = record["fields"]!.DeepClone() }) };
        }
        else if (path.EndsWith("records/lookup")) response = new JsonObject { ["records"] = new JsonArray(serverRecord!.DeepClone()) };
        else if (path.EndsWith("records/query") && body.Number("resultsLimit") == 1) response = J.Node(new { records = Array.Empty<object>(), syncToken = "cursor" });
        else if (path.EndsWith("records/query") && body["query"].Text("recordType") == "Hashtag")
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"reason\":\"Hashtag is not queryable\"}") };
        else if (path.EndsWith("records/query")) response = new JsonObject { ["records"] = new JsonArray(serverRecord!.DeepClone()) };
        else if (path.EndsWith("changes/zone"))
        {
            var types = body.Array("zones")[0]!.Array("desiredRecordTypes");
            JsonObject Tag(string tagId, string name) => new() { ["recordName"] = tagId, ["recordType"] = "Hashtag", ["fields"] = new JsonObject { ["Name"] = CloudKit.String(name), ["Reminder"] = CloudKit.Reference(serverRecord!.Required("recordName")) } };
            JsonArray records;
            if (types.Count == 1 && types[0]!.GetValue<string>() == "Hashtag")
            {
                tagReads++; records = new JsonArray(Tag("Hashtag/1", "old"), Tag("Hashtag/1", changedTags ? "engineering" : "school"), Tag("Hashtag/removed", "removed"), new JsonObject { ["recordName"] = "Hashtag/removed", ["deleted"] = true }, new JsonObject { ["recordName"] = "Hashtag/tombstone", ["recordType"] = "Hashtag", ["reason"] = "deleted" });
            }
            else if (types.Count == 1) records = new JsonArray(new JsonObject { ["recordName"] = "List/1", ["recordType"] = "List", ["fields"] = new JsonObject { ["Name"] = CloudKit.String("School"), ["Count"] = CloudKit.Number(1) } });
            else if (body.Array("zones")[0]!.Text("syncToken") is null)
            {
                tagReads++;
                records = new JsonArray(serverRecord!.DeepClone(), new JsonObject { ["recordName"] = "List/1", ["recordType"] = "List", ["fields"] = new JsonObject { ["Name"] = CloudKit.String("School"), ["Count"] = CloudKit.Number(1) } },
                    Tag("Hashtag/1", "old"), Tag("Hashtag/1", "school"), Tag("Hashtag/removed", "removed"), new JsonObject { ["recordName"] = "Hashtag/removed", ["deleted"] = true });
            }
            else records = changedTags ? new JsonArray(Tag("Hashtag/1", "engineering")) : new JsonArray();
            response = new JsonObject { ["zones"] = new JsonArray(new JsonObject { ["syncToken"] = "cursor", ["moreComing"] = false, ["records"] = records }) };
        }
        else throw new Exception("unexpected request " + path);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
    }));
    var local = J.Node(new { id = "local/1", list_id = "List/1", title = "Homework 🛫", description = "Notes", priority = 0, all_day = false, flagged = false });
    cache.Upsert(local, true); cache.Enqueue("local/1", "create", local, null);
    cache.Edit("local/1", new() { ["title"] = "Newer homework" }); cache.Enqueue("local/1", "update", J.Node(new { title = "Newer homework" }), null);
    var sync = new SyncEngine(cache, client, new SemaphoreSlim(1, 1), (name, _) => events.Add(name));
    await sync.Sync(true, default);
    var id = serverRecord!.Required("recordName"); Equal("Newer homework", cache.Reminder(id).Text("title"), "full sync preserves queued edit after id mapping");
    Equal("List/1", cache.Reminder(id).Text("list_id"), "partial upload response preserves list");
    Equal(0, cache.Pending().Count, "outbox drained"); Equal("school", cache.Reminder(id)!.Array("tags")[0]!.GetValue<string>(), "tags refreshed");
    Equal(1, tagReads, "full sync reads tags through zone changes"); Equal(1, cache.Reminder(id)!.Array("tags").Count, "duplicate and deleted tags excluded");
    Equal("School", cache.Lists[0].Text("title"), "full sync lists"); Equal("cursor", cache.Meta("sync_cursor"), "cursor committed after full sync");
    Check(events.Contains("sync_started") && events.Contains("sync_finished"), "sync lifecycle events");
    cache.Edit(id, new() { ["completed"] = true }); cache.Enqueue(id, "update", J.Node(new { completed = true }), cache.Reminder(id).Text("change_tag"));
    await sync.Sync(false, default); Check(cache.Reminder(id).Flag("completed"), "completion uploads"); Check(cache.Reminder(id).Text("completed_date") is not null, "completion date round trip");
    changedTags = true; await sync.Sync(false, default);
    Equal("engineering", cache.Reminder(id)!.Array("tags")[0]!.GetValue<string>(), "delta applies changed tags directly"); Equal(1, tagReads, "delta does not download tag history again");
    Equal("cursor", cache.Meta("sync_cursor"), "delta cursor committed after tag refresh"); changedTags = false;
    cache.Edit(id, new() { ["title"] = "My local version" }); cache.Enqueue(id, "update", J.Node(new { title = "My local version" }), cache.Reminder(id).Text("change_tag"));
    serverRecord!["recordChangeTag"] = "externally-changed"; serverRecord["fields"]!["TitleDocument"] = CloudKit.String(AppleDocument.Encode("Remote version"));
    await sync.Sync(false, default); Equal(1, cache.Conflicts.Count, "remote change becomes conflict");
    Equal("My local version", cache.Conflicts[0]!["local"].Text("title"), "conflict preserves local"); Equal("Remote version", cache.Conflicts[0]!["remote"].Text("title"), "conflict preserves remote");
    Equal(0, cache.Pending().Count, "conflicting upload leaves normal queue");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(syncRoot, true); }
var shutdownRoot = Path.Combine(Path.GetTempPath(), "reminders-shutdown-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var service = new ReminderService(shutdownRoot, new MemorySecrets(), () => new BlockingHandler(started));
    var login = service.CallAsync("login", JsonSerializer.SerializeToElement(new { apple_id = "test@example.com", password = "test" }));
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check((await service.CallAsync("auth_status", JsonSerializer.SerializeToElement(new { }))).ValueKind == JsonValueKind.Object, "status remains available during sign-in");
    await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    try { await login; throw new Exception("shutdown should cancel sign-in"); } catch (OperationCanceledException) { Check(true, "shutdown cancels active direct call"); }
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(shutdownRoot, true); }
await AuthVerificationChecks.RunAsync(Check);
await BrowserSessionTrustChecks.RunAsync(Check);
await SignOutChecks.RunAsync(Check);
await CacheRecoveryChecks.RunAsync(Check);
AppleSignInPolicyChecks.Run(Check);
await RecurrenceAndSyncChecks.RunAsync(Check);
await DemoAccountChecks.RunAsync(Check);
SignInTroubleshootingChecks.Run(Check);
ReminderGroupingChecks.Run(Check);
PaneSizingChecks.Run(Check);
Console.WriteLine($"Passed {passed} backend and scheduling checks.");

sealed class MemorySecrets : ISecrets
{
    private readonly Dictionary<string, string> data = new(); public int Writes; public int FailWriteAfter = int.MaxValue;
    public string? Read(string service, string account) => data.GetValueOrDefault(service + ":" + account);
    public void Write(string service, string account, string value) { if (Writes++ >= FailWriteAfter) throw new IOException("injected write failure"); data[service + ":" + account] = value; }
    public void Delete(string service, string account) => data.Remove(service + ":" + account);
}
sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
}
sealed class ScriptHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
}
sealed class BlockingHandler(TaskCompletionSource started) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException();
    }
}
