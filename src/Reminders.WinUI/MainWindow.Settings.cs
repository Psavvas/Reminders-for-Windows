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
        if (_dialogOpen || _accountBusy || _verificationBusy || SetupGuide.Visibility == Visibility.Visible) return;
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
            var theme = new ComboBox { Style = choiceStyle, ItemsSource = new[] { "Match Windows", "Light", "Dark" }, SelectedIndex = _settings.Text("theme", "system") switch { "light" => 1, "dark" => 2, _ => 0 } };
            var paneSizing = new ComboBox { Style = choiceStyle, ItemsSource = new[] { "Automatic", "Custom" }, SelectedIndex = _uiPreferences.DetailPaneFraction is null ? 0 : 1 };
            var detailWidth = new Slider { Header = "Preferred details width (%)", Minimum = 0, Maximum = 100, StepFrequency = 1, Value = Math.Clamp(_uiPreferences.DetailPaneFraction ?? .5, 0, 1) * 100 };
            var customLayout = new StackPanel { Spacing = 8, Visibility = paneSizing.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed };
            customLayout.Children.Add(detailWidth);
            customLayout.Children.Add(SettingsDescription("Splits stay readable. At either end of the slider, opening a task fills the content area. Use Back to return to the list."));
            paneSizing.SelectionChanged += (_, _) => customLayout.Visibility = paneSizing.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            var animation = new ToggleSwitch { IsOn = _uiPreferences.CompletionAnimation, OnContent = "", OffContent = "", MinWidth = 0 };
            var notifications = new ToggleSwitch { IsOn = _settings.Flag("notifications_enabled"), OnContent = "", OffContent = "", MinWidth = 0 };
            var sync = new NumberBox { Minimum = 1, Maximum = 60, MinWidth = 0, MinHeight = 40, Value = Math.Clamp(_settings.Number("sync_minutes"), 1, 60), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var defaultList = new ComboBox { Style = choiceStyle, DisplayMemberPath = "Title", SelectedValuePath = "Id" };
            defaultList.ItemsSource = new[] { new ReminderList { Id = "", Title = "First list in sidebar" } }.Concat(OrderedLists()).ToList();
            defaultList.SelectedValue = _settings.Text("default_list_id", ""); if (defaultList.SelectedIndex < 0) defaultList.SelectedIndex = 0;

            var general = SettingsPage("General", "Appearance and reminder defaults.");
            general.Children.Add(SettingsCard("Appearance", SettingsRow("Theme", null, theme), SettingsRow("Completion animation", "Animate reminders when completed.", animation, 60)));
            general.Children.Add(SettingsCard("Layout", SettingsRow("Details pane", null, paneSizing), customLayout));
            general.Children.Add(SettingsCard("Reminders", SettingsRow("Default list", null, defaultList), SettingsRow("Due-date notifications", null, notifications, 60)));

            var syncPage = SettingsPage("Sync & storage", "Keep your reminders up to date and recover from sync problems.");
            var syncNow = SettingsAction("Sync now"); syncNow.IsEnabled = !local && auth.Flag("authenticated");
            var syncNotice = new InfoBar { IsOpen = false, IsClosable = true };
            var syncContent = new StackPanel { Spacing = 16 };
            var lastSync = DateTimeOffset.TryParse(syncStatus.Text("last_sync"), out var time) ? $"Last synced {time.ToLocalTime():g}" : "No completed sync yet";
            syncContent.Children.Add(SettingsDescription(local ? "This account stores reminders on this PC. iCloud sync is off." : lastSync));
            if (!local) syncContent.Children.Add(SettingsRow("Sync interval", "Minutes between automatic syncs.", sync, 128));
            if (!local) syncContent.Children.Add(syncNow);
            syncContent.Children.Add(syncNotice);
            syncPage.Children.Add(SettingsCard(local ? "Local storage" : "iCloud sync", syncContent));
            syncNow.Click += async (_, _) =>
            {
                syncNow.IsEnabled = false;
                try { await _sync.CallAsync("sync", new { }); syncNotice.Severity = InfoBarSeverity.Informational; syncNotice.Message = "Sync requested. Changes will be checked in the background."; }
                catch (Exception error) { syncNotice.Severity = InfoBarSeverity.Error; syncNotice.Message = error.Message; }
                finally { syncNotice.IsOpen = true; syncNow.IsEnabled = true; }
            };
            var discardUnsynced = SettingsAction("Discard unsynced changes");
            var unsyncedStatus = SettingsDescription("");
            void UpdateUnsyncedStatus(JsonElement status)
            {
                syncStatus = status;
                var count = status.Number("pending_pushes") + status.Number("conflicts");
                unsyncedStatus.Text = count > 0 ? $"{count} queued changes or conflicts. Review the affected tasks before discarding anything." : "No unsynced changes or conflicts.";
                discardUnsynced.IsEnabled = (!local || IsSyncRecoveryPreview) && !_updateBusy && count > 0;
            }
            UpdateUnsyncedStatus(syncStatus);
            syncPage.Children.Add(SettingsCard("Unsynced changes", SettingsDescription(local && !IsSyncRecoveryPreview
                ? "These reminders exist only on this PC. There is no iCloud copy to restore."
                : "Remove selected local-only tasks or restore selected tasks from iCloud without clearing the app cache."), unsyncedStatus, discardUnsynced));
            var clearCache = SettingsAction("Clear app cache"); clearCache.IsEnabled = !local && !_updateBusy;
            var pending = syncStatus.Number("pending_pushes") + syncStatus.Number("conflicts");
            syncPage.Children.Add(SettingsCard("App cache", SettingsDescription(local
                ? "These reminders exist only on this PC, so clearing a downloaded cache is unavailable."
                : "Remove downloaded reminders and sync history, then download a fresh copy. Sign-in and app preferences are kept."),
                SettingsDescription(pending > 0 ? $"{pending} unsynced changes or conflicts. Clearing the cache will ask before discarding them." : "No unsynced changes or conflicts."), clearCache));
            var setup = SettingsAction("iCloud setup and troubleshooting"); syncPage.Children.Add(setup);

            var account = SettingsPage("Account", local ? "Manage your local session." : "Manage the Apple Account used for your reminders.");
            var accountName = local ? (_demo ? "Sample data" : "Local account") : auth.Text("apple_id", "iCloud account");
            if (string.IsNullOrWhiteSpace(accountName)) accountName = "iCloud account";
            var signOut = SettingsAction(_demo ? "Leave sample data" : _sync.IsLocalDemo ? "Leave local account" : "Sign out");
            signOut.IsEnabled = !_updateBusy && (local || !auth.Flag("signed_out") && (auth.Flag("authenticated") || auth.Flag("has_cache") || auth.Flag("needs_2fa") || auth.Flag("restoring")));
            account.Children.Add(SettingsCard(accountName, SettingsDescription(local ? "Reminders stay on this PC." : auth.Flag("authenticated") ? "Signed in" : "Sign-in needed"),
                SettingsDescription(local ? "Leave this session to return to sign-in." : "Sign out on this PC. Downloaded reminders and queued edits will be kept until you sign back in or clear the cache."), signOut));

            var about = SettingsPage("About", "App information and support.");
            var updates = SettingsAction("Check for updates"); updates.IsEnabled = !_demo;
            var updateProgress = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(updateProgress, "Checking for updates");
            var updateControls = new Grid { ColumnSpacing = 12 };
            updateControls.ColumnDefinitions.Add(new()); updateControls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            Grid.SetColumn(updateProgress, 1); updateControls.Children.Add(updates); updateControls.Children.Add(updateProgress);
            var installUpdate = SettingsAction(""); installUpdate.Visibility = Visibility.Collapsed;
            var updateNotice = new InfoBar { IsOpen = false, IsClosable = false, ActionButton = installUpdate };
            var privacy = SettingsAction("Privacy policy");
            about.Children.Add(SettingsCard("Reminders for Windows", SettingsDescription($"Version {AppUpdater.CurrentVersion.ToString(3)}"), updateControls, updateNotice));
            updates.Click += async (_, _) =>
            {
                if (updateCancellation.IsCancellationRequested || _updateLifetime.IsCancellationRequested) return;
                if (_updateBusy)
                {
                    updateNotice.Severity = InfoBarSeverity.Informational;
                    updateNotice.Title = "An update check is already running";
                    updateNotice.Message = "Try again in a moment.";
                    installUpdate.Visibility = Visibility.Collapsed; updateNotice.IsOpen = true;
                    return;
                }
                updates.IsEnabled = false; ((TextBlock)updates.Content).Text = "Checking…";
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
                    ((TextBlock)updates.Content).Text = "Check for updates"; updates.IsEnabled = true;
                }
            };
            about.Children.Add(SettingsCard("Links", privacy, new HyperlinkButton { Content = new TextBlock { Text = "View source on GitHub", TextWrapping = TextWrapping.Wrap }, NavigateUri = new Uri("https://github.com/Psavvas/Reminders-for-Windows"), Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Left }));
            about.Children.Add(new Expander { Header = "About this app", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = SettingsDescription("Reminders for Windows is an independent app and is not affiliated with, endorsed by, or sponsored by Apple Inc. or Microsoft Corporation. iCloud is a trademark of Apple Inc. Windows is a trademark of Microsoft Corporation. Sync uses unofficial iCloud interfaces that may change or stop working without notice.") });

            var names = new[] { "General", "Sync & storage", "Account", "About" };
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
            AutomationProperties.SetName(nav, "Settings categories");
            var compactNav = new ComboBox { Style = choiceStyle, ItemsSource = names, Margin = new Thickness(0, 0, 0, 16) };
            AutomationProperties.SetName(compactNav, "Settings category");
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
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "Settings", Content = layout,
                PrimaryButtonText = "Save changes", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            dialog.Resources["ContentDialogMaxWidth"] = 1020d;
            dialog.Resources["ContentDialogMinWidth"] = 0d;
            dialog.Resources["ExpanderChevronMargin"] = new Thickness(16, 0, 16, 0);
            string? action = null;
            void Request(string value) { action = value; dialog.Hide(); }
            signOut.Click += (_, _) => Request("sign_out"); clearCache.Click += (_, _) => Request("clear_cache");
            setup.Click += (_, _) => Request("setup");
            installUpdate.Click += async (_, _) =>
            {
                if (AppUpdater.CanInstall) { Request("install_update"); return; }
                try { await global::Windows.System.Launcher.LaunchUriAsync(new Uri($"https://github.com/{AppUpdater.Repository}/releases/latest")); }
                catch (Exception error) { updateNotice.Severity = InfoBarSeverity.Error; updateNotice.Title = "Couldn't open releases"; updateNotice.Message = error.Message; updateNotice.IsOpen = true; }
            };
            void RestoreSettings()
            {
                recoveryReview = null; dialog.Content = layout; dialog.Title = "Settings";
                dialog.PrimaryButtonText = "Save changes"; dialog.CloseButtonText = "Cancel";
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
                        syncNotice.Severity = InfoBarSeverity.Informational; syncNotice.Message = "There are no unsynced changes to discard."; syncNotice.IsOpen = true; return;
                    }
                    recoveryReview = new SyncRecoveryReview(rows, () =>
                    {
                        var count = recoveryReview?.Selected.Count ?? 0;
                        dialog.IsPrimaryButtonEnabled = !recoveryBusy && !recoveryLoading && recoveryReview?.CanDiscard == true && count is > 0 and <= 1000;
                        dialog.PrimaryButtonText = count == 0 ? "Discard selected" : $"Discard selected ({count})";
                    }, _detailDirty);
                    recoveryReview.Refresh.Click += async (_, _) => { recoveryWork = LoadRecoveryReview(); await recoveryWork; };
                    recoveryReview.View.Width = layout.Width; recoveryReview.View.Height = layout.Height;
                    dialog.Content = recoveryReview.View; dialog.Title = "Discard unsynced changes";
                    dialog.PrimaryButtonText = "Discard selected"; dialog.CloseButtonText = "Back";
                    dialog.DefaultButton = ContentDialogButton.Close; dialog.IsPrimaryButtonEnabled = false;
                }
                catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
                catch (Exception error)
                {
                    if (recoveryReview is { } review) { review.Notice.Title = "Couldn't load changes"; review.Notice.Message = error.Message; review.Notice.Severity = InfoBarSeverity.Error; review.Notice.IsOpen = true; }
                    else { syncNotice.Severity = InfoBarSeverity.Error; syncNotice.Message = error.Message; syncNotice.IsOpen = true; }
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
                dialog.IsPrimaryButtonEnabled = false; dialog.CloseButtonText = "Cancel";
                try
                {
                    if (IsSyncRecoveryPreview) throw new InvalidOperationException("This sample preview cannot discard reminders.");
                    var outcome = await _sync.CallAsync("discard_unsynced", new { confirmed = true,
                        targets = selected.Select(row => new { id = row.Id, version = row.Version }).ToArray() }, recoveryCancellation.Token);
                    discardCommitted = true;
                    if (_selected is { } open && selected.Any(row => row.Id == open.Id)) ShowDetail(null);
                    await RefreshAllAsync();
                    UpdateUnsyncedStatus(await _sync.CallAsync("sync_status", new { }));
                    RestoreSettings(); syncNotice.Severity = InfoBarSeverity.Success; syncNotice.Title = "Selected changes discarded";
                    syncNotice.Message = $"Removed {outcome.Number("removed_local")} local-only tasks and restored {outcome.Number("restored_from_icloud")} iCloud tasks. Your other reminders and cache were kept.";
                    syncNotice.IsOpen = true;
                }
                catch (OperationCanceledException)
                {
                    review.Notice.Title = discardCommitted ? "Changes discarded" : "Discard cancelled"; review.Notice.Message = discardCommitted ? "Refresh the list to see the restored reminders." : "No changes were discarded.";
                    review.Notice.Severity = InfoBarSeverity.Informational; review.Notice.IsOpen = true;
                }
                catch (Exception error)
                {
                    review.Notice.Title = discardCommitted ? "Changes discarded; refresh failed" : "Couldn't discard changes"; review.Notice.Message = error.Message;
                    review.Notice.Severity = InfoBarSeverity.Error; review.Notice.IsOpen = true;
                    // A changed selection must be reviewed again with fresh versions.
                    if (error is SyncException { Code: "STALE_SELECTION" }) review.CanDiscard = false;
                    review.ClearSelection();
                }
                finally
                {
                    recoveryBusy = false; review.SetBusy(false); recoveryCancellation.Dispose(); recoveryCancellation = null;
                    if (returnAfterCancel) RestoreSettings();
                    else if (recoveryReview is not null) { dialog.CloseButtonText = "Back"; dialog.IsPrimaryButtonEnabled = false; }
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
            privacy.Click += (_, _) => { dialog.Content = CreatePrivacyPolicyContent(); dialog.Title = "Privacy policy"; dialog.PrimaryButtonText = ""; dialog.CloseButtonText = "Back"; dialog.DefaultButton = ContentDialogButton.Close; showingPrivacy = true; };
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
                e.Cancel = true; showingPrivacy = false; dialog.Content = layout; dialog.Title = "Settings";
                dialog.PrimaryButtonText = "Save changes"; dialog.CloseButtonText = "Cancel"; dialog.DefaultButton = ContentDialogButton.Primary;
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
            if (action == "install_update") { await InstallUpdateAsync(); return; }
            if (result != ContentDialogResult.Primary || !double.IsFinite(sync.Value)) return;
            var themeValue = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
            var values = new { theme = themeValue, sync_minutes = (int)Math.Clamp(sync.Value, 1, 60), notifications_enabled = notifications.IsOn, default_list_id = defaultList.SelectedValue as string };
            _settings = _demo ? JsonSerializer.SerializeToElement(values) : await _sync.CallAsync("set_settings", values);
            _uiPreferences.CompletionAnimation = animation.IsOn; _uiPreferences.DetailPaneFraction = paneSizing.SelectedIndex == 0 ? null : detailWidth.Value / 100;
            SaveUiPreferences(); UpdatePaneLayout(); ApplyTheme(themeValue);
        }
        catch (Exception error)
        {
            ShowInfo("Couldn't open settings", error.Message, InfoBarSeverity.Error);
            if (AuthGate.Visibility != Visibility.Collapsed) { GateError.Message = error.Message; GateError.IsOpen = true; }
        }
        finally
        {
            updateCancellation.Cancel();
            await updateCheck;
            await recoveryWork;
            if (resize is not null) Root.SizeChanged -= resize;
            _dialogOpen = false;
        }
    }
}
