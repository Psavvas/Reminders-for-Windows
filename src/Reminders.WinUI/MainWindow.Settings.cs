using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;
using System.Text.Json;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private static Button SettingsAction(string label) => new()
    {
        Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        Style = (Style)Application.Current.Resources["SettingsActionStyle"]
    };

    private static TextBlock SettingsDescription(string text) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["SettingsDescriptionStyle"]
    };

    private static Border SettingsCard(string title, params UIElement[] content)
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap });
        foreach (var child in content) panel.Children.Add(child);
        return new Border { Style = (Style)Application.Current.Resources["SettingsCardStyle"], Child = panel };
    }

    private static Grid SettingsRow(string title, string? description, FrameworkElement control, double controlWidth = 208)
    {
        AutomationProperties.SetName(control, title);
        var row = new Grid { ColumnSpacing = 20, RowSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description is not null) text.Children.Add(SettingsDescription(description));
        row.Children.Add(text); row.Children.Add(control); control.VerticalAlignment = VerticalAlignment.Center;
        row.SizeChanged += (_, _) =>
        {
            var compact = row.ActualWidth < 480;
            Grid.SetColumn(control, compact ? 0 : 1); Grid.SetRow(control, compact ? 1 : 0);
            Grid.SetColumnSpan(text, compact ? 2 : 1); Grid.SetColumnSpan(control, compact ? 2 : 1);
            var stretch = compact && control is ComboBox;
            control.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            control.Width = stretch ? double.NaN : Math.Min(controlWidth, row.ActualWidth);
        };
        return row;
    }

    private static StackPanel SettingsPage(string title, string description)
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(SettingsDescription(description));
        return panel;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync(int initialCategory = 0)
    {
        if (_dialogOpen || _savingDetail || _accountBusy || _verificationBusy || AppTourActive || SetupGuide.Visibility == Visibility.Visible || WelcomeScreen.Visibility == Visibility.Visible) return;
        _dialogOpen = true;
        SizeChangedEventHandler? resize = null;
        using var updateCancellation = new CancellationTokenSource();
        Task updateCheck = Task.CompletedTask;
        Task recoveryWork = Task.CompletedTask;
        CancellationTokenSource? recoveryCancellation = null;
        try
        {
            JsonElement auth = default, syncStatus = default;
            if (!_demo)
            {
                if (!_sync.IsRunning) await _sync.StartAsync();
                auth = await _sync.CallAsync("auth_status", new { });
                syncStatus = await _sync.CallAsync("sync_status", new { });
                _settings = await _sync.CallAsync("settings", new { });
            }
            var local = _demo || _sync.IsLocalDemo;
            if (IsSyncRecoveryPreview) syncStatus = JsonSerializer.SerializeToElement(new { pending_pushes = 3, conflicts = 1 });
            var choiceStyle = (Style)Application.Current.Resources["SettingsChoiceStyle"];
            var theme = new ComboBox { Style = choiceStyle, ItemsSource = new[] { L10n.Get("MatchWindows"), L10n.Get("Light"), L10n.Get("Dark") }, SelectedIndex = _settings.Text("theme", "system") switch { "light" => 1, "dark" => 2, _ => 0 } };
            var interfaceStyle = new ComboBox { Style = choiceStyle, ItemsSource = new[] { L10n.Get("WindowsInterfaceStyle"), L10n.Get("AppleInterfaceStyle") }, SelectedIndex = IsAppleStyle ? 1 : 0 };
            var paneSizing = new ComboBox { Style = choiceStyle, ItemsSource = new[] { L10n.Get("Automatic"), L10n.Get("Custom") }, SelectedIndex = _uiPreferences.DetailPaneFraction is null ? 0 : 1 };
            var detailWidth = new Slider { Header = L10n.Get("PreferredDetailsWidth"), Minimum = 0, Maximum = 100, StepFrequency = 1, Value = Math.Clamp(_uiPreferences.DetailPaneFraction ?? .5, 0, 1) * 100 };
            var customLayout = new StackPanel { Spacing = 8, Visibility = paneSizing.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed };
            customLayout.Children.Add(detailWidth);
            customLayout.Children.Add(SettingsDescription(L10n.Get("DetailsPaneSizingDescription")));
            paneSizing.SelectionChanged += (_, _) => customLayout.Visibility = paneSizing.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            var animation = new ToggleSwitch { IsOn = _uiPreferences.CompletionAnimation, OnContent = "", OffContent = "", MinWidth = 0 };
            var notifications = new ToggleSwitch { IsOn = _settings.Flag("notifications_enabled"), OnContent = "", OffContent = "", MinWidth = 0 };
            var sync = new NumberBox { Minimum = 1, Maximum = 60, MinWidth = 0, MinHeight = 40, Value = Math.Clamp(_settings.Number("sync_minutes"), 1, 60), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var defaultList = new ComboBox { Style = choiceStyle, DisplayMemberPath = "Title", SelectedValuePath = "Id" };
            defaultList.ItemsSource = new[] { new ReminderList { Id = "", Title = L10n.Get("FirstListInSidebar") } }.Concat(OrderedLists()).ToList();
            defaultList.SelectedValue = _settings.Text("default_list_id", ""); if (defaultList.SelectedIndex < 0) defaultList.SelectedIndex = 0;

            var general = SettingsPage(L10n.Get("General"), L10n.Get("GeneralSettingsDescription"));
            general.Children.Add(SettingsCard(L10n.Get("Appearance"), SettingsRow(L10n.Get("InterfaceStyle"), null, interfaceStyle), SettingsRow(L10n.Get("Theme"), null, theme), SettingsRow(L10n.Get("CompletionAnimation"), L10n.Get("AnimateRemindersWhenCompleted"), animation, 60)));
            general.Children.Add(SettingsCard(L10n.Get("Layout"), SettingsRow(L10n.Get("DetailsPane"), null, paneSizing), customLayout));
            general.Children.Add(SettingsCard(L10n.Get("Reminders"), SettingsRow(L10n.Get("DefaultList"), null, defaultList), SettingsRow(L10n.Get("DueDateNotifications"), null, notifications, 60)));

            var syncPage = SettingsPage(L10n.Get("SyncStorage"), L10n.Get("SyncSettingsDescription"));
            var syncNow = SettingsAction(L10n.Get("SyncNow")); syncNow.IsEnabled = !local && auth.Flag("authenticated");
            var syncNotice = new InfoBar { IsOpen = false, IsClosable = true };
            var syncContent = new StackPanel { Spacing = 16 };
            var lastSync = DateTimeOffset.TryParse(syncStatus.Text("last_sync"), out var time) ? L10n.Format("LastSyncedAt", time.ToLocalTime()) : L10n.Get("NeverSynced");
            syncContent.Children.Add(SettingsDescription(local ? L10n.Get("LocalAccountSyncDisabled") : lastSync));
            if (!local) syncContent.Children.Add(SettingsRow(L10n.Get("SyncInterval"), L10n.Get("MinutesBetweenAutomaticSyncs"), sync, 128));
            if (!local) syncContent.Children.Add(syncNow);
            syncContent.Children.Add(syncNotice);
            syncPage.Children.Add(SettingsCard(local ? L10n.Get("LocalStorage") : L10n.Get("ICloudSync"), syncContent));
            syncNow.Click += async (_, _) =>
            {
                syncNow.IsEnabled = false;
                try { await _sync.CallAsync("sync", new { }); syncNotice.Severity = InfoBarSeverity.Informational; syncNotice.Message = L10n.Get("SyncRequestedNotice"); }
                catch (Exception error) { syncNotice.Severity = InfoBarSeverity.Error; syncNotice.Message = L10n.Error(error); }
                finally { syncNotice.IsOpen = true; syncNow.IsEnabled = true; }
            };
            var discardUnsynced = SettingsAction(L10n.Get("DiscardUnsyncedChanges"));
            var unsyncedStatus = SettingsDescription("");
            void UpdateUnsyncedStatus(JsonElement status)
            {
                syncStatus = status;
                var count = status.Number("pending_pushes") + status.Number("conflicts");
                unsyncedStatus.Text = count > 0 ? L10n.Format("QueuedChangesReviewDescription", count) : L10n.Get("NoUnsyncedChangesOrConflicts");
                discardUnsynced.IsEnabled = (!local || IsSyncRecoveryPreview) && !_updateBusy && count > 0;
            }
            UpdateUnsyncedStatus(syncStatus);
            syncPage.Children.Add(SettingsCard(L10n.Get("UnsyncedChanges"), SettingsDescription(local && !IsSyncRecoveryPreview
                ? L10n.Get("LocalAccountRecoveryDescription")
                : L10n.Get("DiscardSelectedChangesDescription")), unsyncedStatus, discardUnsynced));
            var clearCache = SettingsAction(L10n.Get("ClearAppCacheAction")); clearCache.IsEnabled = !local && !_updateBusy;
            var pending = syncStatus.Number("pending_pushes") + syncStatus.Number("conflicts");
            syncPage.Children.Add(SettingsCard(L10n.Get("AppCache"), SettingsDescription(local
                ? L10n.Get("LocalAccountClearCacheUnavailable")
                : L10n.Get("ClearCacheSettingsDescription")),
                SettingsDescription(pending > 0 ? L10n.Format("CacheClearPendingChangesWarning", pending) : L10n.Get("NoUnsyncedChangesOrConflicts")), clearCache));
            var setup = SettingsAction(L10n.Get("ICloudSetupAndTroubleshooting")); syncPage.Children.Add(setup);

            var account = SettingsPage(L10n.Get("Account"), local ? L10n.Get("LocalAccountSettingsDescription") : L10n.Get("AppleAccountSettingsDescription"));
            var accountName = local ? (_demo ? L10n.Get("SampleData") : L10n.Get("LocalAccount")) : auth.Text("apple_id", L10n.Get("ICloudAccount"));
            if (string.IsNullOrWhiteSpace(accountName)) accountName = L10n.Get("ICloudAccount");
            var signOut = SettingsAction(_demo ? L10n.Get("LeaveSampleData") : _sync.IsLocalDemo ? L10n.Get("LeaveLocalAccount") : L10n.Get("SignOut"));
            signOut.IsEnabled = !_updateBusy && (local || !auth.Flag("signed_out") && (auth.Flag("authenticated") || auth.Flag("has_cache") || auth.Flag("needs_2fa") || auth.Flag("restoring")));
            account.Children.Add(SettingsCard(accountName, SettingsDescription(local ? L10n.Get("RemindersStayOnThisPC") : auth.Flag("authenticated") ? L10n.Get("SignedIn") : L10n.Get("SignInNeeded")),
                SettingsDescription(local ? L10n.Get("LeaveSessionDescription") : L10n.Get("SignOutDescription")), signOut));

            var about = SettingsPage(L10n.Get("About"), L10n.Get("AboutSettingsDescription"));
            var updates = SettingsAction(L10n.Get("CheckForUpdates")); updates.IsEnabled = !_demo;
            var updateProgress = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(updateProgress, L10n.Get("CheckingForUpdates"));
            var updateControls = new Grid { ColumnSpacing = 12 };
            updateControls.ColumnDefinitions.Add(new()); updateControls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            Grid.SetColumn(updateProgress, 1); updateControls.Children.Add(updates); updateControls.Children.Add(updateProgress);
            var installUpdate = SettingsAction(""); installUpdate.Visibility = Visibility.Collapsed;
            var updateNotice = new InfoBar { IsOpen = false, IsClosable = false, ActionButton = installUpdate };
            var privacy = SettingsAction(L10n.Get("PrivacyPolicy"));
            about.Children.Add(SettingsCard(L10n.Get("AppTitle"), SettingsDescription(L10n.Format("Version", AppUpdater.CurrentVersion.ToString(3))), updateControls, updateNotice));
            updates.Click += async (_, _) =>
            {
                if (updateCancellation.IsCancellationRequested || _updateLifetime.IsCancellationRequested) return;
                if (_updateBusy)
                {
                    updateNotice.Severity = InfoBarSeverity.Informational;
                    updateNotice.Title = L10n.Get("UpdateCheckInProgress");
                    updateNotice.Message = L10n.Get("TryAgainShortly");
                    installUpdate.Visibility = Visibility.Collapsed; updateNotice.IsOpen = true;
                    return;
                }
                updates.IsEnabled = false; ((TextBlock)updates.Content).Text = L10n.Get("Checking");
                updateProgress.Visibility = Visibility.Visible; updateProgress.IsActive = true;
                updateNotice.IsOpen = false; installUpdate.Visibility = Visibility.Collapsed;
                try
                {
                    updateCheck = CheckForUpdatesAsync(true, updateNotice, updateCancellation.Token);
                    await updateCheck;
                }
                finally
                {
                    updateProgress.IsActive = false; updateProgress.Visibility = Visibility.Collapsed;
                    ((TextBlock)updates.Content).Text = L10n.Get("CheckForUpdates"); updates.IsEnabled = true;
                }
            };
            about.Children.Add(SettingsCard(L10n.Get("Links"), privacy, new HyperlinkButton { Content = new TextBlock { Text = L10n.Get("ViewSourceOnGitHub"), TextWrapping = TextWrapping.Wrap }, NavigateUri = new Uri("https://github.com/Psavvas/Reminders-for-Windows"), Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Left }));
            about.Children.Add(new Expander { Header = L10n.Get("AboutThisApp"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = SettingsDescription(L10n.Get("AboutAppDisclaimer")) });
            var appTour = SettingsAction(L10n.Get("ReplayAppTour"));
            appTour.IsEnabled = AuthGate.Visibility == Visibility.Collapsed;
            about.Children.Add(SettingsCard(L10n.Get("GettingStarted"), SettingsDescription(L10n.Get("ReplayAppTourDescription")), appTour));

            var names = new[] { L10n.Get("General"), L10n.Get("SyncStorage"), L10n.Get("Account"), L10n.Get("About") };
            var pages = new[] { general, syncPage, account, about };
            var nav = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, Padding = new Thickness(0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(nav, ScrollBarVisibility.Disabled);
            var symbols = new[] { Symbol.Setting, Symbol.Sync, Symbol.Contact, Symbol.Help };
            for (var i = 0; i < names.Length; i++)
            {
                var label = new Grid { ColumnSpacing = 12 };
                label.ColumnDefinitions.Add(new() { Width = new GridLength(20) }); label.ColumnDefinitions.Add(new());
                var icon = new SymbolIcon { Symbol = symbols[i], Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
                var text = new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1); label.Children.Add(icon); label.Children.Add(text);
                var item = new ListViewItem { Content = label, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4),
                    MinHeight = 48, HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(6) };
                AutomationProperties.SetName(item, names[i]); nav.Items.Add(item);
            }
            AutomationProperties.SetName(nav, L10n.Get("SettingsCategories"));
            var compactNav = new ComboBox { Style = choiceStyle, ItemsSource = names, Margin = new Thickness(0, 0, 0, 16) };
            AutomationProperties.SetName(compactNav, L10n.Get("SettingsCategory"));
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 0, 8, 0) };
            var layout = new Grid { ColumnSpacing = 24 };
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(200) }); layout.ColumnDefinitions.Add(new());
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new());
            Grid.SetColumnSpan(compactNav, 2); Grid.SetRow(nav, 1); Grid.SetRow(scroll, 1); Grid.SetColumn(scroll, 1);
            layout.Children.Add(compactNav); layout.Children.Add(nav); layout.Children.Add(scroll);
            var selecting = false;
            SyncRecoveryReview? recoveryReview = null;
            var recoveryBusy = false; var recoveryLoading = false; var returnAfterCancel = false;
            void SelectCategory(int index)
            {
                if (selecting || index < 0 || index >= pages.Length) return;
                selecting = true;
                nav.SelectedIndex = index; compactNav.SelectedIndex = index; scroll.Content = pages[index];
                if (scroll.XamlRoot is not null) scroll.ChangeView(null, 0, null);
                selecting = false;
            }
            nav.SelectionChanged += (_, _) => SelectCategory(nav.SelectedIndex);
            compactNav.SelectionChanged += (_, _) => SelectCategory(compactNav.SelectedIndex);
            void Resize()
            {
                layout.Width = Math.Clamp(Root.ActualWidth - 100, 280, 900); layout.Height = Math.Clamp(Root.ActualHeight - 230, 200, 580);
                var compact = layout.Width < 720;
                compactNav.Visibility = compact ? Visibility.Visible : Visibility.Collapsed; nav.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                layout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 200); layout.ColumnSpacing = compact ? 0 : 24;
                if (recoveryReview is not null) { recoveryReview.View.Width = layout.Width; recoveryReview.View.Height = layout.Height; }
            }
            resize = (_, _) => Resize(); Root.SizeChanged += resize; Resize(); SelectCategory(Math.Clamp(initialCategory, 0, pages.Length - 1));
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = L10n.Get("Settings"), Content = layout,
                PrimaryButtonText = L10n.Get("SaveChanges"), CloseButtonText = L10n.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
            dialog.Resources["ContentDialogMaxWidth"] = 1020d;
            dialog.Resources["ContentDialogMinWidth"] = 0d;
            dialog.Resources["ExpanderChevronMargin"] = new Thickness(16, 0, 16, 0);
            string? action = null;
            void Request(string value) { action = value; dialog.Hide(); }
            signOut.Click += (_, _) => Request("sign_out"); clearCache.Click += (_, _) => Request("clear_cache");
            setup.Click += (_, _) => Request("setup");
            appTour.Click += (_, _) => Request("tour");
            installUpdate.Click += async (_, _) =>
            {
                if (AppUpdater.CanInstall) { Request("install_update"); return; }
                try { await global::Windows.System.Launcher.LaunchUriAsync(new Uri($"https://github.com/{AppUpdater.Repository}/releases/latest")); }
                catch (Exception error) { updateNotice.Severity = InfoBarSeverity.Error; updateNotice.Title = L10n.Get("CouldntOpenReleases"); updateNotice.Message = L10n.Error(error); updateNotice.IsOpen = true; }
            };
            void RestoreSettings()
            {
                recoveryReview = null; dialog.Content = layout; dialog.Title = L10n.Get("Settings");
                dialog.PrimaryButtonText = L10n.Get("SaveChanges"); dialog.CloseButtonText = L10n.Get("Cancel");
                dialog.DefaultButton = ContentDialogButton.Primary;
                dialog.IsPrimaryButtonEnabled = double.IsFinite(sync.Value) && sync.Value is >= 1 and <= 60;
            }
            async Task LoadRecoveryReview()
            {
                if (recoveryLoading || recoveryBusy) return;
                recoveryLoading = true; discardUnsynced.IsEnabled = false;
                if (recoveryReview is { } existingReview) { existingReview.Refresh.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false; }
                try
                {
                    var rows = await ReadUnsyncedReview(updateCancellation.Token);
                    if (updateCancellation.IsCancellationRequested) return;
                    if (rows.Count == 0)
                    {
                        RestoreSettings(); UpdateUnsyncedStatus(await _sync.CallAsync("sync_status", new { }));
                        syncNotice.Severity = InfoBarSeverity.Informational; syncNotice.Message = L10n.Get("NoChangesToDiscard"); syncNotice.IsOpen = true; return;
                    }
                    recoveryReview = new SyncRecoveryReview(rows, () =>
                    {
                        var count = recoveryReview?.Selected.Count ?? 0;
                        dialog.IsPrimaryButtonEnabled = !recoveryBusy && !recoveryLoading && recoveryReview?.CanDiscard == true && count is > 0 and <= 1000;
                        dialog.PrimaryButtonText = count == 0 ? L10n.Get("DiscardSelected") : L10n.Format("DiscardSelectedWithCount", count);
                    }, _detailDirty);
                    recoveryReview.Refresh.Click += async (_, _) => { recoveryWork = LoadRecoveryReview(); await recoveryWork; };
                    recoveryReview.View.Width = layout.Width; recoveryReview.View.Height = layout.Height;
                    dialog.Content = recoveryReview.View; dialog.Title = L10n.Get("DiscardUnsyncedChanges");
                    dialog.PrimaryButtonText = L10n.Get("DiscardSelected"); dialog.CloseButtonText = L10n.Get("Back");
                    dialog.DefaultButton = ContentDialogButton.Close; dialog.IsPrimaryButtonEnabled = false;
                }
                catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
                catch (Exception error)
                {
                    if (recoveryReview is { } review) { review.Notice.Title = L10n.Get("CouldntLoadChanges"); review.Notice.Message = L10n.Error(error); review.Notice.Severity = InfoBarSeverity.Error; review.Notice.IsOpen = true; }
                    else { syncNotice.Severity = InfoBarSeverity.Error; syncNotice.Message = L10n.Error(error); syncNotice.IsOpen = true; }
                }
                finally
                {
                    recoveryLoading = false;
                    if (recoveryReview is { } review) review.Refresh.IsEnabled = true;
                    if (!updateCancellation.IsCancellationRequested) UpdateUnsyncedStatus(syncStatus);
                }
            }
            discardUnsynced.Click += async (_, _) => { recoveryWork = LoadRecoveryReview(); await recoveryWork; };
            async Task DiscardSelection(SyncRecoveryReview review)
            {
                var selected = review.Selected;
                var discardCommitted = false;
                recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(updateCancellation.Token);
                recoveryBusy = true; returnAfterCancel = false; review.SetBusy(true);
                dialog.IsPrimaryButtonEnabled = false; dialog.CloseButtonText = L10n.Get("Cancel");
                try
                {
                    if (IsSyncRecoveryPreview) throw new InvalidOperationException(L10n.Get("ThisSamplePreviewCannotDiscardReminders"));
                    var outcome = await _sync.CallAsync("discard_unsynced", new { confirmed = true,
                        targets = selected.Select(row => new { id = row.Id, version = row.Version }).ToArray() }, recoveryCancellation.Token);
                    discardCommitted = true;
                    if (_selected is { } open && selected.Any(row => row.Id == open.Id)) ShowDetail(null);
                    await RefreshAllAsync();
                    UpdateUnsyncedStatus(await _sync.CallAsync("sync_status", new { }));
                    RestoreSettings(); syncNotice.Severity = InfoBarSeverity.Success; syncNotice.Title = L10n.Get("SelectedChangesDiscarded");
                    syncNotice.Message = L10n.Format("DiscardChangesResult", outcome.Number("removed_local"), outcome.Number("restored_from_icloud"));
                    syncNotice.IsOpen = true;
                }
                catch (OperationCanceledException)
                {
                    review.Notice.Title = discardCommitted ? L10n.Get("ChangesDiscarded") : L10n.Get("DiscardCancelled"); review.Notice.Message = discardCommitted ? L10n.Get("DiscardChangesRefreshPrompt") : L10n.Get("NoChangesWereDiscarded");
                    review.Notice.Severity = InfoBarSeverity.Informational; review.Notice.IsOpen = true;
                }
                catch (Exception error)
                {
                    review.Notice.Title = discardCommitted ? L10n.Get("ChangesDiscardedRefreshFailed") : L10n.Get("CouldntDiscardChanges"); review.Notice.Message = L10n.Error(error);
                    review.Notice.Severity = InfoBarSeverity.Error; review.Notice.IsOpen = true;
                    // A changed selection must be reviewed again with fresh versions.
                    if (error is SyncException { Code: "STALE_SELECTION" }) review.CanDiscard = false;
                    review.ClearSelection();
                }
                finally
                {
                    recoveryBusy = false; review.SetBusy(false); recoveryCancellation.Dispose(); recoveryCancellation = null;
                    if (returnAfterCancel) RestoreSettings();
                    else if (recoveryReview is not null) { dialog.CloseButtonText = L10n.Get("Back"); dialog.IsPrimaryButtonEnabled = false; }
                }
            }
            dialog.PrimaryButtonClick += async (_, e) =>
            {
                if (recoveryReview is not { } review) return;
                e.Cancel = true; var deferral = e.GetDeferral();
                try { recoveryWork = DiscardSelection(review); await recoveryWork; }
                finally { deferral.Complete(); }
            };
            dialog.Closing += (_, e) =>
            {
                if (!recoveryBusy) return;
                e.Cancel = true; returnAfterCancel = true; recoveryCancellation?.Cancel();
            };
            var showingPrivacy = false;
            privacy.Click += (_, _) => { dialog.Content = CreatePrivacyPolicyContent(); dialog.Title = L10n.Get("PrivacyPolicy"); dialog.PrimaryButtonText = ""; dialog.CloseButtonText = L10n.Get("Back"); dialog.DefaultButton = ContentDialogButton.Close; showingPrivacy = true; };
            dialog.CloseButtonClick += (_, e) =>
            {
                if (recoveryReview is not null)
                {
                    e.Cancel = true;
                    if (recoveryBusy) { returnAfterCancel = true; recoveryCancellation?.Cancel(); }
                    else RestoreSettings();
                    return;
                }
                if (!showingPrivacy) return;
                e.Cancel = true; showingPrivacy = false; dialog.Content = layout; dialog.Title = L10n.Get("Settings");
                dialog.PrimaryButtonText = L10n.Get("SaveChanges"); dialog.CloseButtonText = L10n.Get("Cancel"); dialog.DefaultButton = ContentDialogButton.Primary;
            };
            sync.ValueChanged += (_, _) => { if (recoveryReview is null) dialog.IsPrimaryButtonEnabled = double.IsFinite(sync.Value) && sync.Value >= 1 && sync.Value <= 60; };
            var result = await dialog.ShowAsync();
            updateCancellation.Cancel();
            await updateCheck;
            await recoveryWork;
            _dialogOpen = false;
            if (action == "sign_out") { await SignOutAsync(); return; }
            if (action == "clear_cache") { await ClearCacheAsync(); return; }
            if (action == "setup") { ShowSetupGuide(); return; }
            if (action == "tour") { ShowAppTour(); return; }
            if (action == "install_update") { await InstallUpdateAsync(); return; }
            if (result != ContentDialogResult.Primary || !double.IsFinite(sync.Value)) return;
            var themeValue = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
            var values = new { theme = themeValue, sync_minutes = (int)Math.Clamp(sync.Value, 1, 60), notifications_enabled = notifications.IsOn, default_list_id = defaultList.SelectedValue as string };
            _settings = _demo ? JsonSerializer.SerializeToElement(values) : await _sync.CallAsync("set_settings", values);
            _uiPreferences.CompletionAnimation = animation.IsOn; _uiPreferences.DetailPaneFraction = paneSizing.SelectedIndex == 0 ? null : detailWidth.Value / 100;
            _uiPreferences.InterfaceStyle = interfaceStyle.SelectedIndex == 1 ? "apple" : "windows";
            SaveUiPreferences(); ApplyTheme(themeValue); ApplyInterfaceStyle();
        }
        catch (Exception error)
        {
            ShowInfo(L10n.Get("CouldntOpenSettings"), L10n.Error(error), InfoBarSeverity.Error);
            if (AuthGate.Visibility != Visibility.Collapsed) { GateError.Message = L10n.Error(error); GateError.IsOpen = true; }
        }
        finally
        {
            updateCancellation.Cancel();
            await updateCheck;
            await recoveryWork;
            if (resize is not null) Root.SizeChanged -= resize;
            _dialogOpen = false;
            TryShowFirstAppTour();
        }
    }
}
