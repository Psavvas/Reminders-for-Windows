using System.Text.Json;
using Reminders.Core;

namespace Reminders.Windows.Services;

public sealed class SyncClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly string dataDirectory;
    private ReminderService? service;
    public SyncClient() : this(DataDirectory) { }
    internal SyncClient(string dataDirectory) { this.dataDirectory = dataDirectory; }
    public event EventHandler<SyncEventArgs>? EventReceived;
    public bool IsRunning => service is not null;
    public bool IsLocalDemo => service?.IsLocalDemo == true;
    public bool IsCurrentEvent(SyncEventArgs args) => ReferenceEquals(service, args.Source);
    private static string DataDirectory
    {
        get
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemindersSync");
#if DEBUG
            if (Environment.GetEnvironmentVariable("REMINDERS_DATA_DIR") is { Length: > 0 } developmentData) directory = Path.GetFullPath(developmentData);
#endif
            return directory;
        }
    }
    private void Attach(ReminderService engine)
    {
        engine.EventReceived += (name, data) => { if (ReferenceEquals(service, engine)) EventReceived?.Invoke(this, new(name, data, engine)); };
        engine.Diagnostic += AppLog.Info;
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (service is not null) return;
            var engine = await Task.Run(() => new ReminderService(dataDirectory), cancellationToken);
            Attach(engine);
            try { service = engine; await engine.StartAsync(cancellationToken); }
            catch { service = null; await engine.DisposeAsync(); throw; }
        }
        finally { lifecycle.Release(); }
    }
    public async Task SignInDemoAsync(string password, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            // Validate before replacing a real session; failed demo logins leave it intact.
            var engine = await Task.Run(() => ReminderService.OpenDemo(dataDirectory, password), cancellationToken);
            try
            {
                var previous = service; service = null;
                if (previous is not null) await previous.DisposeAsync();
                Attach(engine);
                service = engine;
                await engine.StartAsync(cancellationToken);
            }
            catch { service = null; await engine.DisposeAsync(); throw; }
        }
        catch (CoreException error) { throw new SyncException(error.Code, error.Message, error.Detail); }
        finally { lifecycle.Release(); }
    }
    public async Task<T> CallAsync<T>(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        (await CallAsync(method, parameters, cancellationToken)).Deserialize<T>(Json) ?? throw new InvalidDataException($"The {method} response was empty.");
    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        var engine = service ?? throw new SyncException("SYNC_DOWN", "The sync service is not running.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            // SQLite and cryptography execute away from the WinUI dispatcher.
            return await Task.Run(() => engine.CallAsync(method, JsonSerializer.SerializeToElement(parameters ?? new { }, Json), deadline.Token), deadline.Token);
        }
        catch (CoreException error)
        {
            // Parameters may contain passwords and verification codes.
            AppLog.Info($"call '{method}' failed: {error.Code}: {error.Message}");
            throw new SyncException(error.Code, error.Message, error.Detail);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try { var engine = service; service = null; if (engine is not null) await engine.DisposeAsync(); }
        finally { lifecycle.Release(); }
    }
}

public sealed record SyncEventArgs(string Name, JsonElement Data, ReminderService Source);
internal static class JsonHelpers
{
    public static string Text(this JsonElement value, string name, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() ?? fallback : fallback;
    public static bool Flag(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.True;
    public static long Number(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node) && node.TryGetInt64(out var number) ? number : 0;
    public static JsonElement Property(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node) ? node : default;
}
