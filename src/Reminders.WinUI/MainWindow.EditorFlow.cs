using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private bool _savingDetail;
    private bool _detailTransition;
    private bool _allowWindowClose;
    private bool _windowClosed;

    private void RestoreReminderSelection()
    {
        _regrouping = true;
        try { ReminderList.SelectedItem = _reminders.FirstOrDefault(row => row.Id == _selected?.Id); }
        finally { _regrouping = false; }
    }

    private async Task<bool> ConfirmLeaveDetailAsync()
    {
        if (!_detailDirty || _selected is null) return true;
        if (_dialogOpen || _detailTransition || _savingDetail) return false;
        _detailTransition = true; _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "Save task changes?",
                Content = "The changes to this task haven't been saved.", PrimaryButtonText = "Save changes",
                SecondaryButtonText = "Discard changes", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = SaveButton.IsEnabled
            };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary) return await SaveDetailAsync();
            if (choice != ContentDialogResult.Secondary) return false;
            _detailDirty = false; return true;
        }
        catch (Exception error) { ShowInfo("Couldn't leave task", error.Message, InfoBarSeverity.Error); return false; }
        finally { _detailTransition = false; _dialogOpen = false; }
    }

    private async Task<bool> SaveDetailAsync()
    {
        if (_savingDetail) return false;
        if (_selected is not { } selected || !_detailDirty) return true;
        if (!SaveButton.IsEnabled) return false;
        var draft = CaptureDetailValues();
        var title = DetailTitle.Text.Trim(); var notes = DetailNotes.Text;
        var due = FormatDueDate(DetailDate.Date, DetailAllDay.IsOn, DetailTime.Time);
        var listId = DetailList.SelectedValue as string ?? selected.ListId;
        var allDay = DetailAllDay.IsOn; var flagged = DetailFlagged.IsOn;
        var priority = (DetailPriority.SelectedItem as ComboBoxItem)?.Tag is long value ? value : selected.Priority;
        var recurrence = DetailRepeatChoice.SelectedIndex <= 0 ? null : new RepeatSchedule { Frequency = DetailRepeatChoice.SelectedIndex, Interval = (long)DetailRepeatInterval.Value };
        var values = new Dictionary<string, object?> { ["title"] = title, ["description"] = notes, ["list_id"] = listId,
            ["due_date"] = due, ["all_day"] = allDay, ["flagged"] = flagged, ["priority"] = priority };
        if (DetailRepeatChoice.IsEnabled) values["recurrence"] = recurrence;
        var saved = false;
        _savingDetail = true; SaveButton.IsEnabled = false;
        DetailScroll.IsEnabled = false; ReminderList.IsEnabled = false; Navigation.IsEnabled = false;
        try
        {
            var patch = ReminderEditPatch.Build(selected.Id, _detailBaseline!, draft, values);
            if (_demo)
            {
                if (patch.ContainsKey("title")) selected.Title = title;
                if (patch.ContainsKey("description")) selected.Description = notes;
                if (patch.ContainsKey("list_id")) MoveDemoFamily(selected, listId);
                if (patch.ContainsKey("due_date")) { selected.DueDate = due; selected.AllDay = allDay; }
                if (patch.ContainsKey("flagged")) selected.Flagged = flagged;
                if (patch.ContainsKey("priority")) selected.Priority = priority;
                if (patch.ContainsKey("recurrence")) selected.Recurrence = recurrence;
            }
            else
            {
                await _sync.CallAsync("update_reminder", patch);
            }
            saved = true;
            if (ReferenceEquals(_selected, selected) && CaptureDetailValues() == draft) _detailDirty = false;
            await RefreshAllAsync(); return true;
        }
        catch (Exception error)
        {
            ShowInfo(saved ? "Task saved; refresh failed" : "Couldn't save reminder", error.Message, InfoBarSeverity.Error);
            return saved;
        }
        finally
        {
            _savingDetail = false;
            DetailScroll.IsEnabled = true; ReminderList.IsEnabled = true; Navigation.IsEnabled = true;
            if (_detailDirty && _selected is not null) Detail_Changed(DetailTitle, EventArgs.Empty);
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowWindowClose || _syncDisposedForUpdate) return;
        if (_savingDetail) { args.Cancel = true; return; }
        if (!_detailDirty) return;
        args.Cancel = true;
        if (await ConfirmLeaveDetailAsync()) { _allowWindowClose = true; Close(); }
    }
}
