using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reminders.Windows.Services;
using Windows.UI.ViewManagement;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private DataTemplate? _windowsReminderTemplate;
    private Style? _windowsReminderRowStyle;
    private readonly Dictionary<string, long> _appleSmartCounts = [];
    private bool _restoringAppleNavigation;
    private bool _appleSidebarOpen = true;
    private bool _appleSearchOpen;
    private bool IsAppleStyle => _uiPreferences.InterfaceStyle == "apple";

    private Brush AppleBrush(string name)
    {
        var palette = (ResourceDictionary)Root.Resources.MergedDictionaries[0].ThemeDictionaries[
            new AccessibilitySettings().HighContrast ? "HighContrast" : Root.ActualTheme == ElementTheme.Dark ? "Dark" : "Default"];
        return (Brush)palette[name];
    }

    private void ApplyInterfaceStyle()
    {
        var apple = IsAppleStyle;
        Navigation.IsPaneVisible = !apple;
        Navigation.IsPaneToggleButtonVisible = !apple;
        ReminderList.ItemTemplate = apple ? (DataTemplate)Root.Resources["AppleReminderTemplate"] : _windowsReminderTemplate;
        ReminderList.ItemContainerStyle = apple ? (Style)Root.Resources["AppleRowStyle"] : _windowsReminderRowStyle;
        ReminderList.Padding = apple ? new Thickness(18, 0, 22, 0) : new Thickness(8, 0, 8, 0);
        if (apple)
        {
            ViewTitle.FontSize = 32;
            ViewTitle.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        }
        else
        {
            ViewTitle.ClearValue(TextBlock.FontSizeProperty);
            ViewTitle.ClearValue(TextBlock.FontWeightProperty);
            ViewTitle.ClearValue(TextBlock.ForegroundProperty);
        }
        ReminderHeader.Margin = apple ? new Thickness(28, 20, 24, 10) : new Thickness(24, 0, 24, 12);
        foreach (var button in new[] { ApplePaneButton, AppleSearchButton, AppleSortButton })
            button.Visibility = apple ? Visibility.Visible : Visibility.Collapsed;
        SortButton.Visibility = apple ? Visibility.Collapsed : Visibility.Visible;
        UpdateCompletedControls();
        ReminderFooter.Padding = apple ? new Thickness(28, 8, 24, 10) : new Thickness(24, 12, 24, 16);
        DetailFields.Spacing = apple ? 20 : 16;
        SaveButton.Content = apple ? "Save" : "Save changes";
        AddButton.Height = apple ? 40 : 36;
        if (apple)
        {
            AddButton.Width = 40;
            AddButton.CornerRadius = new CornerRadius(20);
            AddButton.Padding = new Thickness(0);
            DetailNotes.MinHeight = 160;
            DetailTitle.Header = "TITLE";
        }
        else
        {
            foreach (var property in new[] { FrameworkElement.WidthProperty, Control.CornerRadiusProperty, Control.PaddingProperty })
                AddButton.ClearValue(property);
            DetailNotes.ClearValue(FrameworkElement.MinHeightProperty);
            DetailTitle.ClearValue(TextBox.HeaderProperty);
        }
        DetailNotes.Header = apple ? "NOTES" : "Notes";
        DetailPriority.Header = apple ? "PRIORITY" : "Priority";
        DetailList.Header = apple ? "LIST" : "List";
        DetailDate.Header = apple ? "DUE DATE" : "Due date";
        DetailTime.Header = apple ? "TIME" : "Time";
        var headerTemplate = apple ? (DataTemplate)Root.Resources["AppleFieldHeaderTemplate"] : null;
        DetailTitle.HeaderTemplate = headerTemplate;
        DetailNotes.HeaderTemplate = headerTemplate;
        DetailPriority.HeaderTemplate = headerTemplate;
        DetailList.HeaderTemplate = headerTemplate;
        DetailDate.HeaderTemplate = headerTemplate;
        DetailTime.HeaderTemplate = headerTemplate;
        foreach (var control in new Control[] { DetailTitle, DetailNotes, DetailPriority, DetailList, DetailDate, DetailTime })
        {
            if (apple) control.CornerRadius = new CornerRadius(12);
            else
                foreach (var property in new[] { Control.CornerRadiusProperty, Control.BackgroundProperty, Control.ForegroundProperty, Control.BorderBrushProperty, Control.BorderThicknessProperty })
                    control.ClearValue(property);
        }
        if (apple) ApplyAppleColors();
        else
        {
            ReminderHost.ClearValue(Panel.BackgroundProperty);
            DetailsHost.ClearValue(Panel.BackgroundProperty);
            foreach (var button in new[] { AddButton, SaveButton })
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.ForegroundProperty);
            }
        }
        UpdateAppleDetailVisibility();
        UpdateAppleSidebarLayout();
        UpdateSearchVisibility();
        RebuildAppleNavigation();
        UpdateViewAccent();
        UpdatePaneLayout();
    }

    private void ApplyAppleColors()
    {
        ReminderHost.Background = AppleBrush("AppleCanvasBrush");
        DetailsHost.Background = AppleBrush("AppleSidebarBrush");
        foreach (var control in new Control[] { DetailTitle, DetailNotes, DetailPriority, DetailList, DetailDate, DetailTime })
        {
            control.Background = AppleBrush("AppleFieldBrush");
            control.Foreground = AppleBrush("AppleTextBrush");
            control.BorderBrush = AppleBrush("AppleFieldBrush");
            control.BorderThickness = new Thickness(0);
        }
        AddButton.Background = AppleBrush("AppleBlueBrush"); AddButton.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        SaveButton.Background = AppleBrush("AppleBlueBrush"); SaveButton.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        UpdateViewAccent();
    }

    private void UpdateViewAccent()
    {
        if (!IsAppleStyle) return;
        var color = _view.Kind == NavKind.List ? ListColor(_lists.FirstOrDefault(list => list.Id == _view.Key))
            : _view.Key switch { "today" => "#3478F6", "upcoming" => "#FF3B30", "completed" => "#34C759", _ => "#AF52DE" };
        ViewTitle.Foreground = new AccessibilitySettings().HighContrast ? AppleBrush("AppleTextBrush") : ColorBrush(color);
    }

    private string ListColor(ReminderList? list)
    {
        if (list is null) return "#AF52DE";
        _uiPreferences.Lists.TryGetValue(list.Id, out var appearance);
        return appearance?.Color ?? list.ColorHex ?? "#AF52DE";
    }

    private void UpdateAppleSidebarLayout()
    {
        var visible = IsAppleStyle && _appleSidebarOpen;
        AppleSidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AppleSidebarColumn.Width = new GridLength(visible ? Root.ActualWidth is > 0 and < 900 ? 220 : 270 : 0);
    }

    private void RebuildAppleNavigation()
    {
        if (!IsAppleStyle) return;
        _restoringAppleNavigation = true;
        try
        {
            AppleSmartLists.Items.Clear(); AppleLists.Items.Clear(); AppleTags.Items.Clear();
            foreach (var (label, scope, glyph, color) in new[]
            {
                ("Today", "today", "\uE823", "#3478F6"), ("Upcoming", "upcoming", "\uE787", "#FF3B30"),
                ("All", "all", "\uE8FD", "#8E8E93"), ("Completed", "completed", "\uE73E", "#34C759"), ("Deleted", "deleted", "\uE74D", "#8E8E93")
            })
                AppleSmartLists.Items.Add(AppleNavigationRow(new(label, glyph, NavKind.Smart, scope, _appleSmartCounts.GetValueOrDefault(scope), color)));
            foreach (var list in OrderedLists())
            {
                _uiPreferences.Lists.TryGetValue(list.Id, out var appearance);
                var row = AppleNavigationRow(new(list.Title, appearance?.Glyph ?? "\uE8FD", NavKind.List, list.Id, list.Count, ListColor(list)));
                row.ContextFlyout = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag?.ToString() == $"list:{list.Id}")?.ContextFlyout;
                AppleLists.Items.Add(row);
            }
            foreach (var tag in _tags)
            {
                var button = new Button
                {
                    Content = "#" + tag, Tag = tag, FontSize = 13, Foreground = AppleBrush("AppleBlueBrush"), Background = AppleBrush("AppleHoverBrush"),
                    BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                button.Click += async (_, _) => await SelectViewAsync(new("#" + tag, "\uE8EC", NavKind.Tag, tag));
                AppleTags.Items.Add(button);
            }
            RestoreAppleSelection();
        }
        finally { _restoringAppleNavigation = false; }
    }

    private ListViewItem AppleNavigationRow(NavEntry entry)
    {
        var grid = new Grid { Padding = new Thickness(8, 8, 8, 8), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = ColorBrush(entry.Color), Child = new FontIcon { Glyph = entry.Glyph, FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } };
        var title = new TextBlock { Text = entry.Label, FontSize = 14, Foreground = AppleBrush("AppleTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Text = entry.Count.ToString(), FontSize = 13, Foreground = AppleBrush("AppleSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); Grid.SetColumn(count, 2);
        grid.Children.Add(icon); grid.Children.Add(title); grid.Children.Add(count);
        var row = new ListViewItem { Content = grid, Tag = entry, Style = (Style)Root.Resources["AppleRowStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{entry.Label}, {entry.Count} reminders");
        return row;
    }

    private void RestoreAppleSelection()
    {
        var restoring = _restoringAppleNavigation;
        _restoringAppleNavigation = true;
        foreach (var selector in new[] { AppleSmartLists, AppleLists })
            selector.SelectedItem = selector.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag is NavEntry entry && entry.Kind == _view.Kind && entry.Key == _view.Key);
        _restoringAppleNavigation = restoring;
    }

    private async void AppleNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringAppleNavigation || !IsAppleStyle) return;
        if (((ListView)sender).SelectedItem is ListViewItem { Tag: NavEntry entry }) await SelectViewAsync(entry);
    }

    private void UpdateDemoAppleCounts()
    {
        var active = _demoRows.Where(row => !row.Deleted && !row.Completed).ToList();
        _appleSmartCounts["today"] = active.Count(row => DateTimeOffset.TryParse(row.DueDate, out var due) && due.LocalDateTime.Date <= DateTime.Today);
        _appleSmartCounts["upcoming"] = active.Count(row => row.DueDate is not null);
        _appleSmartCounts["all"] = active.Count;
        _appleSmartCounts["completed"] = _demoRows.Count(row => row.Completed && !row.Deleted);
        _appleSmartCounts["deleted"] = _demoRows.Count(row => row.Deleted);
        AppleSyncStatus.Text = "Sample data · no iCloud connection";
        foreach (var list in _lists) list.Count = active.Count(row => row.ListId == list.Id);
        RebuildAppleNavigation();
    }

    private void UpdateSearchVisibility()
    {
        SearchRow.Visibility = !IsAppleStyle || _appleSearchOpen || !string.IsNullOrWhiteSpace(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RevealSearch()
    {
        if (IsAppleStyle)
        {
            _appleSearchOpen = true;
            UpdateSearchVisibility();
        }
        SearchBox.Focus(FocusState.Programmatic);
    }
    private void AppleSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_appleSearchOpen && string.IsNullOrWhiteSpace(SearchBox.Text)) { _appleSearchOpen = false; UpdateSearchVisibility(); }
        else RevealSearch();
    }
    private void AppleSort_Click(object sender, RoutedEventArgs e) => SortButton.Flyout?.ShowAt(AppleSortButton);
    private void ApplePane_Click(object sender, RoutedEventArgs e) { _appleSidebarOpen = !_appleSidebarOpen; UpdateAppleSidebarLayout(); }
    private void UpdateCompletedControls()
    {
        var available = _view.Kind != NavKind.Smart || _view.Key != "completed";
        ShowCompleted.Visibility = !IsAppleStyle && available ? Visibility.Visible : Visibility.Collapsed;
        AppleShowCompleted.Visibility = IsAppleStyle && available ? Visibility.Visible : Visibility.Collapsed;
        AppleShowCompleted.IsChecked = ShowCompleted.IsOn;
    }

    private void UpdateAppleDetailVisibility()
    {
        AppleClearDue.Visibility = IsAppleStyle && DetailDate.Date is not null ? Visibility.Visible : Visibility.Collapsed;
        AppleDetailStatus.Visibility = IsAppleStyle ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppleShowCompleted_Click(object sender, RoutedEventArgs e) => ShowCompleted.IsOn = AppleShowCompleted.IsChecked == true;
    private void AppleClearDue_Click(object sender, RoutedEventArgs e) => DetailDate.Date = null;
    private async void AppleSettings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();
    private async void AppleSync_Click(object sender, RoutedEventArgs e)
    {
        if (_demo || _sync.IsLocalDemo) ShowInfo("Local reminders", "These reminders are stored on this PC.", InfoBarSeverity.Informational);
        else await RequestAutoSyncAsync();
    }

    private async void AppleConflicts_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmLeaveDetailAsync()) await ResolveConflictsAsync();
    }
}
