using Reminders.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Reminders.Windows.Services;

internal sealed record AppUpdate(Version Version, Uri DownloadUri, string Sha256, long Size);

internal sealed class AppUpdater
{
    internal const string Repository = "Psavvas/Reminders-for-Windows";
    private const long MaximumInstallerSize = 1024L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private readonly HttpClient _client;
    private readonly string _downloadDirectory;
    public AppUpdater(HttpClient? client = null, string? downloadDirectory = null)
    {
        _client = client ?? Client;
        _downloadDirectory = downloadDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemindersForWindows", "Updates");
    }
    public static Version CurrentVersion => typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);
    // Only the Inno installer writes this marker. Development, portable and MSIX
    // builds must not silently acquire an unrelated Inno installation.
    public static bool CanInstall => File.Exists(Path.Combine(AppContext.BaseDirectory, "installer-installation.txt"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Reminders-for-Windows/" + CurrentVersion);
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return ParseRelease(document.RootElement, CurrentVersion, RuntimeInformation.ProcessArchitecture);
    }

    internal static AppUpdate? ParseRelease(JsonElement release, Version current, Architecture architecture)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || Normalize(version) <= Normalize(current)) return null;
        var arch = architecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new NotSupportedException("Unsupported update architecture.").WithPresentation("ErrorUnsupportedUpdateArchitecture") };
        var name = $"Reminders-for-Windows-{arch}-Setup.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            var expectedPath = $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
            if (uri.Scheme != "https" || uri.Host != "github.com" || uri.AbsolutePath != expectedPath || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("The update download is not a release asset from this repository.").WithPresentation("ErrorUpdateAssetSource");
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
                throw new InvalidDataException("This release has no valid SHA-256 checksum. Download it manually from GitHub.").WithPresentation("ErrorUpdateChecksumMissing");
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumInstallerSize) throw new InvalidDataException("The update installer size is invalid.").WithPresentation("ErrorUpdateInstallerSize");
            return new AppUpdate(version, uri, digest[7..], size);
        }
        throw new InvalidDataException($"This release does not include the {arch} installer yet.").WithPresentation("ErrorUpdateInstallerMissing", arch);
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public async Task<string> DownloadAsync(AppUpdate update, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var directory = _downloadDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Reminders-{update.Version}-{Guid.NewGuid():N}-Setup.exe");
        try
        {
            using var response = await _client.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                var lastPercent = -1;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > update.Size) throw new InvalidDataException("The downloaded update exceeds its expected size.").WithPresentation("ErrorUpdateSizeExceeded");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    var percent = (int)(100.0 * total / update.Size);
                    if (percent != lastPercent) { progress.Report(percent); lastPercent = percent; }
                }
                if (total != update.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                    throw new InvalidDataException("Update verification failed. The installer was not run.").WithPresentation("ErrorUpdateVerificationFailed");
            }
            return path;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public static void LaunchInstaller(string path)
    {
        if (!CanInstall) throw new InvalidOperationException("Automatic installation is available for installer builds only.").WithPresentation("ErrorUpdateInstallationUnavailable");
        var start = new ProcessStartInfo(path) { UseShellExecute = true };
        // Keep installer error dialogs visible if an upgrade cannot complete.
        start.Arguments = $"/SILENT /NORESTART /NORESTARTAPPLICATIONS /UPDATE /DIR=\"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\"";
        _ = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the update installer.").WithPresentation("ErrorUpdateInstallerLaunch");
    }
}
