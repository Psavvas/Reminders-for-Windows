using Reminders.Windows.Services;

internal static class LatestRequestChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var gate = new LatestRequest(); var applied = new List<int>();
        var slow = new TaskCompletionSource<int>();
        var older = gate.RunAsync(() => slow.Task, applied.Add);
        check(await gate.RunAsync(() => Task.FromResult(2), applied.Add), "a new view can load while an old query is pending");
        slow.SetResult(1);
        check(!await older && applied.SequenceEqual(new[] { 2 }), "a late response never overwrites the more recent view");
        var error = new TaskCompletionSource<int>();
        var obsoleteFailure = gate.RunAsync(() => error.Task, applied.Add);
        await gate.RunAsync(() => Task.FromResult(3), applied.Add);
        error.SetException(new IOException("Old account request failed"));
        check(!await obsoleteFailure && applied.SequenceEqual(new[] { 2, 3 }), "obsolete errors do not replace a newer view with an error banner");
        var signedOut = new TaskCompletionSource<int>(); var stale = gate.RunAsync(() => signedOut.Task, applied.Add);
        gate.Invalidate(); signedOut.SetResult(4);
        check(!await stale && applied.SequenceEqual(new[] { 2, 3 }), "signing out invalidates in-flight data from the old account");
        try { await gate.RunAsync<int>(() => Task.FromException<int>(new IOException("Current failure")), applied.Add); check(false, "current failure must surface"); }
        catch (IOException) { check(true, "the current request still reports real failures"); }
    }
}
