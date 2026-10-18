using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed class SyncEngine(Cache cache, ICloudClient client, SemaphoreSlim clientGate, Action<string, JsonNode> emit)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private long requested, completed;
    private int fullRequested;
    public bool Running { get; private set; }
    private async Task<T> WithClient<T>(Func<Task<T>> action, CancellationToken token)
    {
        await clientGate.WaitAsync(token); try { return await action(); } finally { clientGate.Release(); }
    }
    private void Progress(string message) => emit("sync_progress", J.Node(new { message }));
    public async Task<JsonNode> Sync(bool full, CancellationToken token)
    {
        if (full) Interlocked.Exchange(ref fullRequested, 1); var ticket = Interlocked.Increment(ref requested);
        await serial.WaitAsync(token);
        try
        {
            if (Interlocked.Read(ref completed) >= ticket) return J.Node(new { coalesced = true });
            Running = true;
            while (true)
            {
                var generation = Interlocked.Read(ref requested); var force = Interlocked.Exchange(ref fullRequested, 0) != 0;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
                try
                {
                    Progress("Uploading queued changes..."); var push = await Recover(() => Push(deadline.Token), deadline.Token);
                    var needsFull = force || cache.Meta("sync_cursor") is null || cache.Meta("cloudkit_read_protocol") != "4";
                    var pull = await Recover(() => needsFull ? Full(deadline.Token) : Delta(deadline.Token), deadline.Token);
                    Interlocked.Exchange(ref completed, generation);
                    if (Interlocked.Read(ref requested) == generation && cache.Pending(1).Count == 0) return new JsonObject { ["push"] = push, ["pull"] = pull };
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { if (force) Interlocked.Exchange(ref fullRequested, 1); throw new CoreException("NETWORK", "Sync timed out after 10 minutes. Cached reminders are available; try syncing again."); }
                catch { if (force) Interlocked.Exchange(ref fullRequested, 1); throw; }
            }
        }
        finally { Running = false; serial.Release(); }
    }
    private async Task<JsonNode> Recover(Func<Task<JsonNode>> operation, CancellationToken token)
    {
        try { return await operation(); }
        catch (CoreException ex) when (ex.Code == "AUTH_REQUIRED")
        {
            var restored = await WithClient(async () =>
            {
                if (client.PendingTwoFactor) throw new CoreException("2FA_REQUIRED", "Enter the verification code to finish signing in.");
                client.Invalidate(); var ok = await client.Restore(token); emit("auth_changed", client.Status);
                if (!ok && client.RestoreRetryable) throw new CoreException("NETWORK", "Couldn't reach iCloud. Sync will try again shortly.", client.RestoreDetail);
                return ok;
            }, token);
            if (!restored) throw new CoreException("AUTH_REQUIRED", "Your iCloud session expired. Please sign in again."); return await operation();
        }
    }
    private async Task<JsonNode> Full(CancellationToken token)
    {
        emit("sync_started", J.Node(new { mode = "full", determinate = false })); Progress("Downloading reminders, lists, tags and repeat schedules...");
        // One paginated zone stream replaces N list queries and separate tag
        // history reads. Its cursor describes exactly the snapshot we apply.
        var (records, cursor) = await WithClient(() => client.Changes(null, token), token);
        cache.ApplyRecords(records, full: true);
        if (cursor is not null) cache.SetMeta("sync_cursor", cursor);
        cache.SetMeta("last_full_sync", TimeUtil.Now); cache.SetMeta("last_sync", TimeUtil.Now); cache.SetMeta("cloudkit_read_protocol", "4");
        await WithClient(() => { client.Persist(); return Task.FromResult(true); }, token);
        var result = J.Node(new { mode = "full", records = records.Count }); emit("sync_finished", result.DeepClone()); return result;
    }
    private async Task<JsonNode> Delta(CancellationToken token)
    {
        emit("sync_started", J.Node(new { mode = "delta", determinate = false })); Progress("Downloading changed reminders...");
        var (records, cursor) = await WithClient(() => client.Changes(cache.Meta("sync_cursor"), token), token);
        cache.ApplyRecords(records);
        if (cursor is not null) cache.SetMeta("sync_cursor", cursor); cache.SetMeta("last_sync", TimeUtil.Now);
        await WithClient(() => { client.Persist(); return Task.FromResult(true); }, token);
        var result = J.Node(new { mode = "delta", updated = records.Count }); emit("sync_finished", result.DeepClone()); return result;
    }
    private async Task<JsonNode> Push(CancellationToken token)
    {
        var pushed = 0; var conflicts = 0;
        for (var i = 0; i < 100; i++)
        {
            var item = cache.Pending(1).FirstOrDefault(); if (item is null) break;
            var seq = item.Number("seq"); var id = item.Required("reminder_id"); var payload = J.Parse(item.Text("payload")) as JsonObject ?? new();
            // Upgrade already-queued completions before touching the network.
            // Their history identity must survive retries just like new edits.
            if (payload.Flag("completed") && payload.Text("completion_id") is null)
            {
                payload["completion_id"] = "Reminder/" + Guid.NewGuid().ToString().ToUpperInvariant();
                cache.Execute("UPDATE outbox SET payload=@p0 WHERE seq=@p1", payload.ToJsonString(), seq);
            }
            try
            {
                JsonObject remote;
                if (item.Text("op") == "create") remote = await WithClient(() => client.Create(payload, token), token);
                else if (item.Text("op") == "tag") remote = await WithClient(() => client.AddTag(id, payload, item.Text("base_tag"), token), token);
                else if (item.Text("op") is "update" or "delete") remote = await WithClient(() => client.Update(id, item.Text("op") == "delete" ? new() { ["deleted"] = true } : payload, item.Text("base_tag"), token), token);
                else { cache.Execute("DELETE FROM outbox WHERE seq=@p0", seq); continue; }
                if (item.Text("op") == "create") cache.ReplaceId(id, remote.Required("id"));
                cache.Acknowledge(seq, remote.Required("id"), remote.Text("change_tag")); cache.Upsert(remote); pushed++;
                if (remote["cloud_record"] is JsonObject savedRecord) cache.ApplyRecords(new JsonArray(savedRecord.DeepClone()));
                if (remote["completed_occurrence"] is JsonObject occurrence) cache.Upsert(occurrence);
            }
            catch (CoreException ex) when (ex.Code == "CONFLICT")
            {
                var local = cache.Reminder(id) ?? new(); var remote = J.Parse(ex.Detail) ?? new JsonObject();
                if (remote["recordName"] is not null) remote = ICloudClient.ToReminder(remote) ?? remote;
                cache.Transaction(() =>
                {
                    cache.Execute("INSERT INTO conflicts(reminder_id,local_json,remote_json,detected_at) VALUES(@p0,@p1,@p2,@p3)", id, local.ToJsonString(), remote.ToJsonString(), TimeUtil.Now);
                    cache.Execute("DELETE FROM outbox WHERE seq=@p0", seq);
                    if (remote.Text("id") is not null && cache.Pending(10000).All(p => p.Text("reminder_id") != id)) { cache.Execute("UPDATE reminders SET dirty=0 WHERE id=@p0", id); cache.Upsert(remote); }
                    return true;
                }); conflicts++; emit("conflict", J.Node(new { reminder_id = id }));
            }
            catch (CoreException ex)
            {
                cache.Execute("UPDATE outbox SET attempts=attempts+1,last_error=@p0 WHERE seq=@p1", ex.Message, seq); emit("push_failed", new JsonObject { ["reminder_id"] = id, ["error"] = ex.Body }); throw;
            }
        }
        return J.Node(new { pushed, conflicts, failed = 0 });
    }
}
