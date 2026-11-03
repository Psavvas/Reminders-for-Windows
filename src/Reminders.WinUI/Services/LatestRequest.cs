namespace Reminders.Windows.Services;

public sealed class LatestRequest
{
    private long generation;
    public void Invalidate() => Interlocked.Increment(ref generation);

    public async Task<bool> RunAsync<T>(Func<Task<T>> fetch, Action<T> apply)
    {
        var ticket = Interlocked.Increment(ref generation);
        try
        {
            var result = await fetch();
            if (ticket != Interlocked.Read(ref generation)) return false;
            apply(result); return true;
        }
        catch when (ticket != Interlocked.Read(ref generation)) { return false; }
    }
}
