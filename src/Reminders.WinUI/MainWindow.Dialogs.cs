using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Reminders.Windows.Services;
using System.Globalization;
using System.Text.Json;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private static readonly (string Name, string Glyph)[] ListIcons =
    [ ("List", "\uE8FD"), ("Inbox", "\uE715"), ("Work", "\uE821"), ("Home", "\uE80F"),
      ("School", "\uE7BE"), ("Shopping", "\uE7BF"), ("Travel", "\uE709"), ("Heart", "\uEB51"), ("Star", "\uE734") ];
    private static readonly (string Name, string Hex)[] ListColors =
    [ ("Blue", "#0078D4"), ("Red", "#D13438"), ("Orange", "#CA5010"), ("Green", "#107C10"),
      ("Purple", "#8764B8"), ("Pink", "#C239B3"), ("Gray", "#767676") ];

    private void RestoreNavigationSelection()
    {
        _restoringNavigation = true;
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == $"{_view.Kind.ToString().ToLowerInvariant()}:{_view.Key}"
                || (_view.Kind == NavKind.Smart && item.Tag?.ToString() == $"smart:{_view.Key}"));
        _restoringNavigation = false;
    }

    private async void Navigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.IsSettingsInvoked) { RestoreNavigationSelection(); await ShowSettingsAsync(); }
            else if (args.InvokedItemContainer?.Tag?.ToString() == "sync")
            {
                if (_demo) ShowInfo("Demo mode", "Sample reminders do not sync with iCloud.", InfoBarSeverity.Informational);
                else await RequestAutoSyncAsync();
            }
            else if (args.InvokedItemContainer?.Tag?.ToString() == "conflicts") await ResolveConflictsAsync();
        }
        catch (Exception error) { ShowInfo("Couldn't complete action", error.Message, InfoBarSeverity.Error); }
    }

    private List<ReminderList> OrderedLists() => _lists.Where(list => !list.IsGroup)
        .OrderBy(list => { var index = _uiPreferences.ListOrder.IndexOf(list.Id); return index < 0 ? int.MaxValue : index; }).ToList();

    private static Brush ColorBrush(string? hex)
    {
        if (hex is not null && hex.StartsWith('#') && hex.Length == 7 && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
    }

    private void RebuildListNavigation()
    {
        _restoringNavigation = true;
        while (Navigation.MenuItems.Count > 7) Navigation.MenuItems.RemoveAt(7);
        var ordered = OrderedLists();
        foreach (var list in ordered)
        {
            _uiPreferences.Lists.TryGetValue(list.Id, out var appearance);
            var item = CreateNavigationItem(list.Title, appearance?.Glyph ?? "\uE8FD", $"list:{list.Id}");
            item.Icon.Foreground = ColorBrush(appearance?.Color ?? list.ColorHex);
            var menu = new MenuFlyout();
            var customize = new MenuFlyoutItem { Text = "Change icon and color" };
            customize.Click += async (_, _) => await CustomizeListAsync(list);
            menu.Items.Add(customize);
            var up = new MenuFlyoutItem { Text = "Move up", IsEnabled = ordered.IndexOf(list) > 0 };
            up.Click += (_, _) => MoveList(list.Id, -1);
            var down = new MenuFlyoutItem { Text = "Move down", IsEnabled = ordered.IndexOf(list) < ordered.Count - 1 };
            down.Click += (_, _) => MoveList(list.Id, 1);
            menu.Items.Add(up); menu.Items.Add(down); item.ContextFlyout = menu;
            Navigation.MenuItems.Add(item);
        }
        if (_tags.Count > 0)
        {
            Navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Tags" });
            foreach (var tag in _tags) Navigation.MenuItems.Add(CreateNavigationItem("#" + tag, "\uE8EC", $"tag:{tag}"));
        }
        _restoringNavigation = false;
        RestoreNavigationSelection();
    }

    private void SaveUiPreferences()
    {
        if (_demo) return;
        _uiPreferences.NavigationPaneOpen = _panePinnedOpen;
        UiPreferences.Save(_uiPreferences, localDemo: _sync.IsLocalDemo);
    }

    private void MoveList(string id, int direction)
    {
        var order = OrderedLists().Select(list => list.Id).ToList();
        var index = order.IndexOf(id); var target = index + direction;
        if (index < 0 || target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        _uiPreferences.ListOrder = order; SaveUiPreferences(); RebuildListNavigation();
    }

    private async Task CustomizeListAsync(ReminderList list)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            _uiPreferences.Lists.TryGetValue(list.Id, out var appearance);
            var icons = new ComboBox { Header = "Icon", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var icon in ListIcons)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new FontIcon { Glyph = icon.Glyph, FontSize = 18 }); row.Children.Add(new TextBlock { Text = icon.Name });
                icons.Items.Add(new ComboBoxItem { Content = row, Tag = icon.Glyph });
            }
            icons.SelectedIndex = Math.Max(0, Array.FindIndex(ListIcons, icon => icon.Glyph == appearance?.Glyph));
            var colors = new ComboBox { Header = "Color", HorizontalAlignment = HorizontalAlignment.Stretch };
            colors.Items.Add(new ComboBoxItem { Content = "Use iCloud list color", Tag = "" });
            foreach (var color in ListColors)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new FontIcon { Glyph = "\uEA3B", Foreground = ColorBrush(color.Hex), FontSize = 18 }); row.Children.Add(new TextBlock { Text = color.Name });
                colors.Items.Add(new ComboBoxItem { Content = row, Tag = color.Hex });
            }
            colors.SelectedIndex = Array.FindIndex(ListColors, color => color.Hex == appearance?.Color) + 1;
            var panel = new StackPanel { Spacing = 16, MinWidth = 320 };
            panel.Children.Add(icons); panel.Children.Add(colors);
            panel.Children.Add(new TextBlock { Text = "Icon, color, and list order are saved on this PC.", TextWrapping = TextWrapping.Wrap });
            var dialog = MakeDialog(list.Title, panel, "Save");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _uiPreferences.Lists[list.Id] = new() { Glyph = ((ComboBoxItem)icons.SelectedItem).Tag.ToString()!, Color = ((ComboBoxItem)colors.SelectedItem).Tag.ToString() };
            if (_uiPreferences.Lists[list.Id].Color == "") _uiPreferences.Lists[list.Id].Color = null;
            SaveUiPreferences(); RebuildListNavigation();
        }
        catch (Exception error) { ShowInfo("Couldn't customize list", error.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }

    private ContentDialog MakeDialog(string title, StackPanel panel, string action) => new()
    {
        XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = title,
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 480 },
        PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
    };

    private async Task ShowNewReminderAsync()
    {
        if (_dialogOpen || AuthGate.Visibility != Visibility.Collapsed || SetupGuide.Visibility == Visibility.Visible) return;
        _dialogOpen = true;
        try
        {
            var title = new TextBox { Header = "Title", PlaceholderText = "What needs doing?" };
            var notes = new TextBox { Header = "Notes", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
            var list = new ComboBox { Header = "List", ItemsSource = OrderedLists(), DisplayMemberPath = "Title", SelectedValuePath = "Id", HorizontalAlignment = HorizontalAlignment.Stretch };
            list.SelectedValue = _view.Kind == NavKind.List ? _view.Key : _settings.Text("default_list_id", "");
            if (list.SelectedIndex < 0) list.SelectedIndex = 0;
            var date = new CalendarDatePicker { Header = "Due date (optional)", HorizontalAlignment = HorizontalAlignment.Stretch };
            if (_view.Kind == NavKind.Smart && _view.Key == "today") date.Date = DateTimeOffset.Now;
            var scheduled = new ToggleSwitch { Header = "Set due date", IsOn = date.Date is not null };
            date.IsEnabled = scheduled.IsOn;
            var allDay = new ToggleSwitch { Header = "All day", IsOn = true, IsEnabled = scheduled.IsOn };
            var time = new TimePicker { Header = "Time", Time = new TimeSpan(9, 0, 0), IsEnabled = false };
            scheduled.Toggled += (_, _) =>
            {
                date.IsEnabled = scheduled.IsOn; allDay.IsEnabled = scheduled.IsOn;
                if (scheduled.IsOn && date.Date is null) date.Date = DateTimeOffset.Now;
                time.IsEnabled = scheduled.IsOn && !allDay.IsOn && date.Date is not null;
            };
            allDay.Toggled += (_, _) => time.IsEnabled = scheduled.IsOn && !allDay.IsOn && date.Date is not null;
            date.DateChanged += (_, _) => time.IsEnabled = scheduled.IsOn && !allDay.IsOn && date.Date is not null;
            var priority = new ComboBox { Header = "Priority", ItemsSource = new[] { "None", "Low", "Medium", "High" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var flagged = new ToggleSwitch { Header = "Flagged" };
            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            foreach (var control in new UIElement[] { title, notes, list, scheduled, date, allDay, time, priority, flagged }) panel.Children.Add(control);
            var dialog = MakeDialog("New reminder", panel, "Add");
            dialog.IsPrimaryButtonEnabled = false;
            void Validate() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text) && list.SelectedValue is string;
            title.TextChanged += (_, _) => Validate(); list.SelectionChanged += (_, _) => Validate();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedValue is not string listId) return;
            var due = FormatDueDate(scheduled.IsOn ? date.Date : null, allDay.IsOn, time.Time);
            var priorityValue = priority.SelectedIndex switch { 1 => 9, 2 => 5, 3 => 1, _ => 0 };
            if (_demo) _demoRows.Add(new() { Id = Guid.NewGuid().ToString(), ListId = listId, Title = title.Text.Trim(), Description = notes.Text, DueDate = due, AllDay = allDay.IsOn, Priority = priorityValue, Flagged = flagged.IsOn });
            else await _sync.CallAsync("create_reminder", new { list_id = listId, title = title.Text.Trim(), description = notes.Text, due_date = due, all_day = allDay.IsOn, priority = priorityValue, flagged = flagged.IsOn });
            await RefreshAllAsync();
        }
        catch (Exception error) { ShowInfo("Couldn't add reminder", error.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }

    private static string? FormatDueDate(DateTimeOffset? date, bool allDay, TimeSpan time) => date is not DateTimeOffset value ? null
        : allDay ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : value.Date.Add(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private async Task ShowSettingsAsync()
    {
        if (_dialogOpen || AuthGate.Visibility != Visibility.Collapsed || SetupGuide.Visibility == Visibility.Visible) return;
        _dialogOpen = true;
        try
        {
            var themeValue = _settings.Text("theme", "system");
            var theme = new ComboBox { Header = "App theme", ItemsSource = new[] { "Match Windows", "Light", "Dark" }, SelectedIndex = themeValue switch { "light" => 1, "dark" => 2, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch };
            var paneSizing = new ComboBox { Header = "Reminder detail sizing", ItemsSource = new[] { "Automatic", "Custom" }, SelectedIndex = _uiPreferences.DetailPaneFraction is null ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            var detailWidth = new Slider { Header = "Detail pane share (%)", Minimum = 0, Maximum = 100, StepFrequency = 1, Value = Math.Clamp(_uiPreferences.DetailPaneFraction ?? 0.5, 0, 1) * 100, IsEnabled = paneSizing.SelectedIndex == 1 };
            paneSizing.SelectionChanged += (_, _) => detailWidth.IsEnabled = paneSizing.SelectedIndex == 1;
            var animation = new ToggleSwitch { Header = "Animate completed reminders", IsOn = _uiPreferences.CompletionAnimation };
            var sync = new NumberBox { Header = "Sync interval (minutes)", Minimum = 5, Maximum = 60, Value = _settings.Number("sync_minutes") is 0 ? 10 : _settings.Number("sync_minutes"), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var notifications = new ToggleSwitch { Header = "Due-date notifications", IsOn = _settings.Flag("notifications_enabled") };
            var defaultList = new ComboBox { Header = "Default list for new reminders", DisplayMemberPath = "Title", SelectedValuePath = "Id", HorizontalAlignment = HorizontalAlignment.Stretch };
            defaultList.ItemsSource = new[] { new ReminderList { Id = "", Title = "First list in sidebar" } }.Concat(OrderedLists()).ToList();
            defaultList.SelectedValue = _settings.Text("default_list_id", "");
            if (defaultList.SelectedIndex < 0) defaultList.SelectedIndex = 0;
            var panel = new StackPanel { Spacing = 14, MinWidth = 360 };
            void Heading(string text) => panel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            Heading("Appearance"); panel.Children.Add(theme); panel.Children.Add(animation); panel.Children.Add(paneSizing); panel.Children.Add(detailWidth);
            panel.Children.Add(new TextBlock { Text = "Hover between the panes to reveal the divider. Drag it all the way to either edge to close a pane, and drag from that edge to reopen it. Double-click to restore automatic sizing. Automatic sizing shows one pane at a time in smaller windows.", TextWrapping = TextWrapping.Wrap });
            Heading("Reminders"); panel.Children.Add(defaultList); panel.Children.Add(notifications);
            Heading(_sync.IsLocalDemo ? "Local storage" : "iCloud sync");
            if (!_sync.IsLocalDemo) panel.Children.Add(sync);
            panel.Children.Add(new TextBlock { Text = _sync.IsLocalDemo ? "Changes are saved on this PC and are never uploaded to iCloud." : "Changes save locally and upload automatically. Use Sync now to check for changes immediately.", TextWrapping = TextWrapping.Wrap });
            Heading("My lists");
            panel.Children.Add(new TextBlock { Text = "Right-click a list in the sidebar to change its icon and color or move it up and down. These preferences stay on this PC.", TextWrapping = TextWrapping.Wrap });
            var dialog = MakeDialog("Settings", panel, "Save");
            Heading("App updates");
            panel.Children.Add(new TextBlock { Text = $"Version {AppUpdater.CurrentVersion.ToString(3)}" });
            var checkUpdates = new Button { Content = "Check for updates", IsEnabled = !_demo && !_updateBusy };
            var requestedUpdateCheck = false;
            var requestedSetupGuide = false;
            var requestedLeaveLocal = false;
            if (_sync.IsLocalDemo)
            {
                var leaveLocal = new Button { Content = "Leave local account" };
                leaveLocal.Click += (_, _) => { requestedLeaveLocal = true; dialog.Hide(); };
                panel.Children.Add(leaveLocal);
            }
            checkUpdates.Click += (_, _) => { requestedUpdateCheck = true; dialog.Hide(); };
            panel.Children.Add(checkUpdates);
            var setupGuide = new Button { Content = "iCloud setup and troubleshooting" };
            setupGuide.Click += (_, _) => { requestedSetupGuide = true; dialog.Hide(); };
            panel.Children.Add(setupGuide);
            Heading("About Reminders for Windows");
            panel.Children.Add(new TextBlock
            {
                Text = "Reminders for Windows is an independent app and is not affiliated with, endorsed by, or sponsored by Apple Inc. or Microsoft Corporation. iCloud is a trademark of Apple Inc. Windows is a trademark of Microsoft Corporation. Sync uses unofficial iCloud interfaces that may change or stop working without notice.",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new HyperlinkButton
            {
                Content = "View source on GitHub",
                NavigateUri = new Uri("https://github.com/Psavvas/Reminders-for-Windows"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0)
            });
            var privacy = new Button { Content = "Privacy policy" };
            var settingsContent = dialog.Content;
            var privacyContent = CreatePrivacyPolicyContent();
            var showingPrivacy = false;
            privacy.Click += (_, _) =>
            {
                dialog.Content = privacyContent;
                dialog.Title = "Privacy policy";
                dialog.PrimaryButtonText = "";
                dialog.CloseButtonText = "Back";
                dialog.DefaultButton = ContentDialogButton.Close;
                showingPrivacy = true;
            };
            dialog.CloseButtonClick += (_, args) =>
            {
                if (!showingPrivacy) return;
                // Return to the existing form so unsaved settings survive.
                args.Cancel = true;
                dialog.Content = settingsContent;
                dialog.Title = "Settings";
                dialog.PrimaryButtonText = "Save";
                dialog.CloseButtonText = "Cancel";
                dialog.DefaultButton = ContentDialogButton.Primary;
                showingPrivacy = false;
            };
            panel.Children.Add(privacy);
            sync.ValueChanged += (_, _) => dialog.IsPrimaryButtonEnabled = double.IsFinite(sync.Value) && sync.Value >= 5 && sync.Value <= 60;
            var settingsResult = await dialog.ShowAsync();
            if (requestedLeaveLocal)
            {
                _statusTimer.Stop(); _notificationTimer.Stop(); _syncTimer.Stop();
                ShowDetail(null); _reminders.Clear(); _lists.Clear(); _tags.Clear();
                _view = new("Today", "", NavKind.Smart, "today");
                ViewTitle.Text = "Today"; ViewSubtitle.Text = ""; SearchBox.Text = "";
                AppleIdBox.Text = ""; PasswordBox.Password = ""; AppInfoBar.IsOpen = false;
                _uiPreferences = UiPreferences.Load();
                SetPanePinned(_uiPreferences.NavigationPaneOpen, persist: false);
                UpdatePaneLayout();
                RebuildListNavigation();
                await _sync.DisposeAsync();
                await BootAsync();
                return;
            }
            if (requestedSetupGuide) { _dialogOpen = false; ShowSetupGuide(); return; }
            if (requestedUpdateCheck) { await CheckForUpdatesAsync(true); return; }
            if (settingsResult != ContentDialogResult.Primary) return;
            if (!double.IsFinite(sync.Value)) { ShowInfo("Settings weren't saved", "Enter a sync interval from 5 to 60 minutes.", InfoBarSeverity.Warning); return; }
            themeValue = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
            var values = new { theme = themeValue, sync_minutes = (int)Math.Clamp(sync.Value, 5, 60), notifications_enabled = notifications.IsOn, default_list_id = defaultList.SelectedValue as string };
            _settings = _demo ? JsonSerializer.SerializeToElement(values) : await _sync.CallAsync("set_settings", values);
            _uiPreferences.CompletionAnimation = animation.IsOn;
            _uiPreferences.DetailPaneFraction = paneSizing.SelectedIndex == 0 ? null : detailWidth.Value / 100;
            SaveUiPreferences(); UpdatePaneLayout(); ApplyTheme(themeValue);
        }
        catch (Exception error) { ShowInfo("Couldn't save settings", error.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }

    private async Task AnimateCompletionAsync(ReminderItem reminder)
    {
        if (!_uiPreferences.CompletionAnimation || !new UISettings().AnimationsEnabled) return;
        if (ShowCompleted.IsOn || _view.Key == "completed") { await Task.Delay(600); return; }
        var container = ReminderList.ContainerFromItem(reminder) as UIElement;
        if (container is null) return;
        var visual = ElementCompositionPreview.GetElementVisual(container);
        try
        {
            // Leave time to see the check mark before gently fading the row.
            await Task.Delay(180);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 1); fade.InsertKeyFrame(1, 0.35f); fade.Duration = TimeSpan.FromMilliseconds(420);
            visual.StartAnimation("Opacity", fade);
            await Task.Delay(420);
        }
        finally { visual.StopAnimation("Opacity"); visual.Opacity = 1; }
    }

    private void LoadDemoRows()
    {
        IEnumerable<ReminderItem> rows = _demoRows.Where(row => !row.Deleted);
        if (_view.Kind == NavKind.List) rows = rows.Where(row => row.ListId == _view.Key);
        else if (_view.Kind == NavKind.Tag) rows = rows.Where(row => row.Tags.Contains(_view.Key));
        else if (_view.Key == "today") rows = rows.Where(row => !row.Completed && DateTimeOffset.TryParse(row.DueDate, out var due) && due.LocalDateTime.Date <= DateTime.Today);
        else if (_view.Key == "upcoming") rows = rows.Where(row => !row.Completed && row.DueDate is not null);
        else if (_view.Key == "completed") rows = rows.Where(row => row.Completed);
        else if (_view.Key == "deleted") rows = _demoRows.Where(row => row.Deleted);
        if (!ShowCompleted.IsOn && _view.Key != "completed") rows = rows.Where(row => !row.Completed);
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) rows = rows.Where(row => (row.Title + " " + row.Description).Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        rows = _sort switch { "title" => rows.OrderBy(row => row.Title), "priority" => rows.OrderBy(row => row.Priority == 0 ? 10 : row.Priority), _ => rows.OrderBy(row => row.DueDate is null).ThenBy(row => row.DueDate) };
        SetReminderRows(rows);
        SetBadge("smart:today", _demoRows.Count(row => !row.Completed && !row.Deleted && DateTimeOffset.TryParse(row.DueDate, out var due) && due.LocalDateTime.Date <= DateTime.Today));
    }
}
