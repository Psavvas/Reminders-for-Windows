using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Reminders.Windows.Services;
using System.Text.Json.Nodes;
using Windows.Storage.Streams;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private async void BrowserSignIn_Click(object sender, RoutedEventArgs args)
    {
        if (_dialogOpen || _verificationBusy) return;
        _dialogOpen = true;
        using var lifetime = new CancellationTokenSource();
        var host = new Grid { Width = Math.Clamp(Root.ActualWidth - 100, 320, 850), Height = Math.Clamp(Root.ActualHeight - 210, 300, 620) };
        var browser = new WebView2(); host.Children.Add(browser);
        var views = new List<WebView2> { browser };
        var parents = new Dictionary<WebView2, WebView2>();
        var pendingPopups = new List<Task>();
        var message = new TextBlock { Text = AppleSignInPolicy.Instructions, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(message); panel.Children.Add(host);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Apple sign-in · icloud.com", Content = panel,
            CloseButtonText = "Cancel", PrimaryButtonText = "Continue in app", IsPrimaryButtonEnabled = false,
            SecondaryButtonText = "Reload", IsSecondaryButtonEnabled = false };
        dialog.Resources["ContentDialogMaxWidth"] = 1000d;
#if DEBUG
        if (AppleSignInPreview.Enabled) dialog.Title = "Account-free popup test";
#endif
        var importing = false; var finished = false; var closed = false; var remindersObserved = false;
        var authHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Task import = Task.CompletedTask; Task initialization = Task.CompletedTask;
        CoreWebView2Environment? environment = null;
        CoreWebView2ControllerOptions? options = null;

        void NavigateStart()
        {
#if DEBUG
            if (AppleSignInPreview.Enabled) { browser.CoreWebView2.NavigateToString(AppleSignInPreview.Html); return; }
#endif
            browser.CoreWebView2.Navigate(AppleSignInPolicy.StartUrl);
        }

        void ShowFailure(AppleSignInFailure failure)
        {
            AppLog.Info(failure.Diagnostic);
            if (!closed && !finished) message.Text = failure.Message;
        }

        void ReturnToParent(WebView2 view)
        {
            if (!parents.Remove(view, out var parent)) return;
            host.Children.Remove(view); views.Remove(view); view.Close();
            parent.Visibility = Visibility.Visible;
            dialog.SecondaryButtonText = parents.ContainsKey(parent) ? "Back" : "Reload";
        }

        void CheckNavigation(CoreWebView2NavigationCompletedEventArgs e)
        {
            if (closed || finished || e.IsSuccess || e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted) return;
            ShowFailure(new("Apple's sign-in page could not load. Reload to try again, or cancel and use text verification.",
                $"apple sign-in: stage=navigation error={e.WebErrorStatus} HTTP {e.HttpStatusCode}"));
        }

        void Configure(WebView2 view)
        {
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
#if DEBUG
                // NavigateToString uses a generated URI. This fixture contains
                // only local markup and a blank child; there are no remote links.
                if (AppleSignInPreview.Enabled) return;
#endif
                if (e.Uri != "about:blank" && (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !AppleSignInPolicy.IsAppleUri(uri)))
                {
                    e.Cancel = true;
                    ShowFailure(new("This page cannot open in Apple sign-in. Return to the sign-in page or cancel and use text verification.", "apple sign-in: stage=navigation blocked_destination"));
                }
            };
            core.NavigationCompleted += (_, e) => CheckNavigation(e);
            core.FrameCreated += (_, e) => e.Frame.NavigationCompleted += (_, completion) => CheckNavigation(completion);
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (closed || importing || !AppleSignInPolicy.IsPopupUri(e.Uri)) return;
                var deferral = e.GetDeferral();
                pendingPopups.Add(OpenPopupAsync(view, e, deferral));
            };
            core.WindowCloseRequested += (_, _) => ReturnToParent(view);
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebResourceResponseReceived += (_, e) =>
            {
                if (closed || finished || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !AppleSignInPolicy.IsAppleUri(uri)) return;
                if (AppleSignInPolicy.HttpFailure(uri, e.Response.StatusCode) is { } failure) ShowFailure(failure);
                else if (AppleSignInPolicy.Stage(uri) is { } stage) AppLog.Info($"apple sign-in: stage={stage} endpoint={AppleSignInPolicy.AccountEndpoint(uri)} HTTP {e.Response.StatusCode}");
                if (AppleSignInPolicy.Stage(uri) == "authentication")
                    foreach (var header in new[] { "X-Apple-TwoSV-Trust-Token", "X-Apple-Session-Token" })
                        if (e.Response.Headers.Contains(header)) authHeaders[header] = e.Response.Headers.GetHeader(header);
                // A usable Reminders page may be restored without a fresh
                // account-login response. Validate its cookies independently.
                if (!remindersObserved && AppleSignInPolicy.IsRemindersResponse(uri, e.Response.StatusCode))
                {
                    remindersObserved = true;
                    AppLog.Info("apple sign-in: stage=reminders session_detected");
                    import = QueueCookieImportAsync(import, core);
                }
                if (e.Response.StatusCode != 200 || uri.Host != "setup.icloud.com" || AppleSignInPolicy.AccountEndpoint(uri) is not ("accountLogin" or "validate" or "loginOrCreateAccount")) return;
                // Do not drop a successful validation while an earlier account
                // response is being read. Trust may change between responses.
                import = QueueImportAsync(import, core, e);
            };
        }

        async Task OpenPopupAsync(WebView2 parent, CoreWebView2NewWindowRequestedEventArgs e, global::Windows.Foundation.Deferral deferral)
        {
            WebView2? child = null;
            try
            {
                child = new WebView2(); views.Add(child); host.Children.Add(child);
                await child.EnsureCoreWebView2Async(environment!, options!);
                if (closed) return;
                Configure(child);
                // Assign the child to the request, rather than navigating the
                // parent. This preserves window.opener and Apple's messages.
                e.NewWindow = child.CoreWebView2;
                parents.Add(child, parent); parent.Visibility = Visibility.Collapsed;
                dialog.SecondaryButtonText = "Back";
                AppLog.Info("apple sign-in: stage=popup opened");
            }
            catch (Exception error)
            {
                if (child is not null) { host.Children.Remove(child); views.Remove(child); child.Close(); }
                ShowFailure(new("Apple's sign-in window could not open. Reload and try again, or cancel and use text verification.",
                    $"apple sign-in: stage=popup type={error.GetType().Name} hresult=0x{error.HResult:X8}"));
            }
            finally { deferral.Complete(); }
        }

        async Task QueueImportAsync(Task previous, CoreWebView2 core, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            await previous;
            if (!closed && !finished) await ImportAsync(core, e);
        }

        async Task QueueCookieImportAsync(Task previous, CoreWebView2 core)
        {
            await previous;
            if (!closed && !finished) await ConnectAsync(core, null, null, null);
        }

        async Task ImportAsync(CoreWebView2 core, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                var response = JsonNode.Parse(await ReadSignInStream(await e.Response.GetContentAsync()));
                var eligible = AppleSignInPolicy.CanImportAccount(response);
                AppLog.Info($"apple sign-in: stage=account_response eligible={eligible} account_present={response?["dsInfo"] is JsonObject} services_present={response?["webservices"] is JsonObject}");
                if (!eligible || response is null) return;
                var request = e.Request.Content is { } content ? JsonNode.Parse(await ReadSignInStream(content)) : null;
                var account = response["dsInfo"]?["appleId"]?.GetValue<string>() ?? response["dsInfo"]?["primaryEmail"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(account)) throw new InvalidDataException();
                var clientId = new Uri(e.Request.Uri).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "clientId") is { Length: 2 } pair ? Uri.UnescapeDataString(pair[1]) : null;
                await ConnectAsync(core, account, request, clientId);
            }
            catch (OperationCanceledException) when (closed) { }
            catch (Exception error) { ShowFailure(AppleSignInPolicy.ImportFailure(error)); }
        }

        async Task ConnectAsync(CoreWebView2 core, string? account, JsonNode? request, string? clientId)
        {
            if (closed || finished) return;
            try
            {
                importing = true;
                message.Text = "Connecting your Apple session to the app…";
                dialog.IsPrimaryButtonEnabled = false; dialog.IsSecondaryButtonEnabled = false;
                AppLog.Info("apple sign-in: stage=import started");
                var cookies = await core.CookieManager.GetCookiesAsync("");
                var state = new JsonObject
                {
                    ["session_token"] = request?["dsWebAuthToken"]?.DeepClone() ?? (authHeaders.TryGetValue("X-Apple-Session-Token", out var session) ? JsonValue.Create(session) : null),
                    ["trust_token"] = authHeaders.TryGetValue("X-Apple-TwoSV-Trust-Token", out var trust) ? JsonValue.Create(trust) : request?["trustToken"]?.DeepClone(),
                    ["account_country"] = request?["accountCountryCode"]?.DeepClone(),
                    ["browser_cookie_session"] = true, ["user_agent"] = core.Settings.UserAgent,
                    ["client_id"] = clientId ?? Guid.NewGuid().ToString(),
                    ["cookies"] = JsonSerializerNode(cookies)
                };
                if (closed) return;
                await _sync.CallAsync("browser_login", new { apple_id = account, state }, lifetime.Token);
                finished = true;
                AppLog.Info("apple sign-in: stage=import completed");
                PasswordBox.Password = ""; CodeBox.Text = "";
                dialog.Hide();
            }
            catch (OperationCanceledException) when (closed) { }
            catch (Exception error) { ShowFailure(AppleSignInPolicy.ImportFailure(error)); }
            finally
            {
                importing = false;
                if (!closed && !finished) { dialog.IsPrimaryButtonEnabled = true; dialog.IsSecondaryButtonEnabled = true; }
            }
        }

        async Task InitializeAsync()
        {
            try
            {
                // The backend must be ready even when this is opened before a
                // native password sign-in (or after a previous startup failure).
#if DEBUG
                if (!AppleSignInPreview.Enabled)
#endif
                await _sync.StartAsync(lifetime.Token);
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemindersForWindows", "AppleSignIn");
#if DEBUG
                if (AppleSignInPreview.Enabled && Environment.GetEnvironmentVariable("REMINDERS_DATA_DIR") is { Length: > 0 } previewData)
                    folder = Path.Combine(previewData, "WebView2");
#endif
                environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions());
                if (closed) return;
                options = environment.CreateCoreWebView2ControllerOptions(); options.ProfileName = "AppleSignIn"; options.IsInPrivateModeEnabled = true;
                await browser.EnsureCoreWebView2Async(environment, options);
                if (closed) return;
                Configure(browser);
                dialog.IsPrimaryButtonEnabled = true; dialog.IsSecondaryButtonEnabled = true;
                AppLog.Info("apple sign-in: stage=page opening handoff=2");
                NavigateStart();
            }
            catch (OperationCanceledException) when (closed) { }
            catch (Exception error)
            {
                ShowFailure(new("Apple sign-in could not open. Check Microsoft Edge WebView2 Runtime, or cancel and use text verification.",
                    $"apple sign-in: stage=initialization type={error.GetType().Name} hresult=0x{error.HResult:X8}"));
            }
        }

        dialog.Opened += (_, _) => initialization = InitializeAsync();
        dialog.Closing += (_, _) => { closed = true; lifetime.Cancel(); };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            e.Cancel = true;
            if (importing || closed || finished) return;
            var current = views.LastOrDefault(v => v.Visibility == Visibility.Visible);
            if (current?.CoreWebView2 is not { } core) return;
            AppLog.Info("apple sign-in: stage=import manual_continue");
            import = QueueCookieImportAsync(import, core);
        };
        dialog.SecondaryButtonClick += (_, e) =>
        {
            e.Cancel = true;
            var current = views.LastOrDefault(v => v.Visibility == Visibility.Visible);
            if (current is not null && parents.ContainsKey(current)) { ReturnToParent(current); return; }
            authHeaders.Clear(); remindersObserved = false; message.Text = AppleSignInPolicy.Instructions;
            AppLog.Info("apple sign-in: stage=page reload");
            NavigateStart();
        };
        try { await dialog.ShowAsync(); }
        finally
        {
            closed = true; lifetime.Cancel();
            try { await initialization; await Task.WhenAll(pendingPopups); await import; }
            finally
            {
                _dialogOpen = false;
                foreach (var view in views.ToArray()) view.Close();
            }
        }
        if (finished) { _lastSignInFailure = null; await BootAsync(); }
    }

    private static JsonArray JsonSerializerNode(IEnumerable<CoreWebView2Cookie> cookies) => new(cookies
        .Where(c => AppleSignInPolicy.IsAppleUri(new Uri("https://" + c.Domain.TrimStart('.'))))
        .Select(c => new JsonObject { ["name"] = c.Name, ["value"] = c.Value, ["domain"] = c.Domain, ["path"] = c.Path, ["http_only"] = c.IsHttpOnly,
            ["expires"] = c.IsSession ? 0 : (long)c.Expires }).Cast<JsonNode>().ToArray());

    private static async Task<string> ReadSignInStream(IRandomAccessStream? stream)
    {
        if (stream is null) throw new InvalidDataException();
        using var input = stream.GetInputStreamAt(0).AsStreamForRead();
        using var reader = new StreamReader(input);
        var buffer = new char[2 * 1024 * 1024 + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory());
        if (length == buffer.Length) throw new InvalidDataException();
        return new string(buffer, 0, length);
    }
}
