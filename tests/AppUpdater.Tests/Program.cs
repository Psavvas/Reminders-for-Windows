using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Reminders.Core;
using Reminders.Windows.Services;

var payload = new byte[] { 1, 2, 3, 4 };
var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
var current = new Version(0, 3, 0, 0);
var checks = 0;
JsonElement Release(string tag = "v0.4.0", string arch = "x64", bool draft = false, bool prerelease = false, string? checksum = null, string host = "github.com", long size = 4) =>
    JsonSerializer.SerializeToElement(new
    {
        tag_name = tag, draft, prerelease,
        assets = new[] { new { name = $"Reminders-for-Windows-{arch}-Setup.exe", browser_download_url = $"https://{host}/{AppUpdater.Repository}/releases/download/{tag}/Reminders-for-Windows-{arch}-Setup.exe", digest = checksum ?? "sha256:" + digest, size } }
    });
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name); checks++;
}
void Reject(JsonElement release, string name)
{
    try { AppUpdater.ParseRelease(release, current, Architecture.X64); }
    catch (InvalidDataException) { Assert(true, name); return; }
    throw new Exception("FAILED: " + name);
}
Assert(AppUpdater.ParseRelease(Release(), current, Architecture.X64)?.Version == new Version(0, 4, 0), "newer stable release");
var renamedRepositoryRelease = JsonSerializer.SerializeToElement(new
{
    tag_name = "v0.5.0", draft = false, prerelease = false,
    assets = new[] { new { name = "Reminders-for-Windows-x64-Setup.exe", browser_download_url = "https://github.com/Psavvas/Reminders-for-Windows/releases/download/v0.5.0/Reminders-for-Windows-x64-Setup.exe", digest = "sha256:" + digest, size = payload.Length } }
});
Assert(AppUpdater.ParseRelease(renamedRepositoryRelease, new Version(0, 4, 3), Architecture.X64)?.Version == new Version(0, 5, 0), "accept actual release URLs after repository rename");
Assert(AppUpdater.ParseRelease(Release(arch: "arm64"), current, Architecture.Arm64) is not null, "ARM64 asset selection");
Assert(AppUpdater.ParseRelease(Release(tag: "v0.3.0"), current, Architecture.X64) is null, "three and four part versions compare equally");
Assert(AppUpdater.ParseRelease(Release(tag: "v0.2.0"), current, Architecture.X64) is null, "no downgrades");
Assert(AppUpdater.ParseRelease(Release(draft: true), current, Architecture.X64) is null, "ignore drafts");
Assert(AppUpdater.ParseRelease(Release(prerelease: true), current, Architecture.X64) is null, "ignore prereleases");
Assert(AppUpdater.ParseRelease(Release(tag: "v0.4.0-beta"), current, Architecture.X64) is null, "ignore nonstable tags");
Reject(Release(arch: "arm64"), "reject missing architecture");
try { AppUpdater.ParseRelease(Release(arch: "arm64"), current, Architecture.X64); }
catch (InvalidDataException error)
{
    Assert(error.GetPresentation() is { Key: "ErrorUpdateInstallerMissing", Arguments: ["x64"] },
        "missing installer exposes a resource key and architecture separately");
}
Reject(Release(host: "example.com"), "reject foreign download host");
Reject(Release(checksum: ""), "reject missing checksum");
Reject(Release(checksum: "sha256:" + new string('z', 64)), "reject invalid checksum");
Reject(Release(size: 0), "reject empty installer");
Reject(Release(size: 2L * 1024 * 1024 * 1024), "reject oversized installer");
using (var foreign = JsonDocument.Parse(Release().GetRawText().Replace(AppUpdater.Repository, "other/repository")))
    Reject(foreign.RootElement, "reject foreign repository on GitHub");

var directory = Path.Combine(Path.GetTempPath(), "RemindersUpdaterTests-" + Guid.NewGuid().ToString("N"));
try
{
    using var client = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
    var updater = new AppUpdater(client, directory);
    var update = AppUpdater.ParseRelease(Release(), current, Architecture.X64)!;
    var path = await updater.DownloadAsync(update, new Progress<double>(), CancellationToken.None);
    Assert(File.ReadAllBytes(path).SequenceEqual(payload), "verified download saved");
    File.Delete(path);
    foreach (var invalid in new[] { update with { Sha256 = new string('0', 64) }, update with { Size = 3 }, update with { Size = 5 } })
    {
        try { await updater.DownloadAsync(invalid, new Progress<double>(), CancellationToken.None); throw new Exception("Bad download accepted"); }
        catch (InvalidDataException) { Assert(!Directory.EnumerateFiles(directory).Any(), "reject corrupt or wrong-size download and clean up"); }
    }
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await updater.DownloadAsync(update, new Progress<double>(), cancellation.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Assert(!Directory.EnumerateFiles(directory).Any(), "cancel download and clean up"); }
    using var missing = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
    Assert(await new AppUpdater(missing, directory).CheckAsync(CancellationToken.None) is null, "no published releases is normal");
    using var releaseClient = new HttpClient(new FakeHandler(request =>
    {
        Assert(request.RequestUri!.AbsoluteUri == $"https://api.github.com/repos/{AppUpdater.Repository}/releases/latest", "check the configured repository");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release(tag: $"v{AppUpdater.CurrentVersion.Major + 1}.0.0", arch: RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64").GetRawText()) };
    }));
    Assert(await new AppUpdater(releaseClient, directory).CheckAsync(CancellationToken.None) is not null, "parse API response end to end");
    using var limited = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
    try { await new AppUpdater(limited, directory).CheckAsync(CancellationToken.None); throw new Exception("API failure ignored"); }
    catch (HttpRequestException) { Assert(true, "surface API errors"); }
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
Console.WriteLine($"{checks} updater checks passed.");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
