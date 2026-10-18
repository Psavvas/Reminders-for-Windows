using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json.Nodes;
using Windows.Storage.Streams;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private async void BrowserSignIn_Click(object sender, RoutedEventArgs args)
    {
        if (_dialogOpen || _verificationBusy) return;
        _dialogOpen = true;
        var browser = new WebView2 { Width = Math.Clamp(Root.ActualWidth - 100, 320, 850), Height = Math.Clamp(Root.ActualHeight - 210, 300, 620) };
        var message = new TextBlock { Text = "Sign in on Apple's page, verify with your Apple device, then choose Trust. Your session will be remembered on this PC.", TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(message); panel.Children.Add(browser);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Apple sign-in · icloud.com", Content = panel, CloseButtonText = "Cancel" };
        dialog.Resources["ContentDialogMaxWidth"] = 1000d;
        var importing = false; var finished = false; var closed = false;
        var authHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Task import = Task.CompletedTask;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemindersForWindows", "AppleSignIn");
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions());
                var options = environment.CreateCoreWebView2ControllerOptions(); options.ProfileName = "AppleSignIn"; options.IsInPrivateModeEnabled = true;
                await browser.EnsureCoreWebView2Async(environment, options);
                if (closed) return;
                var core = browser.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
                core.NavigationStarting += (_, e) =>
                {
                    if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !AppleSignInHost(uri)) e.Cancel = true;
                };
                core.NewWindowRequested += (_, e) => { e.Handled = true; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && AppleSignInHost(uri)) core.Navigate(e.Uri); };
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.WebResourceResponseReceived += (_, e) =>
                {
                    if (closed || finished || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
                    if (uri.Host == "idmsa.apple.com" && uri.AbsolutePath.StartsWith("/appleauth/auth", StringComparison.Ordinal))
                        foreach (var header in new[] { "X-Apple-TwoSV-Trust-Token", "X-Apple-Session-Token" })
                            if (e.Response.Headers.Contains(header)) authHeaders[header] = e.Response.Headers.GetHeader(header);
                    if (importing || e.Response.StatusCode != 200 || uri.Host != "setup.icloud.com" || uri.AbsolutePath != "/setup/ws/1/accountLogin") return;
                    importing = true;
                    import = ImportAsync(e);
                };
                async Task ImportAsync(CoreWebView2WebResourceResponseReceivedEventArgs e)
                {
                    try
                    {
                        var response = JsonNode.Parse(await ReadSignInStream(await e.Response.GetContentAsync()));
                        if (response is null || response["hsaTrustedBrowser"]?.ToString() != "true" || response["hsaChallengeRequired"]?.ToString() == "true") return;
                        var request = JsonNode.Parse(await ReadSignInStream(e.Request.Content));
                        if (request?["dsWebAuthToken"] is null) return;
                        var account = response["dsInfo"]?["appleId"]?.GetValue<string>() ?? response["dsInfo"]?["primaryEmail"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(account)) return;
                        var cookies = await core.CookieManager.GetCookiesAsync("");
                        var state = new JsonObject
                        {
                            ["session_token"] = request["dsWebAuthToken"]!.DeepClone(), ["trust_token"] = authHeaders.TryGetValue("X-Apple-TwoSV-Trust-Token", out var trust) ? JsonValue.Create(trust) : request["trustToken"]?.DeepClone(),
                            ["account_country"] = request["accountCountryCode"]?.DeepClone(),
                            ["client_id"] = new Uri(e.Request.Uri).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "clientId") is { Length: 2 } pair ? Uri.UnescapeDataString(pair[1]) : Guid.NewGuid().ToString(),
                            ["cookies"] = JsonSerializerNode(cookies)
                        };
                        if (closed) return;
                        message.Text = "Saving your trusted Apple session…";
                        dialog.IsEnabled = false;
                        await _sync.CallAsync("browser_login", new { apple_id = account, state });
                        finished = true;
                        PasswordBox.Password = ""; CodeBox.Text = "";
                        dialog.Hide();
                    }
                    catch (Exception)
                    {
                        // Browser request/response bodies contain credentials.
                        // Never include them, or exception details, in logs/UI.
                        if (!closed) { message.Text = "Could not save this Apple session. Finish verification and choose Trust, or cancel and use text verification."; dialog.IsEnabled = true; }
                    }
                    finally { importing = false; }
                }
                core.Navigate("https://www.icloud.com/reminders/");
            }
            catch (Exception) { if (!closed) message.Text = "Apple sign-in could not open. Check that Microsoft Edge WebView2 Runtime is installed, or use text verification."; }
        };
        try { await dialog.ShowAsync(); }
        finally
        {
            closed = true;
            await import;
            browser.Close();
            _dialogOpen = false;
        }
        if (finished) { _lastSignInFailure = null; await BootAsync(); }
    }

    private static bool AppleSignInHost(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
        (uri.Host == "icloud.com" || uri.Host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "apple.com" || uri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase));

    private static JsonArray JsonSerializerNode(IEnumerable<CoreWebView2Cookie> cookies) => new(cookies
        .Where(c => AppleSignInHost(new Uri("https://" + c.Domain.TrimStart('.'))))
        .Select(c => new JsonObject { ["name"] = c.Name, ["value"] = c.Value, ["domain"] = c.Domain, ["path"] = c.Path, ["http_only"] = c.IsHttpOnly,
            ["expires"] = c.IsSession ? 0 : (long)c.Expires }).Cast<JsonNode>().ToArray());

    private static async Task<string> ReadSignInStream(IRandomAccessStream stream)
    {
        using var input = stream.GetInputStreamAt(0).AsStreamForRead();
        using var reader = new StreamReader(input);
        var buffer = new char[2 * 1024 * 1024 + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory());
        if (length == buffer.Length) throw new InvalidDataException("Apple sign-in response exceeded the size limit.");
        return new string(buffer, 0, length);
    }
}
