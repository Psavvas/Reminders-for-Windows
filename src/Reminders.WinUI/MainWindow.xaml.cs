using Microsoft.UI.Windowing;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Reminders.Windows.Services;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Reminders.Windows;

public sealed partial class MainWindow : Window
{
    private SyncClient _sync = new();
    private readonly ObservableCollection<ReminderItem> _reminders = [];
    private readonly Microsoft.UI.Xaml.Data.CollectionViewSource _sectionView = new() { IsSourceGrouped = true };
    private string _sectionSignature = "";
    private bool _regrouping;
    private DateTime _reminderDate = DateTime.Today;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _paneHoverOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly DispatcherTimer _paneHoverCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private List<ReminderList> _lists = [];
    private List<string> _tags = [];
    private JsonElement _settings;
    private UiPreferenceData _uiPreferences = UiPreferences.Load();
    private bool _restoringNavigation;
    private bool _dialogOpen;
    private readonly HashSet<string> _completing = [];
    private readonly List<ReminderItem> _demoRows = [];
    private ReminderItem? _selected;
    private NavEntry _view = new("Today", "", NavKind.Smart, "today");
    private string _sort = "manual";
    private bool _verificationBusy;
    private bool _loadingDetail;
    private bool _demo;
    private DateTimeOffset _lastSyncRequest = DateTimeOffset.MinValue;
    private bool _bootStarted;
    private bool _notificationsRegistered;
    private bool _panePinnedOpen;
    private bool _hoverExpanded;
    private bool? _temporaryPaneTarget;
    private bool _initializingPane = true;
    private readonly AppUpdater _updater = new();
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private AppUpdate? _availableUpdate;
    private bool _updateBusy;
    private bool _syncDisposedForUpdate;

    public MainWindow()
    {
        InitializeComponent();
        var settingsAccelerator = new KeyboardAccelerator { Key = (global::Windows.System.VirtualKey)188, Modifiers = global::Windows.System.VirtualKeyModifiers.Control };
        settingsAccelerator.Invoked += SettingsAccelerator_Invoked;
        Root.KeyboardAccelerators.Add(settingsAccelerator);
        Navigation.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Navigation_PointerMoved), true);
        Navigation.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Navigation_PointerExited), true);
        Navigation.Loaded += Navigation_Loaded;
        foreach (var item in Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>()) AttachPaneHover(item);
        ReminderList.ItemsSource = _reminders;
        DetailPriority.SelectedIndex = 0;
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await LoadRemindersAsync(); };
        _statusTimer.Tick += async (_, _) =>
        {
            foreach (var reminder in _reminders) reminder.RefreshTimeMetadata();
            if (_completing.Count == 0)
            {
                if (_reminderDate != DateTime.Today) await LoadRemindersAsync();
                else RebuildReminderSections();
            }
            await RefreshStatusAsync();
        };
        _notificationTimer.Tick += async (_, _) => await CheckNotificationsAsync();
        _syncTimer.Tick += async (_, _) => await BackgroundSyncAsync();
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        _paneHoverOpenTimer.Tick += (_, _) => OpenPaneForHover();
        _paneHoverCloseTimer.Tick += (_, _) => CloseHoverPane();
        AttachSyncEvents();
        SetWindowSize();
        SetPanePinned(UiPreferences.LoadNavigationPaneOpen(), persist: false);
        _initializingPane = false;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        try { AppNotificationManager.Default.Register(); _notificationsRegistered = true; } catch { }
    }

    private void SetWindowSize()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(id);
        appWindow.Resize(new SizeInt32(1280, 800));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "icon-v2.ico");
        if (File.Exists(icon)) appWindow.SetIcon(icon);
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_syncDisposedForUpdate) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        if (_bootStarted)
        {
            if (SetupGuide.Visibility == Visibility.Visible) return;
            if (AuthGate.Visibility == Visibility.Collapsed && DateTimeOffset.UtcNow - _lastSyncRequest >= TimeSpan.FromSeconds(30))
                await RequestAutoSyncAsync();
            return;
        }
        _bootStarted = true;
#if DEBUG
        // Offline UI verification of the failure/recovery flow, using an
        // isolated REMINDERS_DATA_DIR. Never compiled into release builds.
        if (Environment.GetCommandLineArgs().Contains("--preview-sign-in-error", StringComparer.OrdinalIgnoreCase))
        {
            ShowLogin();
            ShowSignInError(new SyncException("NETWORK", "Apple accepted the verification code, but iCloud account sign-in failed (HTTP 500).", "HTTP 500: preview"), SignInStage.Verification);
            return;
        }
