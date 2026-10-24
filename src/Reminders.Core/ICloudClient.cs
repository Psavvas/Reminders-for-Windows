using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class ICloudClient : IDisposable
{
    private readonly SecretStore secrets;
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private AppleAuth auth;
    public string Account { get; private set; }
    internal event Action<string>? Diagnostic;
    public bool Connected { get; private set; }
    public bool PendingTwoFactor { get; private set; }
    public bool Restoring { get; set; }
    public bool RestoreRetryable { get; private set; }
    public string RestoreDetail { get; private set; } = "";
    public ICloudClient(string account, SecretStore secrets, Func<HttpMessageHandler>? handlerFactory = null)
    {
        Account = account; this.secrets = secrets; this.handlerFactory = handlerFactory;
        auth = new AppleAuth(J.Parse(secrets.Session(account)) as JsonObject, handlerFactory);
        auth.Diagnostic += message => Diagnostic?.Invoke(message);
    }
    public void SetAccount(string account)
    {
        if (account == Account) return;
        auth.Dispose(); auth = new AppleAuth(null, handlerFactory); Account = account; Connected = false; PendingTwoFactor = false;
        auth.Diagnostic += message => Diagnostic?.Invoke(message);
    }
    public JsonObject Status => new() { ["authenticated"] = Connected && !PendingTwoFactor, ["apple_id"] = Account, ["needs_2fa"] = PendingTwoFactor, ["trusted_session"] = auth.State.Flag("trusted_session"), ["restoring"] = Restoring, ["can_restore"] = secrets.HasPassword(Account), ["two_factor"] = auth.TwoFactorStatus };
    public async Task<JsonNode> Connect(string? password, bool remember, bool acceptTerms, CancellationToken token)
    {
        password ??= secrets.Password(Account) ?? throw new CoreException("AUTH_REQUIRED", "No saved iCloud credential is available");
        try
        {
            await auth.SignIn(Account, password, acceptTerms, token); PendingTwoFactor = auth.RequiresTwoFactor; Connected = !PendingTwoFactor;
            if (remember) secrets.Password(Account, password); Persist();
            if (PendingTwoFactor) throw new CoreException("2FA_REQUIRED", "Two-factor authentication required"); return Status;
        }
        catch (CoreException ex) when (ex.Code == "2FA_REQUIRED") { PendingTwoFactor = true; Connected = false; if (remember) secrets.Password(Account, password); Persist(); throw; }
    }
    public async Task<bool> Restore(CancellationToken token)
    {
        if (Connected && !PendingTwoFactor) return true;
        if (PendingTwoFactor) { RestoreRetryable = false; return false; }
        Restoring = true;
        try
        {
            if (await auth.Resume(token)) { await CheckBrowserAccess(auth, token); Connected = true; Persist(); }
            else await Connect(null, true, false, token);
            RestoreRetryable = false; RestoreDetail = ""; return true;
        }
        catch (CoreException ex) { RestoreRetryable = ex.Code == "NETWORK" && (auth.State.Text("session_token") is not null || auth.State.Flag("browser_cookie_session") && auth.State.Array("cookies").Count > 0 || secrets.Password(Account) is not null); RestoreDetail = ex.Detail; return false; }
        finally { Restoring = false; }
    }
    public Task<JsonNode> RequestCode(bool sms, CancellationToken token) => auth.RequestCode(sms, token);
    public async Task<JsonNode> SubmitCode(string code, CancellationToken token)
    {
        try { await auth.SubmitCode(code, token); PendingTwoFactor = false; Connected = true; return Status; }
        finally { Persist(); }
    }
    public async Task<JsonNode> ImportBrowserSession(string? expectedAccount, JsonObject state, CancellationToken token, Action<string>? validateAccount = null)
    {
        if (!state.Flag("browser_cookie_session") || state.Array("cookies").Count == 0) state.Required("session_token", 16384);
        var candidate = new AppleAuth((JsonObject)state.DeepClone(), handlerFactory);
        candidate.Diagnostic += message => Diagnostic?.Invoke(message);
        try
        {
            if (!await candidate.Resume(token)) throw new CoreException("2FA_REQUIRED", "Finish verification in Apple's sign-in window.");
            var account = candidate.State.Required("apple_id", 320).Trim().ToLowerInvariant();
            if (expectedAccount is not null && !string.Equals(expectedAccount, account, StringComparison.OrdinalIgnoreCase))
                throw new CoreException("AUTH_REQUIRED", "Apple returned a different account. Please sign in again.");
            // Cookie-only handoff learns the identity from iCloud's validation,
            // never from page text. Check queued edits before saving or switching.
            validateAccount?.Invoke(account);
            await CheckBrowserAccess(candidate, token);
            token.ThrowIfCancellationRequested();
            candidate.SaveCookies();
            secrets.Session(account, candidate.State.ToJsonString());
            auth.Dispose(); auth = candidate; Account = account; PendingTwoFactor = false; Connected = true;
            return Status;
        }
        catch { candidate.Dispose(); throw; }
    }
    private static Task CheckBrowserAccess(AppleAuth session, CancellationToken token) =>
        session.State.Flag("browser_cookie_session") && !session.State.Flag("trusted_session")
            ? new CloudKit(session).CheckAccess(token) : Task.CompletedTask;
    internal void Persist() { auth.SaveCookies(); secrets.Session(Account, auth.State.ToJsonString()); }
    public void Invalidate() { Connected = false; PendingTwoFactor = false; }
    public void SignOut() { secrets.Delete(Account); Invalidate(); auth.Dispose(); auth = new AppleAuth(null, handlerFactory); auth.Diagnostic += message => Diagnostic?.Invoke(message); }
    private CloudKit Cloud => Connected && !PendingTwoFactor ? new(auth) : throw new CoreException("AUTH_REQUIRED", "Not signed in to iCloud");
    public Task<string?> Cursor(CancellationToken token) => Cloud.Cursor(token);
    public Task<(JsonArray Records, string? Cursor)> Changes(string? cursor, CancellationToken token) => Cloud.Changes(cursor, ["Reminder", "List", "Hashtag", "RecurrenceRule"], token);
    public async Task<JsonArray> Lists(CancellationToken token)
    {
        var (records, _) = await Cloud.Changes(null, ["List"], token); var lists = new JsonArray();
        foreach (var r in records.OfType<JsonObject>())
        {
            if (r.Text("recordType") != "List" || r.Flag("deleted") || r.Text("reason") == "deleted" || CloudKit.Int(r, "Deleted") != 0 || r["fields"] is not JsonObject || string.IsNullOrEmpty(r.Text("recordName"))) continue;
            var rawColor = CloudKit.Text(r, "Color"); var color = rawColor?.StartsWith('#') == true ? rawColor : J.Parse(rawColor).Text("daHexString");
            lists.Add(J.Node(new { id = r.Text("recordName"), title = CloudKit.Text(r, "Name") ?? "Untitled", color_hex = color, count = CloudKit.Int(r, "Count"), is_group = CloudKit.Int(r, "IsGroup") != 0, position = lists.Count }));
        }
        return lists;
    }
    public async Task<JsonArray> RemindersFor(string list, CancellationToken token)
    {
        var filters = new JsonArray(); foreach (var (name, value) in new[] { ("List", CloudKit.Reference(list)), ("includeCompleted", CloudKit.Number(1)), ("LookupValidatingReference", CloudKit.Number(1)) }) filters.Add(new JsonObject { ["fieldName"] = name, ["comparator"] = "EQUALS", ["fieldValue"] = value });
        var records = await Cloud.Query("reminderList", filters, token); return new JsonArray(records.OfType<JsonObject>().Where(r => r.Text("recordType") == "Reminder").Select(ToReminder).Where(r => r is not null).ToArray());
    }
    public async Task<Dictionary<string, JsonArray>> Tags(IEnumerable<string> ids, CancellationToken token)
    {
        // Hashtag is not queryable through records/query in the Reminders service.
        // Read the zone change stream, which also includes deleted tag records.
        var included = ids.ToHashSet(); var result = new Dictionary<string, JsonArray>();
        if (included.Count == 0) return result;
        var (records, _) = await Cloud.Changes(null, ["Hashtag"], token);
        var latest = new Dictionary<string, JsonObject>();
        foreach (var r in records.OfType<JsonObject>())
            if (r.Text("recordName") is { Length: > 0 } recordName) latest[recordName] = r;
        foreach (var r in latest.Values)
        {
            if (r.Text("recordType") != "Hashtag" || r.Flag("deleted") || r.Text("reason") == "deleted") continue;
            var id = CloudKit.Field(r, "Reminder").Text("recordName"); var name = CloudKit.Text(r, "Name");
            if (id is null || !included.Contains(id) || name is null || CloudKit.Int(r, "Deleted") != 0) continue;
            if (!result.TryGetValue(id, out var tags)) result[id] = tags = new(); tags.Add(J.Node(new { id = r.Text("recordName"), name, reminder_id = id }));
        }
        return result;
    }
    internal static JsonObject? ToReminder(JsonNode r)
    {
        if (r.Text("recordName") is not { Length: > 0 } id) return null;
        var zone = CloudKit.Text(r, "TimeZone");
        string? Timestamp(string name) => CloudKit.Field(r, name) is JsonValue v && v.TryGetValue<long>(out var ms) ? TimeUtil.Iso(DateTimeOffset.FromUnixTimeMilliseconds(ms)) : null;
        return new JsonObject
        {
            ["id"] = id, ["list_id"] = CloudKit.Field(r, "List").Text("recordName") ?? "", ["title"] = AppleDocument.Decode(CloudKit.Field(r, "TitleDocument")?.ToString()) ?? CloudKit.Text(r, "Title") ?? "", ["description"] = AppleDocument.Decode(CloudKit.Field(r, "NotesDocument")?.ToString()) ?? CloudKit.Text(r, "Description") ?? "",
            ["due_date"] = CloudKit.Field(r, "DueDate") is JsonValue v && v.TryGetValue<long>(out var due) ? TimeUtil.Iso(TimeUtil.FromFloating(due, zone)) : null, ["time_zone"] = zone, ["recurrence_ids"] = CloudKit.Field(r, "RecurrenceRuleIDs")?.DeepClone() ?? new JsonArray(),
            ["priority"] = CloudKit.Int(r, "Priority"), ["completed"] = CloudKit.Int(r, "Completed") != 0, ["completed_date"] = Timestamp("CompletionDate"), ["flagged"] = CloudKit.Int(r, "Flagged") != 0, ["all_day"] = CloudKit.Int(r, "AllDay") != 0, ["deleted"] = CloudKit.Int(r, "Deleted") != 0, ["created"] = Timestamp("CreationDate"), ["modified"] = Timestamp("LastModifiedDate"), ["change_tag"] = r.Text("recordChangeTag"), ["tags"] = new JsonArray(), ["hashtag_ids"] = new JsonArray(), ["dirty"] = 0, ["notified"] = 0
        };
    }
    internal static JsonObject UpdateFields(JsonNode record, JsonObject patch)
    {
        var fields = new JsonObject(); var tokens = new List<string>();
        foreach (var (json, cloud, logical) in new[] { ("title", "TitleDocument", "titleDocument"), ("description", "NotesDocument", "notesDocument") })
        {
            if (!patch.ContainsKey(json)) continue;
            var document = CloudKit.Field(record, cloud)?.ToString();
            if (document is null && CloudKit.Field(record, cloud + "Asset") is not null)
                throw CoreException.Bad("Edit this reminder's large text document on an Apple device.");
            fields[cloud] = CloudKit.String(AppleDocument.Edit(document, patch.Text(json) ?? "")); tokens.Add(logical);
        }
        foreach (var (json, cloud) in new[] { ("priority", "Priority"), ("completed", "Completed"), ("flagged", "Flagged"), ("deleted", "Deleted"), ("all_day", "AllDay") }) if (patch.ContainsKey(json)) { fields[cloud] = CloudKit.Number(patch.Number(json, patch.Flag(json) ? 1 : 0)); tokens.Add(json == "all_day" ? "allDay" : json); }
        if (patch.ContainsKey("completed")) { fields["CompletionDate"] = CloudKit.Timestamp(patch.Flag("completed") ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null); tokens.Add("completionDate"); }
        if (patch.ContainsKey("due_date")) { fields["DueDate"] = CloudKit.Timestamp(patch.Text("due_date") is { } due ? TimeUtil.ToFloating(TimeUtil.Parse(due), CloudKit.Text(record, "TimeZone")) : null); tokens.Add("dueDate"); }
        fields["LastModifiedDate"] = CloudKit.Timestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); tokens.Add("lastModifiedDate"); fields["ResolutionTokenMap"] = CloudKit.ResolutionTokens(tokens, record);
        // The earlier tag writer invented a token that Apple's Reminders model
        // does not define. Preserve all other field conflict-resolution tokens.
        var map = J.Parse(fields["ResolutionTokenMap"].Text("value"));
        if (map?["map"] is JsonObject entries && entries.Remove("hashtagIDs"))
            fields["ResolutionTokenMap"] = CloudKit.String(map.ToJsonString());
        return fields;
    }
    public async Task<JsonObject> Create(JsonObject payload, CancellationToken token)
    {
        var id = payload.Text("cloud_id") ?? "Reminder/" + Guid.NewGuid().ToString().ToUpperInvariant(); var fields = UpdateFields(new JsonObject(), payload);
        fields["List"] = CloudKit.Reference(payload.Required("list_id")); fields["Completed"] = CloudKit.Number(0); fields["CompletionDate"] = CloudKit.Timestamp(null); fields["Deleted"] = CloudKit.Number(0); fields["CreationDate"] = CloudKit.Timestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var operations = new JsonArray();
        if (payload["recurrence"] is JsonObject repeat)
        {
            var ruleId = payload.Text("recurrence_id") ?? Guid.NewGuid().ToString().ToUpperInvariant();
            fields["RecurrenceRuleIDs"] = J.Node(new { type = "STRING_LIST", value = new[] { ruleId } });
            AddTokens(fields, "recurrenceRuleIDs");
            operations.Add(CloudKit.Operation("create", "RecurrenceRule/" + ruleId, null, Recurrence.Fields(id, repeat), "RecurrenceRule", id));
        }
        operations.Insert(0, CloudKit.Operation("create", id, null, fields, parent: payload.Required("list_id")));
        // A stable identity lets a retry recover a committed create whose reply was lost.
        var existing = payload.Text("cloud_id") is null ? null : (await Cloud.LookupMany([id], token, true)).FirstOrDefault();
        if (existing is not null) return Snapshot(existing);
        var result = await Cloud.Modify(operations, token);
        return MergeResult(new JsonObject { ["recordName"] = id, ["fields"] = fields.DeepClone() }, result);
    }
    public async Task<JsonObject> Update(string id, JsonObject patch, string? baseTag, CancellationToken token)
    {
        var original = (await Cloud.Lookup(id, token)).FirstOrDefault() ?? throw CoreException.Bad($"No such reminder: {id}"); var remoteTag = original.Text("recordChangeTag");
        if (patch.ContainsKey("recurrence")) return await ChangeRecurrence(original, patch, baseTag, token);
        if (patch.Flag("completed") && CloudKit.Field(original, "RecurrenceRuleIDs") is JsonArray { Count: > 0 })
            return await CompleteRecurring(original, patch, baseTag, token);
        if (baseTag is not null && baseTag != remoteTag) throw new CoreException("CONFLICT", "This reminder changed on another device while you were editing it.", ToReminder(original)?.ToJsonString() ?? "{}");
        var fields = UpdateFields(original, patch); var result = await Cloud.Modify(CloudKit.Operation("update", id, remoteTag, (JsonObject)fields.DeepClone()), token);
        var returned = result.FirstOrDefault() ?? throw new CoreException("ERROR", "iCloud did not return the updated reminder");
        var target = original["fields"] as JsonObject ?? new(); original["fields"] = null; original["fields"] = target;
        foreach (var p in fields) target[p.Key] = p.Value?.DeepClone(); if (returned["fields"] is JsonObject changed) foreach (var p in changed) target[p.Key] = p.Value?.DeepClone();
        if (returned["recordChangeTag"] is { } tag) original["recordChangeTag"] = tag.DeepClone();
        return Snapshot(original);
    }
    public void Dispose() => auth.Dispose();
}
