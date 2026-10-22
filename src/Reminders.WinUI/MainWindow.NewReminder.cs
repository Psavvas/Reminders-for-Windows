using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private Grid EditorPair(FrameworkElement first, FrameworkElement second)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetColumn(second, 1);
        grid.Children.Add(first); grid.Children.Add(second);
        grid.SizeChanged += EditorPair_SizeChanged;
        return grid;
    }

    private async Task ShowNewReminderAsync()
    {
        if (_dialogOpen || AuthGate.Visibility != Visibility.Collapsed || SetupGuide.Visibility == Visibility.Visible) return;
        _dialogOpen = true;
        SizeChangedEventHandler? resize = null;
        try
        {
            var title = new TextBox { PlaceholderText = "What needs doing?",
                Style = (Style)Application.Current.Resources["TaskTitleEditorStyle"] };
            AutomationProperties.SetName(title, "Task title");
            var notes = new TextBox { Style = (Style)Application.Current.Resources["TaskNotesEditorStyle"] };
            var list = new ComboBox { Header = "List", ItemsSource = OrderedLists(), DisplayMemberPath = "Title",
                SelectedValuePath = "Id", HorizontalAlignment = HorizontalAlignment.Stretch };
            list.SelectedValue = _view.Kind == NavKind.List ? _view.Key : _settings.Text("default_list_id", "");
            if (list.SelectedIndex < 0) list.SelectedIndex = 0;

            var date = new CalendarDatePicker { Header = "Due date", HorizontalAlignment = HorizontalAlignment.Stretch };
            if (_view.Kind == NavKind.Smart && _view.Key == "today") date.Date = DateTimeOffset.Now;
            var scheduled = new ToggleSwitch { IsOn = date.Date is not null, OnContent = "", OffContent = "", MinWidth = 0 };
            AutomationProperties.SetName(scheduled, "Set due date");
            ToolTipService.SetToolTip(scheduled, "Set a due date");
            var allDay = new ToggleSwitch { IsOn = true, OnContent = "", OffContent = "", MinWidth = 0 };
            AutomationProperties.SetName(allDay, "All day");
            var time = new TimePicker { Header = "Time", Time = new TimeSpan(9, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
            var dateTimePair = EditorPair(date, time);
            var allDayRow = new Grid();
            allDayRow.ColumnDefinitions.Add(new()); allDayRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            allDayRow.Children.Add(new TextBlock { Text = "All day", VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(allDay, 1); allDayRow.Children.Add(allDay);

            var repeat = new ComboBox { Header = "Repeat", ItemsSource = new[] { "Never", "Daily", "Weekly", "Monthly", "Yearly" },
                SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var interval = new NumberBox { Header = "Every", Minimum = 1, Maximum = 999, Value = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var repeatPair = EditorPair(repeat, interval);
            var scheduleHeader = new Grid { ColumnSpacing = 12 };
            scheduleHeader.ColumnDefinitions.Add(new()); scheduleHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            scheduleHeader.Children.Add(new TextBlock { Text = "Schedule", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(scheduled, 1); scheduleHeader.Children.Add(scheduled);
            var schedule = new StackPanel { Spacing = 10 };
            schedule.Children.Add(scheduleHeader); schedule.Children.Add(dateTimePair);
            schedule.Children.Add(allDayRow); schedule.Children.Add(repeatPair);

            var priority = new ComboBox { Header = "Priority", ItemsSource = new[] { "None", "Low", "Medium", "High" },
                SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var flagged = new ToggleSwitch { Header = "Flagged" };
            var tags = new TextBox { Header = "Tags", PlaceholderText = "#school, #aviation", TextWrapping = TextWrapping.Wrap };
            var tagHint = new TextBlock { Text = Reminders.Core.ReminderTags.Hint, TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            var options = new StackPanel { Spacing = 12 };
            options.Children.Add(tags); options.Children.Add(tagHint); options.Children.Add(EditorPair(priority, flagged));
            var more = new Expander { Header = "Tags & priority", HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = options };
            var panel = new StackPanel { Spacing = 16 };
            panel.Children.Add(title); panel.Children.Add(notes); panel.Children.Add(list);
            panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorSectionStyle"], Child = schedule });
            panel.Children.Add(more);
            var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0, 0, 8, 0) };
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme,
                Title = "New reminder", Content = scroll, PrimaryButtonText = "Add reminder", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false };
            dialog.Resources["ContentDialogMinWidth"] = 0d;
            dialog.Resources["ContentDialogMaxWidth"] = 680d;
            void Resize()
            {
                scroll.Width = Math.Clamp(Root.ActualWidth - 100, 240, 600);
                scroll.MaxHeight = Math.Clamp(Root.ActualHeight - 240, 100, 600);
            }
            resize = (_, _) => Resize(); Root.SizeChanged += resize; Resize();

            void Validate() => dialog.IsPrimaryButtonEnabled = ValidateTagText(tags.Text, tagHint) && !string.IsNullOrWhiteSpace(title.Text) && list.SelectedValue is string
                && (!scheduled.IsOn || date.Date is not null)
                && (repeat.SelectedIndex == 0 || double.IsFinite(interval.Value) && interval.Value is >= 1 and <= 999 && interval.Value == Math.Truncate(interval.Value));
            void UpdateSchedule()
            {
                dateTimePair.Visibility = scheduled.IsOn ? Visibility.Visible : Visibility.Collapsed;
                allDayRow.Visibility = scheduled.IsOn ? Visibility.Visible : Visibility.Collapsed;
                time.Visibility = scheduled.IsOn && date.Date is not null && !allDay.IsOn ? Visibility.Visible : Visibility.Collapsed;
                interval.Visibility = repeat.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
                interval.Header = repeat.SelectedIndex switch { 1 => "Every (days)", 2 => "Every (weeks)", 3 => "Every (months)", 4 => "Every (years)", _ => "Every" };
                ArrangeDetailPair(dateTimePair, time); ArrangeDetailPair(repeatPair, interval);
                Validate();
            }
            scheduled.Toggled += (_, _) =>
            {
                if (scheduled.IsOn && date.Date is null) date.Date = DateTimeOffset.Now;
                if (!scheduled.IsOn) repeat.SelectedIndex = 0;
                UpdateSchedule();
            };
            repeat.SelectionChanged += (_, _) =>
            {
                if (repeat.SelectedIndex > 0) scheduled.IsOn = true;
                UpdateSchedule();
            };
            date.DateChanged += (_, _) => UpdateSchedule(); allDay.Toggled += (_, _) => UpdateSchedule();
            tags.TextChanged += (_, _) => Validate();
            title.TextChanged += (_, _) => Validate(); list.SelectionChanged += (_, _) => Validate();
            interval.ValueChanged += (_, _) => Validate();
            dialog.Opened += (_, _) => title.Focus(FocusState.Programmatic);
            UpdateSchedule();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedValue is not string listId) return;
            var tagNames = Reminders.Core.ReminderTags.Parse(tags.Text);
            var due = FormatDueDate(scheduled.IsOn ? date.Date : null, allDay.IsOn, time.Time);
            var priorityValue = priority.SelectedIndex switch { 1 => 9, 2 => 5, 3 => 1, _ => 0 };
            var recurrence = repeat.SelectedIndex == 0 ? null : new RepeatSchedule { Frequency = repeat.SelectedIndex, Interval = (long)interval.Value };
            if (_demo)
            {
                _demoRows.Add(new() { Id = Guid.NewGuid().ToString(), ListId = listId, Title = title.Text.Trim(), Description = notes.Text,
                DueDate = due, AllDay = allDay.IsOn, Priority = priorityValue, Flagged = flagged.IsOn, Recurrence = recurrence, Tags = tagNames.ToList() });
                _tags = _demoRows.SelectMany(r => r.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
                RebuildListNavigation();
            }
            else await _sync.CallAsync("create_reminder", new { list_id = listId, title = title.Text.Trim(), description = notes.Text,
                due_date = due, all_day = allDay.IsOn, priority = priorityValue, flagged = flagged.IsOn, recurrence, tags = tagNames });
            await RefreshAllAsync();
        }
        catch (Exception error) { ShowInfo("Couldn't add reminder", error.Message, InfoBarSeverity.Error); }
        finally { if (resize is not null) Root.SizeChanged -= resize; _dialogOpen = false; }
    }
}