#endif
        if (Environment.GetCommandLineArgs().Contains("--demo", StringComparer.OrdinalIgnoreCase) || Environment.GetEnvironmentVariable("REMINDERS_DEMO") == "1")
        {
            LoadDemo();
            return;
        }
        if (!_uiPreferences.ICloudSetupCompleted) { ShowSetupGuide(firstRun: true); return; }
        await StartAccountAsync();
    }

    private async Task StartAccountAsync()
    {
        await BootAsync();
        _updateTimer.Start();
        await CheckForUpdatesAsync(false);
    }

    private async Task BootAsync()
    {
        SetGateState("Starting the sync service…");
        try
        {
            if (!_sync.IsRunning) await _sync.StartAsync();
            SetGateState("Checking your account…");
            var status = await _sync.CallAsync("auth_status", new { });
            _settings = await _sync.CallAsync("settings", new { });
            ApplyTheme(_settings.Text("theme", "system"));
            if (status.Flag("needs_2fa") && !status.Flag("authenticated"))
            {
                // Apple has a code outstanding. Asking for the password again is
                // both wrong and destructive: signing in afresh makes Apple mint
                // a new code and retire the one already sent.
                await ShowCodeEntryAsync(status.Property("two_factor"));
                return;
            }
            if (status.Flag("authenticated") || status.Flag("has_cache"))
            {
                AuthGate.Visibility = Visibility.Collapsed;
                await RefreshAllAsync();
                _statusTimer.Start(); _notificationTimer.Start(); _syncTimer.Start();
                if (status.Flag("authenticated")) await RequestAutoSyncAsync();
                if (!status.Flag("authenticated") && !status.Flag("restoring")) ShowInfo("Sign in to resume syncing", "Cached reminders and queued edits remain available.", InfoBarSeverity.Warning);
                return;
            }
            if (status.Flag("restoring")) { SetGateState("Signing you back in…"); return; }
            ShowLogin();
        }
        catch (Exception error)
        {
            SetGateState("The sync service isn't available.", false);
            GateError.Message = error.Message; GateError.IsOpen = true;
        }
    }

    private void SetGateState(string status, bool progress = true)
    {
        AuthGate.Visibility = Visibility.Visible; GateStatus.Text = status; GateProgress.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        LoginFields.Visibility = Visibility.Collapsed; CodeFields.Visibility = Visibility.Collapsed; GateError.IsOpen = false;
        SetupHelpLink.IsEnabled = !progress;
    }
    private void ShowLogin()
    {
        AuthGate.Visibility = Visibility.Visible; GateProgress.Visibility = Visibility.Collapsed; GateStatus.Text = "Sign in to iCloud"; LoginFields.Visibility = Visibility.Visible; CodeFields.Visibility = Visibility.Collapsed;
        AppleIdBox.Focus(FocusState.Programmatic);
        SetupHelpLink.IsEnabled = true;
    }

    private async Task RefreshAllAsync()
    {
        await LoadNavigationAsync();
        await LoadRemindersAsync();
        await RefreshStatusAsync();
    }

    private async Task LoadNavigationAsync()
    {
        if (_demo) return;
        var listsTask = _sync.CallAsync<List<ReminderList>>("lists", new { });
        var tagsTask = _sync.CallAsync<List<ReminderTag>>("tags", new { });
        var countsTask = _sync.CallAsync("smart_counts", new { });
        await Task.WhenAll(listsTask, tagsTask, countsTask);
        _lists = listsTask.Result;
        DetailList.ItemsSource = _lists.Where(list => !list.IsGroup).ToList();
        var counts = countsTask.Result;
        SetBadge("smart:today", counts.Number("today"));
        _tags = tagsTask.Result.Select(tag => tag.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).Order().ToList();
        RebuildListNavigation();
    }

    private NavigationViewItem CreateNavigationItem(string label, string glyph, string tag)
    {
        var item = new NavigationViewItem
        {
            Content = label, Tag = tag, Icon = new FontIcon { Glyph = glyph }
        };
        AttachPaneHover(item);
        return item;
    }

    private void AttachPaneHover(NavigationViewItem item)
    {
        item.PointerEntered += NavigationPaneItem_PointerEntered;
        item.PointerExited += NavigationPaneItem_PointerExited;
    }

    private void NavigationPaneItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _paneHoverCloseTimer.Stop();
        if (!_panePinnedOpen && !_hoverExpanded && !Navigation.IsPaneOpen && !_paneHoverOpenTimer.IsEnabled)
            _paneHoverOpenTimer.Start();
    }

    private void NavigationPaneItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _paneHoverOpenTimer.Stop();
        if (_hoverExpanded && !_paneHoverCloseTimer.IsEnabled) _paneHoverCloseTimer.Start();
    }
    private void SetBadge(string tag, long count)
    {
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(candidate => candidate.Tag?.ToString() == tag);
        if (item is not null) item.InfoBadge = count > 0 ? new InfoBadge { Value = (int)Math.Min(count, 99) } : null;
    }

    private async Task LoadRemindersAsync()
    {
        if (_demo) { LoadDemoRows(); return; }
        try
        {
            var query = new Dictionary<string, object?> { ["include_completed"] = ShowCompleted.IsOn, ["search"] = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim(), ["sort"] = _sort };
            query[_view.Kind switch { NavKind.List => "list_id", NavKind.Tag => "tag", _ => "scope" }] = _view.Key;
            var rows = await _sync.CallAsync<List<ReminderItem>>("reminders", query);
            if (_completing.Count > 0) return;
            SetReminderRows(rows);
        }
        catch (Exception error) { ShowInfo("Couldn't load reminders", error.Message, InfoBarSeverity.Error); }
    }
    private void UpdateRows()
    {
        RebuildReminderSections(force: true);
        EmptyState.Visibility = _reminders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RowCount.Text = _reminders.Count == 1 ? "1 reminder" : $"{_reminders.Count:N0} reminders";
    }
    private void SetReminderRows(IEnumerable<ReminderItem> rows)
    {
        var selectedId = _selected?.Id;
        _regrouping = true;
        try { _reminders.Clear(); foreach (var row in rows) _reminders.Add(row); }
        finally { _regrouping = false; }
        _reminderDate = DateTime.Today;
        UpdateRows();
        if (selectedId is not null)
        {
            var selected = _reminders.FirstOrDefault(row => row.Id == selectedId);
            _regrouping = true;
            try { ReminderList.SelectedItem = selected; }
            finally { _regrouping = false; }
            ShowDetail(selected);
        }
    }
    private void RebuildReminderSections(bool force = false)
    {
        if (_view.Kind != NavKind.Smart || _view.Key is not ("today" or "upcoming"))
        {
            if (ReferenceEquals(ReminderList.ItemsSource, _reminders)) return;
            _regrouping = true;
            try { ReminderList.ItemsSource = _reminders; ReminderList.SelectedItem = _reminders.FirstOrDefault(row => row.Id == _selected?.Id); }
            finally { _regrouping = false; }
            _sectionSignature = "";
            return;
        }
        var groups = ReminderGrouping.Group(_reminders, _view.Key, _sort, DateTimeOffset.Now);
        var signature = JsonSerializer.Serialize(groups.Select(group => new { group.Title, Ids = group.Items.Select(row => row.Id) }));
        if (!force && signature == _sectionSignature) return;
        _regrouping = true;
        try
        {
            _sectionView.Source = groups.Select(group => new ReminderSection(group.Title, group.Items)).ToList();
            ReminderList.ItemsSource = _sectionView.View;
            ReminderList.SelectedItem = _reminders.FirstOrDefault(row => row.Id == _selected?.Id);
            _sectionSignature = signature;
        }
        finally { _regrouping = false; }
    }
    private async Task RefreshStatusAsync()
    {
        if (_demo) return;
        try
        {
            var status = await _sync.CallAsync("sync_status", new { });
            var conflicts = status.Number("conflicts"); ConflictItem.Visibility = conflicts > 0 ? Visibility.Visible : Visibility.Collapsed; ConflictItem.Content = conflicts == 1 ? "1 sync conflict" : $"{conflicts} sync conflicts";
        }
        catch { }
    }
    private async Task CheckNotificationsAsync()
    {
        if (_demo || !_notificationsRegistered) return;
        try
        {
            var plan = await _sync.CallAsync("due_notifications", new { });
            if (!plan.TryGetProperty("toasts", out var toasts)) return;
            foreach (var toast in toasts.EnumerateArray())
            {
                var notification = new AppNotificationBuilder().AddText(toast.Text("title", "Reminder")).AddText(toast.Text("body", "")).BuildNotification();
                AppNotificationManager.Default.Show(notification);
            }
        }
        catch { }
    }
    private async Task BackgroundSyncAsync()
    {
        var minutes = _settings.TryGetProperty("sync_minutes", out var setting) && setting.TryGetInt32(out var value) ? Math.Clamp(value, 5, 60) : 10;
        if (DateTimeOffset.UtcNow - _lastSyncRequest < TimeSpan.FromMinutes(minutes)) return;
        await RequestAutoSyncAsync();
    }

    private async Task RequestAutoSyncAsync()
    {
        if (_demo || !_sync.IsRunning) return;
        _lastSyncRequest = DateTimeOffset.UtcNow;
        try { await _sync.CallAsync("sync", new { }); }
        catch (Exception error) { ShowInfo("Sync failed", error.Message, InfoBarSeverity.Error); }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SetGateState("Signing in…");
        try
        {
            if (Reminders.Core.DemoAccount.IsDemoUsername(AppleIdBox.Text))
            {
                await _sync.SignInDemoAsync(PasswordBox.Password);
                _uiPreferences = UiPreferences.Load(localDemo: true);
                SetPanePinned(_uiPreferences.NavigationPaneOpen, persist: false);
                UpdatePaneLayout();
            }
            else await _sync.CallAsync("login", new { apple_id = AppleIdBox.Text.Trim(), password = PasswordBox.Password });
            PasswordBox.Password = ""; _lastSignInFailure = null; await BootAsync();
            if (_sync.IsLocalDemo) ShowInfo("Local account", "Reminders are saved on this PC. iCloud sync is off.", InfoBarSeverity.Informational);
        }
        catch (SyncException error) when (error.Code == "2FA_REQUIRED") { await RequestCodeAsync(); }
        catch (SyncException error) when (error.Code == "TERMS_REQUIRED") { await AcceptTermsAsync(); }
        catch (Exception error) { ShowLogin(); ShowSignInError(error, SignInStage.Password); }
    }
    private async Task AcceptTermsAsync()
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Updated iCloud terms", Content = "Apple needs you to accept its updated terms before syncing.", PrimaryButtonText = "Accept and continue", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            SetGateState("Accepting terms…");
            try { await _sync.CallAsync("login", new { apple_id = AppleIdBox.Text.Trim(), password = PasswordBox.Password, accept_terms = true }); PasswordBox.Password = ""; _lastSignInFailure = null; await BootAsync(); }
            catch (SyncException error) when (error.Code == "2FA_REQUIRED") { await RequestCodeAsync(); }
            catch (Exception error) { ShowLogin(); ShowSignInError(error, SignInStage.Password); }
        }
        else ShowLogin();
    }
    private void SetVerificationControlsEnabled(bool enabled)
    {
        CodeBox.IsEnabled = enabled;
        VerifyCode.IsEnabled = enabled;
        ResendCode.IsEnabled = enabled;
        TextCode.IsEnabled = enabled;
        SetupHelpLink.IsEnabled = enabled;
        RestartSignIn.IsEnabled = enabled;
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_verificationBusy) return;
        _verificationBusy = true;
        SetVerificationControlsEnabled(false);
        GateError.IsOpen = false;
        // Digits only. Apple's own message and the Windows autofill both hand
        // over "123 456", and a stray space is not a wrong code.
        var code = new string(CodeBox.Text.Where(char.IsDigit).ToArray());
        try { await _sync.CallAsync("submit_2fa", new { code }); CodeBox.Text = ""; _lastSignInFailure = null; await BootAsync(); }
        catch (Exception error) { CodeBox.Text = ""; ShowSignInError(error, SignInStage.Verification); }
        finally { _verificationBusy = false; SetVerificationControlsEnabled(true); }
    }

    private async void ResendCode_Click(object sender, RoutedEventArgs e) => await RequestCodeAsync();

    private async void TextCode_Click(object sender, RoutedEventArgs e) => await RequestCodeAsync("sms");

    /// <summary>
    /// Ask Apple to send a verification code, and say where it went.
    /// </summary>
    /// <remarks>
    /// This used to be <c>try { … } catch { }</c>. A failure to send meant no
    /// code was ever coming, but the screen still asked for one and then called
    /// every attempt invalid -- so the app blamed the user's typing for its own
    /// dead end. It also matters *where* the code went: "the code sent to your
    /// Apple devices" is useless to someone whose code arrived by text.
    /// </remarks>
    private async Task RequestCodeAsync(string? method = null)
    {
        if (_verificationBusy) return;
        _verificationBusy = true;
        SetVerificationControlsEnabled(false);
        GateError.IsOpen = false;
        GateProgress.Visibility = Visibility.Collapsed;
        LoginFields.Visibility = Visibility.Collapsed;
        CodeFields.Visibility = Visibility.Visible;
        CodeBox.Text = "";
        try
        {
            var sent = await _sync.CallAsync("request_2fa", new { method });
            ShowCodeEntry(sent);
        }
        catch (Exception error)
        {
            GateStatus.Text = "Verification code";
            ShowSignInError(error, SignInStage.Delivery);
        }
        finally { _verificationBusy = false; SetVerificationControlsEnabled(true); }
    }

    /// Draw the code box for a challenge. Pure UI -- asks Apple for nothing.
    private void ShowCodeEntry(JsonElement twoFactor)
    {
        AuthGate.Visibility = Visibility.Visible;
        GateProgress.Visibility = Visibility.Collapsed;
        LoginFields.Visibility = Visibility.Collapsed;
        CodeFields.Visibility = Visibility.Visible;
        GateStatus.Text = DeliveryMessage(twoFactor);
        // Offered only while Apple has a number to text, and only as a choice --
        // a text nobody asked for retires the code already on their phone.
        TextCode.Visibility = twoFactor.Flag("can_sms") && twoFactor.Text("method", "") != "sms"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// Show the code box, asking for a code first if the challenge has not
    /// delivered one yet.
    ///
    /// A challenge that has already sent something must be left alone: reopening
    /// the window would otherwise mint a code that retires the one the user is
    /// reading off their phone.
    private async Task ShowCodeEntryAsync(JsonElement twoFactor)
    {
        ShowCodeEntry(twoFactor);
        if (twoFactor.ValueKind == JsonValueKind.Object && !twoFactor.Flag("sent"))
            await RequestCodeAsync();
    }

    /// <summary>
    /// What to tell someone waiting on a code. Apple's own wording wins when it
    /// gave any -- it knows what it just did with the challenge, and it stays
    /// right when Apple changes its mind.
    /// </summary>
    private static string DeliveryMessage(JsonElement delivery)
    {
        if (delivery.Text("notice", "") is { Length: > 0 } notice) return notice;
        return delivery.Text("method", "") switch
        {
            "sms" => delivery.Text("number", "") is { Length: > 0 } number
                ? $"Enter the code we texted to {number}"
                : "Enter the code we texted you",
            "trusted_device" => "Enter the verification code shown on your Apple device",
            _ => "Enter the six-digit code Apple sent you",
        };
    }

    private void Demo_Click(object sender, RoutedEventArgs e) => LoadDemo();
    private void LoadDemo()
    {
        _demo = true; AuthGate.Visibility = Visibility.Collapsed;
        _settings = JsonDocument.Parse("""{"theme":"system","sync_minutes":10,"notifications_enabled":true}""").RootElement.Clone(); ApplyTheme("system");
        _lists = DemoData.CreateLists(); DetailList.ItemsSource = _lists;
        _tags = ["errands"];
        RebuildListNavigation();
        _demoRows.Clear(); _demoRows.AddRange(DemoData.CreateReminders());
        LoadDemoRows();
        RestoreNavigationSelection();
        ShowInfo("Demo mode", "Sample data stays in memory. Nothing is connected to iCloud.", InfoBarSeverity.Informational);
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringNavigation) return;
        if (args.IsSettingsSelected) { RestoreNavigationSelection(); return; }
        if (args.SelectedItemContainer?.Tag?.ToString() is not string tag) return;
        if (tag == "sync")
        {
            if (!_demo) await _sync.CallAsync("sync", new { });
            if (_sync.IsLocalDemo) ShowInfo("Saved locally", "Your changes are stored on this PC.", InfoBarSeverity.Informational);
            else if (!_demo) ShowInfo("Syncing", "Checking iCloud for changes…", InfoBarSeverity.Informational);
            return;
        }
        if (tag == "conflicts") { await ResolveConflictsAsync(); return; }
        var split = tag.Split(':', 2); if (split.Length != 2) return;
        _view = new(args.SelectedItemContainer.Content?.ToString() ?? "Reminders", "", split[0] switch { "list" => NavKind.List, "tag" => NavKind.Tag, _ => NavKind.Smart }, split[1]);
        ViewTitle.Text = _view.Label; ViewSubtitle.Text = _view.Kind == NavKind.Tag ? "Filtered by tag" : ""; ShowDetail(null); await LoadRemindersAsync();
    }

    private void Navigation_PaneOpening(NavigationView sender, object args)
    {
        if (_initializingPane) return;
        if (_temporaryPaneTarget is true) { _temporaryPaneTarget = null; return; }
        if (!_panePinnedOpen) _hoverExpanded = true;
    }

    private void Navigation_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (_initializingPane) return;
        // A delayed light-dismiss from the compact view can arrive after the
        // pin action. It must not undo the user's explicit choice.
        if (_panePinnedOpen) { args.Cancel = true; return; }
        if (_temporaryPaneTarget is false) { _temporaryPaneTarget = null; _hoverExpanded = false; return; }
        _hoverExpanded = false;
    }

    private void SetPanePinned(bool pinned, bool persist = true)
    {
        _paneHoverOpenTimer.Stop();
        _paneHoverCloseTimer.Stop();
        _hoverExpanded = false;
        _temporaryPaneTarget = null;
        _panePinnedOpen = pinned;
        // Left reserves space for the pinned sidebar instead of opening an
        // overlay that NavigationView dismisses when a list is selected.
        var wasInitializing = _initializingPane;
        _initializingPane = true;
        try
        {
            Navigation.PaneDisplayMode = pinned ? NavigationViewPaneDisplayMode.Left : NavigationViewPaneDisplayMode.LeftCompact;
            Navigation.IsPaneToggleButtonVisible = true;
            Navigation.IsPaneOpen = pinned;
        }
        finally { _initializingPane = wasInitializing; }
        if (persist && !_demo) UiPreferences.SaveNavigationPaneOpen(pinned, localDemo: _sync.IsLocalDemo);
    }

    private void Navigation_Loaded(object sender, RoutedEventArgs e)
    {
        // Click is not a routed event in WinUI. Attach to the template's native
        // hamburger so mouse, keyboard, and automation all use the same action.
        var pending = new Stack<DependencyObject>();
        pending.Push(Navigation);
        while (pending.TryPop(out var source))
        {
            if (source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase { Name: "TogglePaneButton" } toggle)
            {
                toggle.Click += (_, _) => SetPanePinned(!_panePinnedOpen);
                Navigation.Loaded -= Navigation_Loaded;
                return;
            }
            for (var child = 0; child < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(source); child++)
                pending.Push(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(source, child));
        }
    }

    private void Navigation_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pointerX = e.GetCurrentPoint(Navigation).Position.X;
        if (!_panePinnedOpen && !_hoverExpanded && !Navigation.IsPaneOpen && pointerX <= Navigation.CompactPaneLength + 8)
        {
            _paneHoverCloseTimer.Stop();
            if (!_paneHoverOpenTimer.IsEnabled) _paneHoverOpenTimer.Start();
            return;
        }
        _paneHoverOpenTimer.Stop();
        if (_hoverExpanded && pointerX > Navigation.OpenPaneLength)
        {
            if (!_paneHoverCloseTimer.IsEnabled) _paneHoverCloseTimer.Start();
        }
        else _paneHoverCloseTimer.Stop();
    }

    private void Navigation_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _paneHoverOpenTimer.Stop();
        if (_hoverExpanded && !_paneHoverCloseTimer.IsEnabled) _paneHoverCloseTimer.Start();
    }

    private void OpenPaneForHover()
    {
        _paneHoverOpenTimer.Stop();
        if (_panePinnedOpen || Navigation.IsPaneOpen) return;
        _hoverExpanded = true;
        _temporaryPaneTarget = true;
        Navigation.IsPaneOpen = true;
    }

    private void CloseHoverPane()
    {
        _paneHoverCloseTimer.Stop();
        if (!_hoverExpanded || _panePinnedOpen) return;
        _temporaryPaneTarget = false;
        Navigation.IsPaneOpen = false;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { _searchTimer.Stop(); _searchTimer.Start(); } }
    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem selected) return;
        _sort = selected.Tag?.ToString() ?? "manual"; await LoadRemindersAsync();
    }
    private async void ShowCompleted_Toggled(object sender, RoutedEventArgs e) { if (Root.IsLoaded) await LoadRemindersAsync(); }
    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkbox || checkbox.DataContext is not ReminderItem reminder) return;
        if (!_completing.Add(reminder.Id)) return;
        checkbox.IsEnabled = false;
        var completed = reminder.Completed;
        var saved = false;
        try
        {
            if (!_demo) await _sync.CallAsync("update_reminder", new { id = reminder.Id, completed });
            saved = true;
            if (completed) await AnimateCompletionAsync(reminder);
            _completing.Remove(reminder.Id);
            if (_demo) LoadDemoRows(); else await RefreshAllAsync();
        }
        catch (Exception error) { if (!saved) reminder.Completed = !completed; ShowInfo(saved ? "Reminder saved; refresh failed" : "Couldn't update reminder", error.Message, InfoBarSeverity.Error); }
        finally { checkbox.IsEnabled = true; _completing.Remove(reminder.Id); }

    }

    private void ReminderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_regrouping) ShowDetail(ReminderList.SelectedItem as ReminderItem);
    }
    private void ShowDetail(ReminderItem? reminder)
    {
        _selected = reminder; _loadingDetail = true; NoSelection.Visibility = reminder is null ? Visibility.Visible : Visibility.Collapsed; DetailPane.Visibility = reminder is null ? Visibility.Collapsed : Visibility.Visible;
        if (reminder is not null)
        {
            DetailTitle.Text = reminder.Title; DetailNotes.Text = reminder.Description; DetailList.SelectedValue = reminder.ListId; DetailPriority.SelectedItem = DetailPriority.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == reminder.Priority.ToString());
            if (DateTimeOffset.TryParse(reminder.DueDate, out var due)) { DetailDate.Date = due; DetailTime.Time = due.LocalDateTime.TimeOfDay; } else DetailDate.Date = null;
            DetailAllDay.IsOn = reminder.AllDay; DetailTime.IsEnabled = !reminder.AllDay; DetailFlagged.IsOn = reminder.Flagged;
        }
        SaveButton.IsEnabled = false; _loadingDetail = false;
        UpdatePaneLayout();
        if (reminder is not null) AnimateDetailPane();
    }
    private void AnimateDetailPane()
    {
        if (!new UISettings().AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(DetailPane);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f); fade.InsertKeyFrame(1f, 1f, easing); fade.Duration = TimeSpan.FromMilliseconds(180);
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(18f, 0f, 0f)); slide.InsertKeyFrame(1f, Vector3.Zero, easing); slide.Duration = fade.Duration;
        visual.StartAnimation("Opacity", fade); visual.StartAnimation("Offset", slide);
    }
    private void Detail_Changed(object sender, object e) { if (!_loadingDetail && _selected is not null) { SaveButton.IsEnabled = true; DetailTime.IsEnabled = !DetailAllDay.IsOn; } }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        string? due = FormatDueDate(DetailDate.Date, DetailAllDay.IsOn, DetailTime.Time);
        var priority = long.Parse(((ComboBoxItem?)DetailPriority.SelectedItem)?.Tag?.ToString() ?? "0");
        try
        {
            if (_demo)
            {
                _selected.Title = DetailTitle.Text.Trim(); _selected.Description = DetailNotes.Text;
                _selected.DueDate = due; _selected.AllDay = DetailAllDay.IsOn;
                _selected.Flagged = DetailFlagged.IsOn; _selected.Priority = priority;
            }
            else await _sync.CallAsync("update_reminder", new { id = _selected.Id, title = DetailTitle.Text.Trim(), description = DetailNotes.Text, due_date = due, all_day = DetailAllDay.IsOn, flagged = DetailFlagged.IsOn, priority });
            SaveButton.IsEnabled = false; await LoadRemindersAsync();
        }
        catch (Exception error) { ShowInfo("Couldn't save reminder", error.Message, InfoBarSeverity.Error); }
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Delete reminder?", Content = _selected.DisplayTitle, PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (_demo) _selected.Deleted = true; else await _sync.CallAsync("delete_reminder", new { id = _selected.Id }); ShowDetail(null); await LoadRemindersAsync();
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await ShowNewReminderAsync();
    private async void NewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await ShowNewReminderAsync(); }
    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; if (AuthGate.Visibility == Visibility.Collapsed && SetupGuide.Visibility == Visibility.Collapsed) SearchBox.Focus(FocusState.Programmatic); }
    private async void SettingsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await ShowSettingsAsync(); }
    private async Task ResolveConflictsAsync()
    {
        if (_demo) return; var conflicts = await _sync.CallAsync<List<ConflictItem>>("conflicts", new { });
        foreach (var conflict in conflicts) { var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Resolve sync conflict", Content = "Keep the version edited on this PC, or the current iCloud version?", PrimaryButtonText = "Keep this PC", SecondaryButtonText = "Keep iCloud", CloseButtonText = "Later" }; var result = await dialog.ShowAsync(); if (result == ContentDialogResult.None) break; await _sync.CallAsync("resolve_conflict", new { id = conflict.Id, keep = result == ContentDialogResult.Primary ? "local" : "remote" }); }
        await RefreshAllAsync();
    }
    private void ApplyTheme(string theme) => Root.RequestedTheme = theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_demo || _updateBusy || _updateLifetime.IsCancellationRequested) return;
        // Portable and development launches only contact GitHub on an explicit check.
        if (!manual && !AppUpdater.CanInstall) return;
        _updateBusy = true;
        try
        {
            _availableUpdate = await _updater.CheckAsync(_updateLifetime.Token);
            if (_updateLifetime.IsCancellationRequested) return;
            if (_availableUpdate is null)
            {
                UpdateInfoBar.IsOpen = false;
                if (manual) { UpdateInfoBar.Title = "You're up to date"; UpdateInfoBar.Message = "No newer stable release is available."; UpdateInfoBar.IsOpen = true; UpdateButton.Visibility = Visibility.Collapsed; }
                return;
            }
            UpdateInfoBar.Title = $"Reminders for Windows {_availableUpdate.Version} is available";
            UpdateInfoBar.Message = AppUpdater.CanInstall ? "Install the update and restart when you're ready. Your reminders and sign-in will be kept." : "Download the new release from GitHub for this portable or packaged build.";
            UpdateButton.Content = AppUpdater.CanInstall ? "Install and restart" : "Open releases";
            UpdateButton.Visibility = Visibility.Visible;
            UpdateInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppLog.Error("Could not check for updates", error);
            if (manual) { UpdateInfoBar.Title = "Could not check for updates"; UpdateInfoBar.Message = error.Message; UpdateInfoBar.IsOpen = true; UpdateButton.Visibility = Visibility.Collapsed; }
        }
        finally { _updateBusy = false; }
    }

    private async void Update_Click(object sender, RoutedEventArgs args)
    {
        if (_updateBusy || _dialogOpen || _availableUpdate is null) return;
        if (!AppUpdater.CanInstall)
        {
            await global::Windows.System.Launcher.LaunchUriAsync(new Uri($"https://github.com/{AppUpdater.Repository}/releases/latest"));
            return;
        }
        _updateBusy = true;
        _dialogOpen = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var confirmation = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Install update?", Content = "Reminders for Windows will close while the update installs, then reopen. Save any unfinished reminder edits first.", PrimaryButtonText = "Install and restart", CloseButtonText = "Later" };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            UpdateInfoBar.Title = "Downloading update";
            // Progress<T> does not rely on a WinUI SynchronizationContext.
            var progress = new Progress<double>(percent => DispatcherQueue.TryEnqueue(() => UpdateInfoBar.Message = $"{percent:F0}% downloaded"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var installer = await _updater.DownloadAsync(_availableUpdate, progress, timeout.Token);
            _updateLifetime.Token.ThrowIfCancellationRequested();
            Navigation.IsEnabled = false;
            _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop();
            _syncDisposedForUpdate = true;
            await _sync.DisposeAsync();
            _updateLifetime.Token.ThrowIfCancellationRequested();
            AppUpdater.LaunchInstaller(installer);
            Close();
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppLog.Error("Could not install update", error);
            UpdateInfoBar.Title = "Could not install update"; UpdateInfoBar.Message = error.Message;
            if (_syncDisposedForUpdate)
            {
                _sync = new SyncClient(); AttachSyncEvents();
                _syncDisposedForUpdate = false; await BootAsync();
            }
        }
        finally { _updateBusy = false; _dialogOpen = false; UpdateButton.IsEnabled = true; Navigation.IsEnabled = true; }
    }

    private void AttachSyncEvents()
    {
        _sync.EventReceived += Sync_EventReceived;
    }
    private void ShowInfo(string title, string message, InfoBarSeverity severity) { AppInfoBar.Title = title; AppInfoBar.Message = message; AppInfoBar.Severity = severity; AppInfoBar.IsOpen = true; }

    private void Sync_EventReceived(object? sender, SyncEventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_syncDisposedForUpdate || _updateLifetime.IsCancellationRequested || !_sync.IsCurrentEvent(e)) return;
        switch (e.Name)
        {
            case "ready": await BootAsync(); break;
            // Not authenticated is not the same as "start over". A restore that
            // stopped at the second factor needs the code box, not the password
            // box; one still in flight needs neither; and someone already
            // working in cached data should be told, not thrown out of the app.
            case "auth_changed":
                if (e.Data.Flag("authenticated")) await BootAsync();
                else if (e.Data.Flag("needs_2fa")) await ShowCodeEntryAsync(e.Data.Property("two_factor"));
                else if (e.Data.Flag("restoring")) SetGateState("Signing you back in…");
                else if (AuthGate.Visibility == Visibility.Collapsed)
                    ShowInfo("Sign in to resume syncing", "Cached reminders and queued edits remain available.", InfoBarSeverity.Warning);
                else ShowLogin();
                break;
            case "sync_started": _lastSyncRequest = DateTimeOffset.UtcNow; ShowInfo("Syncing", "Checking iCloud for changes…", InfoBarSeverity.Informational); break;
            case "sync_progress":
                ShowInfo("Syncing", e.Data.Text("message", $"Processing {e.Data.Text("stage", "reminders")}..."), InfoBarSeverity.Informational);
                break;
            case "sync_finished": AppInfoBar.IsOpen = false; await RefreshAllAsync(); break;
            case "sync_error": ShowInfo("Sync failed", e.Data.Text("message", "Try again in a moment."), InfoBarSeverity.Error); break;
            case "push_failed": ShowInfo("Upload failed", e.Data.Property("error").Text("message", "Your edit is saved locally and will be retried."), InfoBarSeverity.Error); break;
            case "conflict": ShowInfo("Sync conflict", "This reminder also changed in iCloud. Open sync conflicts to choose which version to keep.", InfoBarSeverity.Warning); await RefreshStatusAsync(); break;
        }
    });
    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop(); _paneHoverOpenTimer.Stop(); _paneHoverCloseTimer.Stop();
        _updateTimer.Stop(); _updateLifetime.Cancel();
        if (_notificationsRegistered) { try { AppNotificationManager.Default.Unregister(); } catch { } }
        if (!_syncDisposedForUpdate) await _sync.DisposeAsync();
    }
}
