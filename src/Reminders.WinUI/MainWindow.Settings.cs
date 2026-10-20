using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;
using System.Text.Json;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private static TextBlock SettingsDescription(string text) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["SettingsDescriptionStyle"]
    };

    private static Border SettingsCard(string title, params UIElement[] content)
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16 });
        foreach (var child in content) panel.Children.Add(child);
        return new Border { Style = (Style)Application.Current.Resources["SettingsCardStyle"], Child = panel };
    }

    private static Grid SettingsRow(string title, string? description, FrameworkElement control, double controlWidth = 180)
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
            var compact = row.ActualWidth < 410;
            Grid.SetColumn(control, compact ? 0 : 1); Grid.SetRow(control, compact ? 1 : 0);
            Grid.SetColumnSpan(text, compact ? 2 : 1); Grid.SetColumnSpan(control, compact ? 2 : 1);
            control.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            control.Width = controlWidth;
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
            var theme = new ComboBox { ItemsSource = new[] { "Match Windows", "Light", "Dark" }, SelectedIndex = _settings.Text("theme", "system") switch { "light" => 1, "dark" => 2, _ => 0 } };
            var paneSizing = new ComboBox { ItemsSource = new[] { "Automatic", "Custom" }, SelectedIndex = _uiPreferences.DetailPaneFraction is null ? 0 : 1 };
            var detailWidth = new Slider { Header = "Details pane width (%)", Minimum = 0, Maximum = 100, StepFrequency = 1, Value = Math.Clamp(_uiPreferences.DetailPaneFraction ?? .5, 0, 1) * 100 };
            detailWidth.Visibility = paneSizing.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            paneSizing.SelectionChanged += (_, _) => detailWidth.Visibility = paneSizing.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            var animation = new ToggleSwitch { IsOn = _uiPreferences.CompletionAnimation, OnContent = "", OffContent = "" };
            var notifications = new ToggleSwitch { IsOn = _settings.Flag("notifications_enabled"), OnContent = "", OffContent = "" };
            var sync = new NumberBox { Minimum = 1, Maximum = 60, Value = Math.Clamp(_settings.Number("sync_minutes"), 1, 60), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var defaultList = new ComboBox { DisplayMemberPath = "Title", SelectedValuePath = "Id" };
            defaultList.ItemsSource = new[] { new ReminderList { Id = "", Title = "First list in sidebar" } }.Concat(OrderedLists()).ToList();
            defaultList.SelectedValue = _settings.Text("default_list_id", ""); if (defaultList.SelectedIndex < 0) defaultList.SelectedIndex = 0;

            var general = SettingsPage("General", "Appearance and reminder defaults.");
            general.Children.Add(SettingsCard("Appearance", SettingsRow("Theme", null, theme), SettingsRow("Completion animation", "Animate reminders when completed.", animation, 60)));
            general.Children.Add(SettingsCard("Layout", SettingsRow("Details pane", "Drag the divider to resize either pane.", paneSizing), detailWidth));
            general.Children.Add(SettingsCard("Reminders", SettingsRow("Default list", null, defaultList), SettingsRow("Due-date notifications", null, notifications, 60)));

            var syncPage = SettingsPage("Sync & storage", "Keep your reminders up to date and recover from sync problems.");
            var syncNow = new Button { Content = "Sync now", IsEnabled = !local && auth.Flag("authenticated") };
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
            var clearCache = new Button { Content = "Clear app cache", IsEnabled = !local && !_updateBusy };
            var pending = syncStatus.Number("pending_pushes") + syncStatus.Number("conflicts");
            syncPage.Children.Add(SettingsCard("App cache", SettingsDescription(local
                ? "These reminders exist only on this PC, so clearing a downloaded cache is unavailable."
                : "Remove downloaded reminders and sync history, then download a fresh copy. Sign-in and app preferences are kept."),
                SettingsDescription(pending > 0 ? $"{pending} unsynced changes or conflicts. Clearing the cache will ask before discarding them." : "No unsynced changes or conflicts."), clearCache));
            var setup = new Button { Content = "iCloud setup and troubleshooting" }; syncPage.Children.Add(setup);

            var account = SettingsPage("Account", local ? "Manage your local session." : "Manage the Apple Account used for your reminders.");
            var accountName = local ? (_demo ? "Sample data" : "Local account") : auth.Text("apple_id", "iCloud account");
            if (string.IsNullOrWhiteSpace(accountName)) accountName = "iCloud account";
            var signOut = new Button { Content = _demo ? "Leave sample data" : _sync.IsLocalDemo ? "Leave local account" : "Sign out",
                IsEnabled = !_updateBusy && (local || !auth.Flag("signed_out") && (auth.Flag("authenticated") || auth.Flag("has_cache") || auth.Flag("needs_2fa") || auth.Flag("restoring"))) };
            account.Children.Add(SettingsCard(accountName, SettingsDescription(local ? "Reminders stay on this PC." : auth.Flag("authenticated") ? "Signed in" : "Sign-in needed"),
                SettingsDescription(local ? "Leave this session to return to sign-in." : "Sign out on this PC. Downloaded reminders and queued edits will be kept until you sign back in or clear the cache."), signOut));

            var about = SettingsPage("About", "App information and support.");
            var updates = new Button { Content = "Check for updates", IsEnabled = !_demo && !_updateBusy };
            var privacy = new Button { Content = "Privacy policy" };
            about.Children.Add(SettingsCard("Reminders for Windows", SettingsDescription($"Version {AppUpdater.CurrentVersion.ToString(3)}"), updates));
            about.Children.Add(SettingsCard("Links", privacy, new HyperlinkButton { Content = "View source on GitHub", NavigateUri = new Uri("https://github.com/Psavvas/Reminders-for-Windows"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left }));
            about.Children.Add(new Expander { Header = "About this app", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = SettingsDescription("Reminders for Windows is an independent app and is not affiliated with, endorsed by, or sponsored by Apple Inc. or Microsoft Corporation. iCloud is a trademark of Apple Inc. Windows is a trademark of Microsoft Corporation. Sync uses unofficial iCloud interfaces that may change or stop working without notice.") });

            var names = new[] { "General", "Sync & storage", "Account", "About" };
            var pages = new[] { general, syncPage, account, about };
            var nav = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
            var symbols = new[] { Symbol.Setting, Symbol.Sync, Symbol.Contact, Symbol.Help };
            for (var i = 0; i < names.Length; i++)
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                label.Children.Add(new SymbolIcon { Symbol = symbols[i] }); label.Children.Add(new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center });
                nav.Items.Add(new ListViewItem { Content = label, Padding = new Thickness(10, 12, 10, 12) });
            }
            AutomationProperties.SetName(nav, "Settings categories");
            var compactNav = new ComboBox { ItemsSource = names, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
            AutomationProperties.SetName(compactNav, "Settings category");
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 0, 8, 0) };
            var layout = new Grid { ColumnSpacing = 24 };
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(180) }); layout.ColumnDefinitions.Add(new());
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new());
            Grid.SetColumnSpan(compactNav, 2); Grid.SetRow(nav, 1); Grid.SetRow(scroll, 1); Grid.SetColumn(scroll, 1);
            layout.Children.Add(compactNav); layout.Children.Add(nav); layout.Children.Add(scroll);
            var selecting = false;
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
                var compact = layout.Width < 650;
                compactNav.Visibility = compact ? Visibility.Visible : Visibility.Collapsed; nav.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                layout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 180); layout.ColumnSpacing = compact ? 0 : 24;
            }
            resize = (_, _) => Resize(); Root.SizeChanged += resize; Resize(); SelectCategory(Math.Clamp(initialCategory, 0, pages.Length - 1));
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "Settings", Content = layout,
                PrimaryButtonText = "Save changes", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            dialog.Resources["ContentDialogMaxWidth"] = 1020d;
            string? action = null;
            void Request(string value) { action = value; dialog.Hide(); }
            signOut.Click += (_, _) => Request("sign_out"); clearCache.Click += (_, _) => Request("clear_cache");
            setup.Click += (_, _) => Request("setup"); updates.Click += (_, _) => Request("updates");
            var showingPrivacy = false;
            privacy.Click += (_, _) => { dialog.Content = CreatePrivacyPolicyContent(); dialog.Title = "Privacy policy"; dialog.PrimaryButtonText = ""; dialog.CloseButtonText = "Back"; dialog.DefaultButton = ContentDialogButton.Close; showingPrivacy = true; };
            dialog.CloseButtonClick += (_, e) =>
            {
                if (!showingPrivacy) return;
                e.Cancel = true; showingPrivacy = false; dialog.Content = layout; dialog.Title = "Settings";
                dialog.PrimaryButtonText = "Save changes"; dialog.CloseButtonText = "Cancel"; dialog.DefaultButton = ContentDialogButton.Primary;
            };
            sync.ValueChanged += (_, _) => dialog.IsPrimaryButtonEnabled = double.IsFinite(sync.Value) && sync.Value >= 1 && sync.Value <= 60;
            var result = await dialog.ShowAsync();
            _dialogOpen = false;
            if (action == "sign_out") { await SignOutAsync(); return; }
            if (action == "clear_cache") { await ClearCacheAsync(); return; }
            if (action == "setup") { ShowSetupGuide(); return; }
            if (action == "updates") { await CheckForUpdatesAsync(true); return; }
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
        finally { if (resize is not null) Root.SizeChanged -= resize; _dialogOpen = false; }
    }
}
