using Reminders.Core;
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
    private readonly LatestRequest _reminderLoads = new();
    private readonly LatestRequest _navigationLoads = new();
    private long _accountEpoch;
    private readonly Microsoft.UI.Xaml.Data.CollectionViewSource _sectionView = new() { IsSourceGrouped = true };
    private string _sectionSignature = "";
    private bool _regrouping;
    private DateTime _reminderDate = DateTime.Today;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(15) };
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
    private readonly HashSet<string> _collapsedSubtasks = [];
    private ReminderItem? _selected;
    private NavEntry _view = new(L10n.Get("Today"), "", NavKind.Smart, "today");
    private string _sort = "manual";
    private bool _verificationBusy;
    private bool _loadingDetail;
    private bool _detailDirty;
    private string? _detailBaseline;
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
    private bool _accountBusy;

    public MainWindow()
    {
        InitializeComponent();
        Title = L10n.Get("AppTitle");
        DetailDateTime.SizeChanged += EditorPair_SizeChanged;
        DetailRecurrence.SizeChanged += EditorPair_SizeChanged;
        DetailOptions.SizeChanged += EditorPair_SizeChanged;
        _windowsReminderTemplate = ReminderList.ItemTemplate;
        _windowsReminderRowStyle = ReminderList.ItemContainerStyle;
        Root.ActualThemeChanged += (_, _) => { if (IsAppleStyle) { ApplyAppleColors(); RebuildAppleNavigation(); } };
        var settingsAccelerator = new KeyboardAccelerator { Key = (global::Windows.System.VirtualKey)188, Modifiers = global::Windows.System.VirtualKeyModifiers.Control };
        settingsAccelerator.Invoked += SettingsAccelerator_Invoked;
        Root.KeyboardAccelerators.Add(settingsAccelerator);
        Navigation.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Navigation_PointerMoved), true);
        Navigation.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Navigation_PointerExited), true);
        Navigation.Loaded += Navigation_Loaded;
        foreach (var item in Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>()) AttachPaneHover(item);
        ReminderList.ItemsSource = _reminders;
        PopulatePriorityChoices(DetailPriority);
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
        InitializeWindowChrome();
        SetPanePinned(UiPreferences.LoadNavigationPaneOpen(), persist: false);
        ApplyInterfaceStyle();
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
        appWindow.Closing += AppWindow_Closing;
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
        if (Environment.GetCommandLineArgs().Contains("--preview-editor", StringComparer.OrdinalIgnoreCase))
        {
            LoadDemo();
            ApplyTheme(Environment.GetCommandLineArgs().Contains("--preview-light", StringComparer.OrdinalIgnoreCase) ? "light" : "dark");
            AppInfoBar.IsOpen = false;
            var sample = _reminders.FirstOrDefault(row => !row.AllDay);
            if (sample is not null) { ReminderList.SelectedItem = sample; ShowDetail(sample); }
            if (Environment.GetCommandLineArgs().Contains("--preview-list-min", StringComparer.OrdinalIgnoreCase))
                _uiPreferences.DetailPaneFraction = 1;
            if (Environment.GetCommandLineArgs().Contains("--preview-details-min", StringComparer.OrdinalIgnoreCase))
                _uiPreferences.DetailPaneFraction = 0;
            UpdatePaneLayout();
            if (Environment.GetCommandLineArgs().Contains("--preview-new-reminder", StringComparer.OrdinalIgnoreCase))
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => await ShowNewReminderAsync());
            if (Environment.GetCommandLineArgs().Contains("--preview-banner", StringComparer.OrdinalIgnoreCase))
                ShowInfo(L10n.Get("Syncing"), L10n.Get("SyncDownloadPreview"), InfoBarSeverity.Informational);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--preview-settings", StringComparer.OrdinalIgnoreCase))
        {
            LoadDemo();
            if (IsSyncRecoveryPreview) Title = L10n.Get("RecoveryPreviewWindowTitle");
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => await ShowSettingsAsync(IsSyncRecoveryPreview ? 1 : 0));
            return;
        }
        if (AppleSignInPreview.Enabled)
        {
            ShowLogin();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => BrowserSignIn_Click(this, new RoutedEventArgs()));
            return;
        }
        // Offline UI verification of the failure/recovery flow, using an
        // isolated REMINDERS_DATA_DIR. Never compiled into release builds.
        if (Environment.GetCommandLineArgs().Contains("--preview-sign-in-error", StringComparer.OrdinalIgnoreCase))
        {
            ShowLogin();
            ShowSignInError(new SyncException("NETWORK", "Apple accepted the verification code, but iCloud account sign-in failed (HTTP 500).", "HTTP 500: preview").WithPresentation("ErrorVerificationAccepted", " (HTTP 500)"), SignInStage.Verification);
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
        if (_accountBusy) return;
        SetGateState(L10n.Get("SyncServiceStarting"));
        try
        {
            if (!_sync.IsRunning) await _sync.StartAsync();
            SetGateState(L10n.Get("CheckingYourAccount"));
            var status = await _sync.CallAsync("auth_status", new { });
            if (_accountBusy) return;
            _settings = await _sync.CallAsync("settings", new { });
            if (_accountBusy) return;
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
                if (_accountBusy || AuthGate.Visibility != Visibility.Collapsed) return;
                _statusTimer.Start(); _notificationTimer.Start(); _syncTimer.Start();
                if (status.Flag("authenticated")) await RequestAutoSyncAsync();
                if (!status.Flag("authenticated") && !status.Flag("restoring")) ShowInfo(L10n.Get("SignInToResumeSyncing"), L10n.Get("CachedRemindersAvailable"), InfoBarSeverity.Warning);
                return;
            }
            if (status.Flag("restoring")) { SetGateState(L10n.Get("SigningYouBackIn")); return; }
            ShowLogin();
        }
        catch (Exception error)
        {
            SetGateState(L10n.Get("SyncServiceUnavailable"), false);
            GateError.Message = L10n.Error(error); GateError.IsOpen = true;
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
        AuthGate.Visibility = Visibility.Visible; GateProgress.Visibility = Visibility.Collapsed; GateStatus.Text = L10n.Get("SignInToICloud"); LoginFields.Visibility = Visibility.Visible; CodeFields.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(AppleIdBox.Text)) AppleDeviceSignIn.Focus(FocusState.Programmatic);
        else AppleIdBox.Focus(FocusState.Programmatic);
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
        if (_demo) { _navigationLoads.Invalidate(); return; }
        await _navigationLoads.RunAsync(async () =>
        {
            var listsTask = _sync.CallAsync<List<ReminderList>>("lists", new { });
            var tagsTask = _sync.CallAsync<List<ReminderTag>>("tags", new { });
            var countsTask = _sync.CallAsync("smart_counts", new { });
            await Task.WhenAll(listsTask, tagsTask, countsTask);
            return (Lists: listsTask.Result, Tags: tagsTask.Result, Counts: countsTask.Result);
        }, data =>
        {
            if (_windowClosed || _accountBusy || AuthGate.Visibility != Visibility.Collapsed) return;
            _lists = data.Lists;
            SetBadge("smart:today", data.Counts.Number("today"));
            foreach (var scope in new[] { "today", "upcoming", "all", "completed", "deleted" })
                _appleSmartCounts[scope] = data.Counts.Number(scope);
            _tags = data.Tags.Select(tag => tag.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).Order().ToList();
            RebuildListNavigation();
        });
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
        if (!IsAppleStyle && !_panePinnedOpen && !_hoverExpanded && !Navigation.IsPaneOpen && !_paneHoverOpenTimer.IsEnabled)
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
        UpdateCompletedControls();
        if (_demo) { _reminderLoads.Invalidate(); LoadDemoRows(); return; }
        try
        {
            var query = new Dictionary<string, object?> { ["include_completed"] = ShowCompleted.IsOn, ["include_parents"] = true, ["search"] = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim(), ["sort"] = _sort };
            query[_view.Kind switch { NavKind.List => "list_id", NavKind.Tag => "tag", _ => "scope" }] = _view.Key;
            await _reminderLoads.RunAsync(() => _sync.CallAsync<List<ReminderItem>>("reminders", query), rows =>
            {
                if (_windowClosed || _accountBusy || AuthGate.Visibility != Visibility.Collapsed || _completing.Count > 0) return;
                SetReminderRows(rows);
            });
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntLoadReminders"), L10n.Error(error), InfoBarSeverity.Error); }
    }
    private void UpdateRows()
    {
        RebuildReminderSections(force: true);
        EmptyState.Visibility = _reminders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
        EmptyStateTitle.Text = searching ? L10n.Get("NoMatchingReminders") : L10n.Get("NoRemindersHere");
        EmptyStateMessage.Text = searching ? L10n.Get("SearchEmptyDescription")
            : _view.Kind == NavKind.Smart && _view.Key is "completed" or "deleted"
                ? L10n.Get(_view.Key == "completed" ? "RemindersAppearWhenCompleted" : "RemindersAppearWhenDeleted")
                : L10n.Get("ReminderListEmptyDescription");
        var count = _reminders.Count(row => !row.ContextOnly);
        var matching = _reminders.Any(row => row.ContextOnly);
        var countKey = matching
            ? (count == 1 ? "MatchingReminderCountOne" : "MatchingReminderCountMany")
            : (count == 1 ? "ReminderCountOne" : "ReminderCountMany");
        RowCount.Text = L10n.Format(countKey, count);
    }
    private void SetReminderRows(IEnumerable<ReminderItem> rows)
    {
        var selectedId = _selected?.Id;
        var preserveEditor = _detailDirty && _selected is not null;
        _regrouping = true;
        try { _reminders.Clear(); foreach (var row in rows) _reminders.Add(row); }
        finally { _regrouping = false; }
        if (!string.IsNullOrWhiteSpace(SearchBox.Text) || _view.Kind == NavKind.Tag)
        {
            var byId = _reminders.ToDictionary(row => row.Id);
            foreach (var match in _reminders.Where(row => !row.ContextOnly))
            {
                var seen = new HashSet<string>();
                for (var parentId = match.ParentId; parentId is not null && seen.Add(parentId);)
                {
                    _collapsedSubtasks.Remove(parentId);
                    parentId = byId.GetValueOrDefault(parentId)?.ParentId;
                }
            }
        }
        _reminderDate = DateTime.Today;
        UpdateRows();
        if (selectedId is not null)
        {
            var selected = _reminders.FirstOrDefault(row => row.Id == selectedId);
            _regrouping = true;
            try { ReminderList.SelectedItem = selected; }
            finally { _regrouping = false; }
            if (!preserveEditor) ShowDetail(selected);
        }
    }
    private void RebuildReminderSections(bool force = false)
    {
        var families = ReminderHierarchy.Build(_reminders, _collapsedSubtasks);
        List<ReminderItem> Visible(ReminderFamily<ReminderItem> family) => family.Rows.Where(row => !row.Hidden).Select(row =>
        {
            row.Item.ConfigureHierarchy(row.Depth, row.HasChildren, !_collapsedSubtasks.Contains(row.Item.Id)); return row.Item;
        }).ToList();
        if (_view.Kind != NavKind.Smart || _view.Key is not ("today" or "upcoming"))
        {
            _regrouping = true;
            try { var visible = families.SelectMany(Visible).ToList(); ReminderList.ItemsSource = visible; ReminderList.SelectedItem = visible.FirstOrDefault(row => row.Id == _selected?.Id); }
            finally { _regrouping = false; }
            _sectionSignature = "";
            return;
        }
        var groups = ReminderGrouping.Group(families, _view.Key, _sort, DateTimeOffset.Now);
        var signature = JsonSerializer.Serialize(groups.Select(group => new { group.Section, Ids = group.Items.SelectMany(family => family.Rows).Select(row => new { row.Item.Id, row.Hidden, row.Depth }) }));
        if (!force && signature == _sectionSignature) return;
        _regrouping = true;
        try
        {
            _sectionView.Source = groups.Select(group => new ReminderSection(L10n.Get($"ReminderSection{group.Section}"), group.Items.SelectMany(Visible))).ToList();
            ReminderList.ItemsSource = _sectionView.View;
            ReminderList.SelectedItem = _reminders.FirstOrDefault(row => row.Id == _selected?.Id);
            _sectionSignature = signature;
        }
        finally { _regrouping = false; }
    }
    private async Task RefreshStatusAsync()
    {
        if (_demo) return;
        var accountEpoch = _accountEpoch;
        try
        {
            var status = await _sync.CallAsync("sync_status", new { });
            if (_windowClosed || accountEpoch != _accountEpoch || _accountBusy || AuthGate.Visibility != Visibility.Collapsed) return;
            var conflicts = status.Number("conflicts"); ConflictItem.Visibility = conflicts > 0 ? Visibility.Visible : Visibility.Collapsed; ConflictItem.Content = conflicts == 1 ? L10n.Get("SyncConflictCountOne") : L10n.Format("SyncConflictCountMany", conflicts);
            AppleConflicts.Visibility = ConflictItem.Visibility;
            AppleConflicts.Content = ConflictItem.Content;
            var lastSync = DateTimeOffset.TryParse(status.Text("last_sync"), out var synced)
                ? L10n.Format("SidebarLastSynced", synced.LocalDateTime) : L10n.Get("SidebarNeverSynced");
            var pendingCount = status.Number("pending_pushes");
            AppleSyncStatus.Text = _sync.IsLocalDemo ? L10n.Get("SidebarLocalAccount")
                : status.Flag("running") ? L10n.Get("Syncing")
                : pendingCount > 0 ? L10n.Format("SidebarPendingSync", lastSync, pendingCount) : lastSync;
        }
        catch { }
    }
    private async Task CheckNotificationsAsync()
    {
        if (_demo || _accountBusy || AuthGate.Visibility != Visibility.Collapsed || !_notificationsRegistered) return;
        var accountEpoch = _accountEpoch;
        try
        {
            var plan = await _sync.CallAsync("due_notifications", new { });
            if (_windowClosed || accountEpoch != _accountEpoch || _accountBusy || AuthGate.Visibility != Visibility.Collapsed) return;
            if (!plan.TryGetProperty("toasts", out var toasts)) return;
            foreach (var toast in toasts.EnumerateArray())
            {
                var (title, body) = NotificationText(toast);
                var notification = new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification();
                AppNotificationManager.Default.Show(notification);
            }
        }
        catch { }
    }

    private static (string Title, string Body) NotificationText(JsonElement toast)
    {
        if (toast.Property("names") is { ValueKind: JsonValueKind.Array } names)
        {
            var count = toast.Number("count");
            var missed = toast.Text("summary_kind") == "missed";
            var titleKey = missed ? (count == 1 ? "NotifyMissedOne" : "NotifyMissedMany") : "NotifyDueMany";
            var body = string.Join(L10n.Get("NotifyNameSeparator"), names.EnumerateArray().Select(name => name.GetString()));
            if (missed && count > 3) body = L10n.Format("NotifyNamesAndMore", body, count - 3);
            return (L10n.Format(titleKey, count), body);
        }
        if (toast.Property("due_at") is { ValueKind: JsonValueKind.String } date && date.TryGetDateTimeOffset(out var due))
        {
            var rawTitle = toast.Text("raw_title");
            return (rawTitle.Length > 0 ? rawTitle : L10n.Get("Reminder"),
                L10n.Format(toast.Flag("all_day") ? "NotifyAllDayDate" : "NotifyTimedDate", due));
        }
        return (toast.Text("title", L10n.Get("Reminder")), toast.Text("body"));
    }
    private async Task BackgroundSyncAsync()
    {
        var minutes = _settings.TryGetProperty("sync_minutes", out var setting) && setting.TryGetInt32(out var value) ? Math.Clamp(value, 1, 60) : 1;
        if (DateTimeOffset.UtcNow - _lastSyncRequest < TimeSpan.FromMinutes(minutes)) return;
        await RequestAutoSyncAsync();
    }

    private async Task RequestAutoSyncAsync()
    {
        if (_demo || _accountBusy || AuthGate.Visibility != Visibility.Collapsed || !_sync.IsRunning) return;
        _lastSyncRequest = DateTimeOffset.UtcNow;
        try { await _sync.CallAsync("sync", new { }); }
        catch (Exception error) { ShowInfo(L10n.Get("SyncFailed"), L10n.Error(error), InfoBarSeverity.Error); }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SetGateState(L10n.Get("SigningIn"));
        try
        {
            if (Reminders.Core.DemoAccount.IsDemoUsername(AppleIdBox.Text))
            {
                await _sync.SignInDemoAsync(PasswordBox.Password);
                _uiPreferences = UiPreferences.Load(localDemo: true);
                SetPanePinned(_uiPreferences.NavigationPaneOpen, persist: false);
                ApplyInterfaceStyle();
            }
            else
            {
                if (!_sync.IsRunning) await _sync.StartAsync();
                await _sync.CallAsync("login", new { apple_id = AppleIdBox.Text.Trim(), password = PasswordBox.Password });
            }
            PasswordBox.Password = ""; _lastSignInFailure = null; await BootAsync();
            if (_sync.IsLocalDemo) ShowInfo(L10n.Get("LocalAccount"), L10n.Get("LocalAccountStartedDescription"), InfoBarSeverity.Informational);
        }
        catch (SyncException error) when (error.Code == "2FA_REQUIRED") { await RequestCodeAsync(); }
        catch (SyncException error) when (error.Code == "TERMS_REQUIRED") { await AcceptTermsAsync(); }
        catch (Exception error) { ShowLogin(); ShowSignInError(error, SignInStage.Password); }
    }
    private async Task AcceptTermsAsync()
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L10n.Get("UpdatedICloudTerms"), Content = L10n.Get("ICloudTermsDescription"), PrimaryButtonText = L10n.Get("AcceptAndContinue"), CloseButtonText = L10n.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            SetGateState(L10n.Get("AcceptingTerms"));
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
            GateStatus.Text = L10n.Get("VerificationCode");
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
                ? L10n.Format("EnterCodeForPhoneNumber", number)
                : L10n.Get("EnterTextedCode"),
            "trusted_device" => L10n.Get("EnterAppleDeviceCode"),
            _ => L10n.Get("EnterGenericVerificationCode"),
        };
    }

    private void LoadDemo()
    {
        _accountEpoch++; _navigationLoads.Invalidate(); _reminderLoads.Invalidate();
        _demo = true; AuthGate.Visibility = Visibility.Collapsed;
        _settings = JsonDocument.Parse("""{"theme":"system","sync_minutes":1,"notifications_enabled":true}""").RootElement.Clone(); ApplyTheme("system");
        _lists = DemoData.CreateLists();
        _tags = ["errands"];
        RebuildListNavigation();
        _demoRows.Clear(); _demoRows.AddRange(DemoData.CreateReminders());
        LoadDemoRows();
        RestoreNavigationSelection();
        ShowInfo(L10n.Get("DemoMode"), L10n.Get("DemoModeDescription"), InfoBarSeverity.Informational);
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringNavigation) return;
        if (args.IsSettingsSelected) { RestoreNavigationSelection(); return; }
        if (args.SelectedItemContainer?.Tag?.ToString() is not string tag) return;
        if (tag is "sync" or "conflicts") { RestoreNavigationSelection(); return; }
        var split = tag.Split(':', 2); if (split.Length != 2) return;
        var target = new NavEntry(args.SelectedItemContainer.Content?.ToString() ?? L10n.Get("Reminders"), "", split[0] switch { "list" => NavKind.List, "tag" => NavKind.Tag, _ => NavKind.Smart }, split[1]);
        await SelectViewAsync(target);
    }

    private async Task SelectViewAsync(NavEntry target)
    {
        if (_savingDetail || _detailTransition || _dialogOpen)
        {
            RestoreNavigationSelection();
            return;
        }
        if (target.Kind == _view.Kind && target.Key == _view.Key) return;
        if (_detailDirty)
        {
            RestoreNavigationSelection();
            if (!await ConfirmLeaveDetailAsync()) return;
        }
        _reminderLoads.Invalidate();
        _view = target;
        RestoreNavigationSelection();
        ViewTitle.Text = _view.Label;
        ViewSubtitle.Text = _view.Kind == NavKind.Tag ? L10n.Get("FilteredByTag") : "";
        ViewSubtitle.Visibility = string.IsNullOrEmpty(ViewSubtitle.Text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateViewAccent();
        ShowDetail(null);
        await LoadRemindersAsync();
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
            Navigation.IsPaneToggleButtonVisible = !IsAppleStyle;
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
        if (!IsAppleStyle && !_panePinnedOpen && !_hoverExpanded && !Navigation.IsPaneOpen && pointerX <= Navigation.CompactPaneLength + 8)
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
            if (_demo) CompleteDemoFamily(reminder, completed);
            else await _sync.CallAsync("update_reminder", new { id = reminder.Id, completed });
            saved = true;
            if (completed) await AnimateCompletionAsync(reminder);
            _completing.Remove(reminder.Id);
            if (_demo) LoadDemoRows(); else await RefreshAllAsync();
        }
        catch (Exception error) { if (!saved) reminder.Completed = !completed; ShowInfo(saved ? L10n.Get("ReminderSavedRefreshFailed") : L10n.Get("CouldntUpdateReminder"), L10n.Error(error), InfoBarSeverity.Error); }
        finally { checkbox.IsEnabled = true; _completing.Remove(reminder.Id); }

    }

    private async void ReminderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_regrouping) return;
        var target = ReminderList.SelectedItem as ReminderItem;
        if (target?.Id == _selected?.Id) return;
        if (_savingDetail || _detailTransition || _dialogOpen) { RestoreReminderSelection(); return; }
        if (_detailDirty)
        {
            var targetId = target?.Id;
            RestoreReminderSelection();
            if (!await ConfirmLeaveDetailAsync()) return;
            target = _reminders.FirstOrDefault(row => row.Id == targetId);
            _regrouping = true;
            try { ReminderList.SelectedItem = target; }
            finally { _regrouping = false; }
        }
        ShowDetail(target);
    }
    private void ShowDetail(ReminderItem? reminder)
    {
        _selected = reminder; _loadingDetail = true; NoSelection.Visibility = reminder is null ? Visibility.Visible : Visibility.Collapsed; DetailPane.Visibility = reminder is null ? Visibility.Collapsed : Visibility.Visible;
        if (reminder is not null)
        {
            DetailTitle.Text = reminder.Title; DetailNotes.Text = reminder.Description;
            DetailListName.Text = _lists.FirstOrDefault(list => list.Id == reminder.ListId)?.Title ?? L10n.Get("Reminder");
            DetailList.ItemsSource = OrderedLists();
            DetailList.SelectedValue = reminder.ListId;
            AddSubtaskButton.IsEnabled = !reminder.Completed && !reminder.Deleted;
            DetailPriority.SelectedItem = DetailPriority.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is long value && value == reminder.Priority);
            if (DateTimeOffset.TryParse(reminder.DueDate, out var due)) { DetailDate.Date = due; DetailTime.Time = due.LocalDateTime.TimeOfDay; } else DetailDate.Date = null;
            DetailAllDay.IsOn = reminder.AllDay; DetailTime.IsEnabled = !reminder.AllDay; DetailFlagged.IsOn = reminder.Flagged;
            var customRepeat = reminder.Recurrence?.Supported == false;
            DetailRepeatChoice.IsEnabled = !customRepeat;
            DetailRepeatChoice.SelectedIndex = customRepeat ? -1 : (int)(reminder.Recurrence?.Frequency ?? 0);
            DetailRepeatInterval.Value = reminder.Recurrence?.Interval ?? 1;
            DetailTags.Text = string.Join(", ", reminder.Tags.Select(t => "#" + t));
            DetailTagsSection.Visibility = reminder.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppleDetailStatus.Text = !_demo && !_sync.IsLocalDemo && reminder.Dirty != 0 ? L10n.Get("PendingICloudSync") : "";
            UpdateRepeatControls();
        }
        _detailBaseline = reminder is null ? null : CaptureDetailValues();
        SaveButton.IsEnabled = false; _detailDirty = false; _loadingDetail = false;
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
    private void UpdateRepeatControls()
    {
        if (DetailRepeatChoice is null || DetailRepeatInterval is null || DetailRepeatHint is null) return;
        var repeats = DetailRepeatChoice.SelectedIndex > 0;
        DetailRepeatInterval.IsEnabled = DetailRepeatChoice.IsEnabled && repeats;
        DetailRepeatInterval.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
        DetailRepeatInterval.Header = DetailRepeatChoice.SelectedIndex switch { 1 => L10n.Get("EveryDays"), 2 => L10n.Get("EveryWeeks"), 3 => L10n.Get("EveryMonths"), 4 => L10n.Get("EveryYears"), _ => L10n.Get("Every") };
        DetailAllDayRow.Visibility = DetailDate.Date is not null ? Visibility.Visible : Visibility.Collapsed;
        DetailTime.IsEnabled = DetailDate.Date is not null && !DetailAllDay.IsOn;
        DetailTime.Visibility = DetailTime.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        var hint = !DetailRepeatChoice.IsEnabled ? L10n.Get("EditCustomRepeatOnAppleDevice")
            : repeats && DetailDate.Date is null ? L10n.Get("RepeatNeedsDueDate") : "";
        DetailRepeatHint.Text = hint;
        DetailRepeatHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAppleDetailVisibility();
        UpdatePaneLayout();
    }
    private void Detail_Changed(object sender, object e)
    {
        if (_loadingDetail || _selected is null) return;
        _detailDirty = CaptureDetailValues() != _detailBaseline;
        UpdateRepeatControls();
        var repeats = DetailRepeatChoice.SelectedIndex > 0 || _selected.Recurrence?.Supported == false;
        SaveButton.IsEnabled = _detailDirty && (!repeats || DetailDate.Date is not null)
            && (!DetailRepeatInterval.IsEnabled || double.IsFinite(DetailRepeatInterval.Value) && DetailRepeatInterval.Value is >= 1 and <= 999 && DetailRepeatInterval.Value == Math.Truncate(DetailRepeatInterval.Value));
    }
    private string CaptureDetailValues() => JsonSerializer.Serialize(new
    {
        Title = DetailTitle.Text, Notes = DetailNotes.Text, List = DetailList.SelectedValue as string ?? _selected?.ListId,
        Date = DetailDate.Date?.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Time = DetailDate.Date is null || DetailAllDay.IsOn ? null : DetailTime.Time.ToString(),
        AllDay = DetailAllDay.IsOn, Flagged = DetailFlagged.IsOn, Priority = DetailPriority.SelectedIndex,
        Repeat = DetailRepeatChoice.SelectedIndex,
        Interval = DetailRepeatChoice.SelectedIndex > 0 ? DetailRepeatInterval.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null
    });
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveDetailAsync();
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } target || _dialogOpen || _savingDetail || _detailTransition) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L10n.Get("DeleteReminder"), Content = L10n.Format("DeleteReminderDetails", target.DisplayTitle), PrimaryButtonText = L10n.Get("Delete"), CloseButtonText = L10n.Get("Cancel"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (_demo)
            {
                var children = DemoDescendants(target.Id); target.Deleted = true;
                foreach (var child in children) child.Deleted = true;
            }
            else await _sync.CallAsync("delete_reminder", new { id = target.Id });
            if (_selected?.Id == target.Id) ShowDetail(null);
            await LoadRemindersAsync();
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntDeleteReminder"), L10n.Error(error), InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }
    private async void Add_Click(object sender, RoutedEventArgs e) => await ShowNewReminderAsync();
    private async void NewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await ShowNewReminderAsync(); }
    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; if (AuthGate.Visibility == Visibility.Collapsed && SetupGuide.Visibility == Visibility.Collapsed) RevealSearch(); }
    private async void SettingsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await ShowSettingsAsync(); }
    private async Task ResolveConflictsAsync()
    {
        if (_demo || _dialogOpen || _savingDetail) return;
        _dialogOpen = true;
        try
        {
            var conflicts = await _sync.CallAsync<List<ConflictItem>>("conflicts", new { });
            foreach (var conflict in conflicts)
            {
                var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L10n.Get("ResolveSyncConflict"), Content = L10n.Get("ResolveConflictDescription"), PrimaryButtonText = L10n.Get("KeepThisPC"), SecondaryButtonText = L10n.Get("KeepICloud"), CloseButtonText = L10n.Get("Later") };
                var result = await dialog.ShowAsync(); if (result == ContentDialogResult.None) break;
                await _sync.CallAsync("resolve_conflict", new { id = conflict.Id, keep = result == ContentDialogResult.Primary ? "local" : "remote" });
            }
            await RefreshAllAsync();
        }
        finally { _dialogOpen = false; }
    }
    private void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        UpdateTitleBarTheme();
    }

    private async Task CheckForUpdatesAsync(bool manual, InfoBar? target = null, CancellationToken cancellationToken = default)
    {
        if (_demo || _updateBusy || _updateLifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested) return;
        // Portable and development launches only contact GitHub on an explicit check.
        if (!manual && (!AppUpdater.CanInstall || _dialogOpen)) return;
        var notice = target ?? UpdateInfoBar;
        var action = notice.ActionButton;
        using var checkLifetime = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token, cancellationToken);
        _updateBusy = true;
        try
        {
            var available = await _updater.CheckAsync(checkLifetime.Token);
            checkLifetime.Token.ThrowIfCancellationRequested();
            _availableUpdate = available;
            if (target is not null) UpdateInfoBar.IsOpen = false;
            if (_availableUpdate is null)
            {
                notice.IsOpen = false;
                if (manual) { notice.Severity = InfoBarSeverity.Success; notice.Title = L10n.Get("YoureUpToDate"); notice.Message = L10n.Get("NoUpdateAvailable"); notice.IsOpen = true; action.Visibility = Visibility.Collapsed; }
                return;
            }
            notice.Severity = InfoBarSeverity.Informational;
            notice.Title = L10n.Format("UpdateAvailableTitle", _availableUpdate.Version);
            notice.Message = AppUpdater.CanInstall ? L10n.Get("UpdateInstallDescription") : L10n.Get("UpdateReleaseLinkDescription");
            action.Content = AppUpdater.CanInstall ? L10n.Get("InstallAndRestart") : L10n.Get("OpenReleases");
            action.Visibility = Visibility.Visible;
            notice.IsOpen = true;
        }
        catch (OperationCanceledException) when (checkLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (checkLifetime.IsCancellationRequested) return;
            AppLog.Error("Could not check for updates", error);
            if (manual)
            {
                notice.Severity = InfoBarSeverity.Error; notice.Title = L10n.Get("CouldNotCheckForUpdates");
                notice.Message = error is OperationCanceledException ? L10n.Get("UpdateCheckTimedOut") : L10n.Error(error);
                notice.IsOpen = true; action.Visibility = Visibility.Collapsed;
            }
        }
        finally { _updateBusy = false; }
    }

    private async void Update_Click(object sender, RoutedEventArgs args) => await InstallUpdateAsync();

    private async Task InstallUpdateAsync()
    {
        if (_updateBusy || _dialogOpen || _savingDetail || _availableUpdate is null) return;
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
            var confirmation = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L10n.Get("InstallUpdate"), Content = L10n.Get("UpdateInstallConfirmation"), PrimaryButtonText = L10n.Get("InstallAndRestart"), CloseButtonText = L10n.Get("Later") };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            UpdateInfoBar.Severity = InfoBarSeverity.Informational;
            UpdateInfoBar.Title = L10n.Get("DownloadingUpdate");
            UpdateInfoBar.Message = L10n.Get("PreparingDownload"); UpdateInfoBar.IsOpen = true;
            UpdateButton.Content = L10n.Get("InstallAndRestart"); UpdateButton.Visibility = Visibility.Visible;
            // Progress<T> does not rely on a WinUI SynchronizationContext.
            var progress = new Progress<double>(percent => DispatcherQueue.TryEnqueue(() => UpdateInfoBar.Message = L10n.Format("UpdateDownloadProgress", percent)));
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
            UpdateInfoBar.Title = L10n.Get("CouldNotInstallUpdate"); UpdateInfoBar.Message = L10n.Error(error);
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
    private void ShowInfo(string title, string message, InfoBarSeverity severity) { AppInfoBar.ActionButton = null; AppInfoBar.Title = title; AppInfoBar.Message = message; AppInfoBar.Severity = severity; AppInfoBar.IsOpen = true; }

    private void Sync_EventReceived(object? sender, SyncEventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_windowClosed || _accountBusy || _syncDisposedForUpdate || _updateLifetime.IsCancellationRequested || !_sync.IsCurrentEvent(e)) return;
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
                else if (e.Data.Flag("restoring")) SetGateState(L10n.Get("SigningYouBackIn"));
                else if (AuthGate.Visibility == Visibility.Collapsed)
                    ShowInfo(L10n.Get("SignInToResumeSyncing"), L10n.Get("CachedRemindersAvailable"), InfoBarSeverity.Warning);
                else ShowLogin();
                break;
            case "sync_started": _lastSyncRequest = DateTimeOffset.UtcNow; ShowInfo(L10n.Get("Syncing"), L10n.Get("CheckingICloudForChanges"), InfoBarSeverity.Informational); break;
            case "sync_progress":
                var progressFallback = e.Data.Text("stage") is { Length: > 0 } stage
                    ? L10n.Format("SyncProgressFallback", stage) : L10n.Get("SyncProgressFallbackReminders");
                ShowInfo(L10n.Get("Syncing"), L10n.EventMessage(e.Data, progressFallback), InfoBarSeverity.Informational);
                break;
            case "sync_downloaded": await RefreshAllAsync(); break;
            case "sync_finished": AppInfoBar.IsOpen = false; await RefreshAllAsync(); break;
            case "sync_warning":
                ShowInfo(L10n.Get("SomeChangesHaventSynced"), e.Data.Text("code", "") == "CONFLICT" ? L10n.Get("SyncWarningConflictDescription") : L10n.Get("SyncWarningRejectedChangeDescription"), InfoBarSeverity.Warning);
                var recovery = new Button { Content = L10n.Get("ReviewSyncSettings") };
                recovery.Click += async (_, _) => await ShowSettingsAsync(1);
                AppInfoBar.ActionButton = recovery;
                break;
            case "sync_error": ShowInfo(L10n.Get("SyncFailed"), L10n.EventMessage(e.Data, L10n.Get("TryAgainShortly")), InfoBarSeverity.Error); break;
            case "push_failed": ShowInfo(L10n.Get("UploadFailed"), L10n.EventMessage(e.Data.Property("error"), L10n.Get("UploadRetryDescription")), InfoBarSeverity.Error); break;
            case "conflict": ShowInfo(L10n.Get("SyncConflict"), L10n.Get("SyncConflictDescription"), InfoBarSeverity.Warning); await RefreshStatusAsync(); break;
        }
    });
    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _windowClosed = true; _accountEpoch++; _searchTimer.Stop(); _reminderLoads.Invalidate(); _navigationLoads.Invalidate();
        _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop(); _paneHoverOpenTimer.Stop(); _paneHoverCloseTimer.Stop();
        _updateTimer.Stop(); _updateLifetime.Cancel();
        if (_notificationsRegistered) { try { AppNotificationManager.Default.Unregister(); } catch { } }
        if (!_syncDisposedForUpdate) await _sync.DisposeAsync();
    }
}
