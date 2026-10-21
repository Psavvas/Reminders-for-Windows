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

    private List<ReminderList> OrderedLists()
    {
        var saved = _settings.Property("list_order");
        var order = saved.ValueKind == JsonValueKind.Array && saved.GetArrayLength() > 0
            ? saved.EnumerateArray().Select(v => v.GetString() ?? "").ToList() : _uiPreferences.ListOrder;
        return _lists.Where(list => !list.IsGroup).OrderBy(list => { var index = order.IndexOf(list.Id); return index < 0 ? int.MaxValue : index; }).ToList();
    }

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
            up.Click += async (_, _) => await MoveListAsync(list.Id, -1);
            var down = new MenuFlyoutItem { Text = "Move down", IsEnabled = ordered.IndexOf(list) < ordered.Count - 1 };
            down.Click += async (_, _) => await MoveListAsync(list.Id, 1);
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

    private async Task MoveListAsync(string id, int direction)
    {
        var order = OrderedLists().Select(list => list.Id).ToList();
        var index = order.IndexOf(id); var target = index + direction;
        if (index < 0 || target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        try
        {
            if (!_demo) _settings = await _sync.CallAsync("set_settings", new { list_order = order });
            _uiPreferences.ListOrder = order; SaveUiPreferences(); RebuildListNavigation();
        }
        catch (Exception error) { ShowInfo("Couldn't save list order", error.Message, InfoBarSeverity.Error); }
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

    private static string? FormatDueDate(DateTimeOffset? date, bool allDay, TimeSpan time) => date is not DateTimeOffset value ? null
        : allDay ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : value.Date.Add(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

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
