using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reminders.Core;

/// <summary>In-process iCloud engine. Cache calls remain available during network operations.</summary>
public sealed class ReminderService : IAsyncDisposable
{
    private readonly Cache cache;
    private readonly ICloudClient? cloudClient;
    private readonly SyncEngine? sync;
    private ICloudClient client => cloudClient ?? throw new CoreException("AUTH_REQUIRED", "This account only stores reminders on this PC.");
    public bool IsLocalDemo => cloudClient is null;
    private readonly SemaphoreSlim clientGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource backgroundLifetime = new();
    private readonly ConcurrentDictionary<long, Task> background = new();
    private readonly ConcurrentDictionary<long, Task> queuedWork = new();
    private readonly object workGate = new();
    private long nextTask;
    private JsonObject authSnapshot;
    private bool disposed;
    public event Action<string, JsonElement>? EventReceived;
    public event Action<string>? Diagnostic;
    public ReminderService(string dataDirectory) : this(dataDirectory, new WindowsSecrets()) { }
    internal ReminderService(string dataDirectory, ISecrets secrets, Func<HttpMessageHandler>? handlerFactory = null)
    {
        cache = new Cache(Path.Combine(dataDirectory, "cache.db"));
        cloudClient = new ICloudClient((cache.Meta("apple_id") ?? "").Trim().ToLowerInvariant(), new SecretStore(secrets), handlerFactory);
        client.Diagnostic += message => Diagnostic?.Invoke(message);
        authSnapshot = client.Status; sync = new(cache, client, clientGate, Emit);
    }
    private ReminderService(Cache localCache)
    {
        // This constructor never opens the iCloud cache, credential vault or HTTP client.
        cache = localCache;
        try { DemoAccount.Seed(cache); }
        catch { cache.Dispose(); throw; }
        authSnapshot = (JsonObject)J.Node(new { authenticated = true, apple_id = "demo", needs_2fa = false, restoring = false, local_only = true });
    }
    public static ReminderService OpenDemo(string dataDirectory, string? password)
    {
        DemoAccount.ValidatePassword(password);
        return new ReminderService(new Cache(Path.Combine(dataDirectory, "demo", "cache.db")));
    }
    private void QueueSync(bool full = false)
    {
        if (sync is not null && cache.Meta("signed_out") != "1") Queue(ct => sync.Sync(full, ct));
    }
    private void Emit(string name, JsonNode data)
    {
        if (name == "sync_progress" && data.Text("message") is { } message) Diagnostic?.Invoke("sync: " + message);
        if (name == "auth_changed" && data is JsonObject status) Volatile.Write(ref authSnapshot, (JsonObject)status.DeepClone());
        // Subscribers cannot abort a sync by throwing from a UI event handler.
        try { EventReceived?.Invoke(name, JsonSerializer.SerializeToElement(data)); } catch (Exception ex) { Diagnostic?.Invoke($"Event '{name}' failed: {ex.GetType().Name}"); }
    }
    private void Queue(Func<CancellationToken, Task> action)
    {
        lock (workGate)
        {
            if (disposed) return;
            var backgroundToken = backgroundLifetime.Token;
            var id = Interlocked.Increment(ref nextTask);
            var task = Task.Run(async () =>
            {
                using var workLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, backgroundToken);
                try { await action(workLifetime.Token); }
                catch (OperationCanceledException) when (workLifetime.IsCancellationRequested) { }
                catch (CoreException ex) { Emit("sync_error", ex.Body); Diagnostic?.Invoke($"sync: {ex.Code}: {ex.Message}"); }
                catch (Exception ex) { Emit("sync_error", new CoreException("ERROR", "The sync operation failed", ex.GetType().Name).Body); Diagnostic?.Invoke($"sync failed: {ex.GetType().Name}"); }
            });
            background[id] = task;
            queuedWork[id] = task;
            _ = task.ContinueWith(_ => { background.TryRemove(id, out var ignored); queuedWork.TryRemove(id, out ignored); }, TaskScheduler.Default);
        }
    }
    private async Task CancelBackgroundWorkAsync()
    {
        Task[] work;
        CancellationTokenSource source;
        lock (workGate) { source = backgroundLifetime; work = queuedWork.Values.ToArray(); }
        await source.CancelAsync();
        await Task.WhenAll(work);
    }
    private void ResumeBackgroundWork()
    {
        lock (workGate)
        {
            if (!backgroundLifetime.IsCancellationRequested) return;
            backgroundLifetime.Dispose(); backgroundLifetime = new();
        }
    }
    public Task StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
        if (IsLocalDemo) { Emit("ready", J.Node(new { apple_id = "demo", local_only = true })); return Task.CompletedTask; }
        if (client.Account.Length > 0 && cache.Meta("signed_out") != "1")
        {
            client.Restoring = true; authSnapshot = client.Status;
            Queue(async ct =>
            {
                bool restored = false;
                foreach (var delay in new[] { 0, 5, 15, 45, 120, 300 })
                {
                    if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    restored = await WithClient(() => client.Restore(ct), ct);
                    if (restored || !client.RestoreRetryable) break;
                    client.Restoring = true; Volatile.Write(ref authSnapshot, client.Status);
                }
                client.Restoring = false; Emit("auth_changed", client.Status);
                if (restored) await sync!.Sync(cache.Lists.Count == 0, ct);
            });
        }
        Emit("ready", J.Node(new { apple_id = client.Account })); return Task.CompletedTask;
    }
    private async Task<T> WithClient<T>(Func<Task<T>> action, CancellationToken token)
    {
        if (IsLocalDemo) throw new CoreException("AUTH_REQUIRED", "This account only stores reminders on this PC.");
        await clientGate.WaitAsync(token);
        try { return await action(); }
        finally { try { Volatile.Write(ref authSnapshot, client.Status); } finally { clientGate.Release(); } }
    }
    public async Task<JsonElement> CallAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        Task<JsonElement> call; long id;
        lock (workGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            id = Interlocked.Increment(ref nextTask);
            call = CallCore(method, parameters, cancellationToken); background[id] = call;
        }
        try { return await call; } finally { background.TryRemove(id, out _); }
    }
    private async Task<JsonElement> CallCore(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var p = parameters.ValueKind == JsonValueKind.Object ? JsonNode.Parse(parameters.GetRawText())!.AsObject() : new JsonObject();
        var result = await Dispatch(method, p, linked.Token);
        return JsonSerializer.SerializeToElement(result);
    }
    private async Task<JsonNode?> Dispatch(string method, JsonObject p, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (method)
        {
            case "ping": return J.Node(new { pong = true });
            case "auth_status":
                var status = (JsonObject)Volatile.Read(ref authSnapshot).DeepClone(); status["signed_out"] = cache.Meta("signed_out") == "1"; status["has_cache"] = !status.Flag("signed_out") && cache.Lists.Count > 0; status["last_sync"] = cache.Meta("last_sync"); return status;
            case "login":
                if (IsLocalDemo) throw CoreException.Bad("Leave the local account before signing in again.");
                var account = (p.Optional("apple_id", 320) ?? cache.Meta("apple_id") ?? "").Trim().ToLowerInvariant(); if (account.Length == 0) throw new CoreException("AUTH_REQUIRED", "No Apple ID provided");
                if (DemoAccount.IsDemoUsername(account)) throw new CoreException("AUTH_REQUIRED", "Use the local sign-in flow for this account.");
                var password = p.Optional("password", 4096);
                var login = await WithClient(async () =>
                {
                    // Mixing another account's cached reminders with a new session
                    // would upload edits to the wrong CloudKit container.
                    if (client.Account.Length > 0 && client.Account != account && cache.Pending(1).Count > 0) throw CoreException.Bad("Sync queued changes or sign out and clear the cache before switching accounts.");
                    if (client.Account.Length > 0 && client.Account != account) cache.Purge();
                    client.SetAccount(account); cache.SetMeta("apple_id", account);
                    return await client.Connect(password, cache.Settings.Flag("remember_password"), p.Flag("accept_terms"), token);
                }, token);
                cache.SetMeta("signed_out", null);
                ResumeBackgroundWork();
                QueueSync(cache.Lists.Count == 0); return login;
            case "request_2fa": return await WithClient(() => client.RequestCode(p.Text("method") == "sms", token), token);
            case "browser_login":
                var browserAccount = p.Optional("apple_id", 320)?.Trim().ToLowerInvariant();
                var browserState = p["state"] as JsonObject ?? throw CoreException.Bad("Missing Apple session.");
                var browserLogin = await WithClient(async () =>
                {
                    var switching = false;
                    var authenticated = await client.ImportBrowserSession(browserAccount, browserState, token, account =>
                    {
                        switching = client.Account.Length > 0 && client.Account != account;
                        if (switching && cache.Pending(1).Count > 0) throw CoreException.Bad("Sync queued changes with this account before switching accounts.");
                    });
                    if (switching) cache.Purge();
                    cache.SetMeta("apple_id", authenticated.Required("apple_id")); cache.SetMeta("signed_out", null); return authenticated;
                }, token);
                ResumeBackgroundWork();
                QueueSync(cache.Lists.Count == 0); return browserLogin;
            case "submit_2fa":
                var code = p.Required("code"); var verified = await WithClient(() => client.SubmitCode(code, token), token); cache.SetMeta("signed_out", null); ResumeBackgroundWork(); QueueSync(cache.Lists.Count == 0); return verified;
            case "sign_out":
                if (IsLocalDemo) { authSnapshot["authenticated"] = false; return J.Node(new { signed_out = true }); }
                // Restoration and full-history sync can hold the account gate
                // across slow HTTP calls. Cancel them before waiting to sign out.
                await CancelBackgroundWorkAsync();
                await WithClient(() => { client.SignOut(); cache.SetMeta("signed_out", "1"); return Task.FromResult(true); }, token);
                if (p.Flag("purge")) cache.SetMeta("sync_cursor", null); return J.Node(new { signed_out = true });
            case "clear_cache":
                if (IsLocalDemo) throw CoreException.Bad("Local-account reminders are stored only on this PC and are not a downloadable cache.");
                await CancelBackgroundWorkAsync();
                try
                {
                    return await WithClient(() =>
                    {
                        return Task.FromResult(cache.Clear(p.Flag("discard_pending")));
                    }, token);
                }
                finally
                {
                    ResumeBackgroundWork();
                    if (!lifetime.IsCancellationRequested) await StartAsync(lifetime.Token);
                }
            case "lists": return cache.Lists;
            case "reminders": return cache.Reminders(p);
            case "reminder": return cache.Reminder(cache.ResolveId(p.Required("id")));
            case "tags": return cache.Tags;
            case "create_tag":
                var tagResult = cache.Transaction(() => AddTag(p.Required("id"), p.Required("name", 256)));
                QueueSync(); return tagResult;
            case "smart_counts": return cache.Counts;
            case "settings": return cache.Settings;
            case "set_settings": return cache.SetSettings(p);
            case "create_reminder":
                var r = new JsonObject { ["id"] = "local/" + Guid.NewGuid(), ["list_id"] = p.Required("list_id"), ["title"] = p.Optional("title", 4096) ?? "", ["description"] = p.Optional("description", 100000) ?? "", ["due_date"] = TimeUtil.Normalize(p["due_date"], p.Flag("all_day")), ["priority"] = p.Number("priority"), ["completed"] = false, ["flagged"] = p.Flag("flagged"), ["all_day"] = p.Flag("all_day"), ["created"] = TimeUtil.Now, ["modified"] = TimeUtil.Now };
                r["recurrence"] = Recurrence.Validate(p["recurrence"], r.Text("due_date"));
                r["cloud_id"] = "Reminder/" + Guid.NewGuid().ToString().ToUpperInvariant();
                r["recurrence_id"] = Guid.NewGuid().ToString().ToUpperInvariant();
                var createTags = p.ContainsKey("tags") ? ReminderTags.Names(p["tags"]) : [];
                cache.Transaction(() => { cache.Upsert(r, !IsLocalDemo); if (!IsLocalDemo) cache.Enqueue(r.Required("id"), "create", r, null); SaveTags(r.Required("id"), createTags, null); return true; }); QueueSync(); return cache.Reminder(r.Required("id"));
            case "update_reminder": return LocalChange(p, "update");
            case "delete_reminder": return LocalChange(p, "delete");
            case "restore_reminder": return LocalChange(p, "restore");
            case "sync": QueueSync(p.Flag("full")); return J.Node(new { queued = !IsLocalDemo && cache.Meta("signed_out") != "1", local_only = IsLocalDemo });
            case "sync_status": return J.Node(new { running = sync?.Running ?? false, local_only = IsLocalDemo, last_sync = cache.Meta("last_sync"), has_cursor = cache.Meta("sync_cursor") is not null, pending_pushes = cache.Pending().Count, conflicts = cache.Conflicts.Count, sync_minutes = cache.Settings.Number("sync_minutes", 1) });
            case "conflicts": return cache.Conflicts;
            case "resolve_conflict":
                var conflictId = p.Number("id", -1); if (conflictId < 0) throw CoreException.Bad("missing conflict id");
                var conflict = cache.Conflicts.FirstOrDefault(c => c.Number("id") == conflictId);
                if (p.Text("keep") == "local" && conflict?["local"] is JsonObject local)
                {
                    var patch = new JsonObject { ["id"] = conflict.Required("reminder_id") };
                    foreach (var f in Cache.EditFields)
                        if (local.ContainsKey(f) && (f != "recurrence" || local.Flag("recurrence_edit"))) patch[f] = local[f]?.DeepClone();
                    if (local.Flag("tags_edit")) patch["tags"] = local["tags"]?.DeepClone();
                    LocalChange(patch, "update");
                }
                cache.Execute("UPDATE conflicts SET resolved=1 WHERE id=@p0", conflictId); return J.Node(new { resolved = conflictId });
            case "due_notifications": return Notifications(p);
            case "shutdown": return J.Node(new { bye = true });
            default: throw new CoreException("NO_METHOD", $"unknown method '{method}'");
        }
    }
    private JsonNode LocalChange(JsonObject p, string operation)
    {
        var id = cache.ResolveId(p.Required("id")); JsonObject? result = null;
        cache.Transaction(() =>
        {
            var current = cache.Reminder(id) ?? throw CoreException.Bad($"No such reminder: {id}"); var fields = new JsonObject();
            var tags = p.ContainsKey("tags") ? ReminderTags.Names(p["tags"]) : null;
            if (operation is "delete" or "restore") fields["deleted"] = operation == "delete";
            else
            {
                foreach (var (key, max) in new[] { ("title", 4096), ("description", 100000) }) if (p.Optional(key, max) is { } value) fields[key] = value;
                foreach (var key in new[] { "priority", "completed", "flagged", "all_day" }) if (p.ContainsKey(key)) fields[key] = p[key]?.DeepClone();
                var allDay = fields.ContainsKey("all_day") ? fields.Flag("all_day") : current.Flag("all_day");
                if (p.ContainsKey("due_date")) fields["due_date"] = TimeUtil.Normalize(p["due_date"], allDay);
                else if (p.ContainsKey("all_day") && allDay && current.Text("due_date") is { } raw) fields["due_date"] = TimeUtil.Normalize(JsonValue.Create(raw), true);
                var due = fields.ContainsKey("due_date") ? fields.Text("due_date") : current.Text("due_date");
                if (p.ContainsKey("recurrence"))
                {
                    if (fields.Flag("completed")) throw CoreException.Bad("Save the repeat change before completing this reminder.");
                    if (current["recurrence"] is JsonObject existing && existing.Property("supported") is not null && !existing.Flag("supported"))
                        throw CoreException.Bad("Edit this custom repeat on an Apple device to preserve its schedule.");
                    fields["recurrence"] = Recurrence.Validate(p["recurrence"], due);
                    fields["recurrence_id"] = Guid.NewGuid().ToString().ToUpperInvariant();
                    if (fields["recurrence"] is null) fields["recurrence_ids"] = new JsonArray();
                }
                var effectiveRepeat = fields.ContainsKey("recurrence") ? fields["recurrence"] : current["recurrence"];
                if (effectiveRepeat is not null)
                {
                    if (fields.ContainsKey("due_date") && fields["due_date"] is null) throw CoreException.Bad("A repeating reminder needs a due date.");
                    if (fields.Flag("completed") && effectiveRepeat.Property("supported")?.ToString() == "false")
                        throw CoreException.Bad("Complete this custom repeat on an Apple device to preserve its schedule.");
                }
                if (fields.Flag("completed"))
                {
                    if (current.Flag("completed")) { result = current; return true; }
                    fields["completion_id"] = "Reminder/" + Guid.NewGuid().ToString().ToUpperInvariant();
                }
            }
            if (IsLocalDemo && fields.Flag("completed") && current["recurrence"] is JsonObject localRepeat && current.Text("due_date") is { } localDue)
            {
                var next = Recurrence.Next(new JsonObject { ["fields"] = Recurrence.Fields(id, localRepeat) }, localDue, current.Text("time_zone"), DateTimeOffset.UtcNow);
                if (next is not null)
                {
                    var occurrence = (JsonObject)current.DeepClone(); occurrence["id"] = fields.Required("completion_id");
                    occurrence["completed"] = true; occurrence["completed_date"] = TimeUtil.Now; occurrence["recurrence"] = null; occurrence["recurrence_ids"] = new JsonArray();
                    cache.Upsert(occurrence); fields["completed"] = false; fields["due_date"] = next;
                }
            }
            cache.Edit(id, fields, dirty: !IsLocalDemo);
            if (IsLocalDemo && fields.ContainsKey("completed")) cache.Execute("UPDATE reminders SET completed_date=@p0 WHERE id=@p1", fields.Flag("completed") ? TimeUtil.Now : null, id);
            if (!IsLocalDemo && fields.Count > 0) cache.Enqueue(id, operation == "delete" ? "delete" : "update", operation == "delete" ? new JsonObject() : fields, current.Text("change_tag"));
            if (tags is not null) SaveTags(id, tags, current.Text("change_tag"));
            result = cache.Reminder(id); return true;
        });
        QueueSync(); return operation == "delete" ? J.Node(new { deleted = id }) : result!;
    }
    private JsonObject AddTag(string id, string rawName)
    {
        id = cache.ResolveId(id);
        var current = cache.Reminder(id) ?? throw CoreException.Bad("Select a reminder before adding a tag.");
        var names = ReminderTags.Names(new JsonArray(rawName));
        SaveTags(id, current.Array("tags").Select(t => t!.ToString()).Concat(names).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), current.Text("change_tag"));
        return cache.Reminder(id)!;
    }
    // Called inside the same SQLite transaction as the task edit. Each tag
    // identity is allocated once and survives offline edits and lost replies.
    private void SaveTags(string id, string[] names, string? baseTag)
    {
        var previous = cache.Query("SELECT id,name FROM tags WHERE reminder_id=@p0 ORDER BY name", id);
        if (previous.Select(t => t!.Required("name")).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(names)) return;
        var tags = new JsonArray(names.Select(name =>
        {
            var existing = previous.FirstOrDefault(t => string.Equals(t.Text("name"), name, StringComparison.OrdinalIgnoreCase));
            return (JsonNode)new JsonObject { ["id"] = existing?.Required("id") ?? "Hashtag/" + Guid.NewGuid().ToString().ToUpperInvariant(), ["name"] = existing?.Required("name") ?? name };
        }).ToArray());
        cache.ReplaceTagsInTransaction(id, tags);
        if (!IsLocalDemo)
        {
            cache.Execute("UPDATE reminders SET dirty=1 WHERE id=@p0", id);
            cache.Enqueue(id, "tags", new JsonObject { ["tags"] = tags, ["previous_ids"] = new JsonArray(previous.Select(t => JsonValue.Create(t.Text("id"))).ToArray()) }, baseTag);
        }
    }
    private JsonNode Notifications(JsonObject p)
    {
        var settings = cache.Settings; var toasts = new JsonArray(); var ids = new JsonArray();
        if (cache.Meta("signed_out") == "1" || !settings.Flag("notifications_enabled")) return new JsonObject { ["toasts"] = toasts, ["notified_ids"] = ids };
        var now = p.Text("now") is { } raw ? TimeUtil.Parse(raw) : DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-Math.Clamp(p.Number("stale_after_minutes", settings.Number("stale_after_minutes", 60)), 1, 1440));
        var due = cache.Query("SELECT * FROM reminders WHERE deleted=0 AND completed=0 AND notified=0 AND due_date IS NOT NULL AND due_date<=@p0 ORDER BY due_date", TimeUtil.Iso(now)); var missed = new List<JsonNode>(); var fresh = new List<JsonNode>();
        foreach (var r in due.OfType<JsonObject>()) { var alert = TimeUtil.NotifyAt(r.Required("due_date"), r.Flag("all_day")); if (alert > now) continue; (alert < cutoff ? missed : fresh).Add(r); }
        string Names(List<JsonNode> reminders) => string.Join(", ", reminders.Take(3).Select(r => r.Text("title")).Where(s => !string.IsNullOrEmpty(s)));
        if (missed.Count > 0) toasts.Add(J.Node(new { title = $"{missed.Count} reminder{(missed.Count == 1 ? "" : "s")} were due while you were away", body = Names(missed) + (missed.Count > 3 ? $", and {missed.Count - 3} more" : ""), reminder_id = (string?)null, kind = "summary" }));
        if (fresh.Count > Math.Clamp(p.Number("max_individual", settings.Number("max_individual_toasts", 3)), 1, 10)) toasts.Add(J.Node(new { title = $"{fresh.Count} reminders are due", body = Names(fresh), reminder_id = (string?)null, kind = "summary" }));
        else foreach (var r in fresh) toasts.Add(J.Node(new { title = r.Text("title") is { Length: > 0 } title ? title : "Reminder", body = TimeZoneInfo.ConvertTime(TimeUtil.Parse(r.Required("due_date")), TimeUtil.Zone()).ToString(r.Flag("all_day") ? "ddd dd MMM" : "ddd dd MMM, HH:mm"), reminder_id = r.Text("id"), kind = "reminder" }));
        foreach (var r in missed.Concat(fresh)) { ids.Add(r.Text("id")); cache.Execute("UPDATE reminders SET notified=1 WHERE id=@p0", r.Text("id")); }
        return new JsonObject { ["toasts"] = toasts, ["notified_ids"] = ids };
    }
    public async ValueTask DisposeAsync()
    {
        Task[] work;
        lock (workGate) { if (disposed) return; disposed = true; work = background.Values.ToArray(); }
        await lifetime.CancelAsync();
        // Failed direct calls have already reached their callers. Their failure
        // must not prevent disposal of the cache or HTTP client.
        try { await Task.WhenAll(work); } catch (Exception) { }
        await clientGate.WaitAsync();
        try
        {
            try { if (cloudClient?.Connected == true) cloudClient.Persist(); }
            finally { cloudClient?.Dispose(); cache.Dispose(); }
        }
        finally { clientGate.Release(); }
        lifetime.Dispose(); backgroundLifetime.Dispose(); clientGate.Dispose();
    }
}
