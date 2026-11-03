using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private void ToggleSubtasks_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ReminderItem reminder }) return;
        if (!_collapsedSubtasks.Add(reminder.Id)) _collapsedSubtasks.Remove(reminder.Id);
        RebuildReminderSections(force: true);
    }

    private async void AddSubtask_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Completed: false, Deleted: false } parent) return;
        if (_detailDirty)
        {
            ShowInfo("Save your changes first", "Save the open task before adding a subtask.", InfoBarSeverity.Informational); return;
        }
        await ShowNewReminderAsync(parent);
    }

    private List<ReminderItem> DemoDescendants(string id)
    {
        var seen = new HashSet<string> { id }; var pending = new Queue<string>(); var result = new List<ReminderItem>();
        var children = _demoRows.Where(row => row.ParentId is not null).GroupBy(row => row.ParentId!).ToDictionary(group => group.Key, group => group.ToArray());
        pending.Enqueue(id);
        while (pending.TryDequeue(out var parent))
            foreach (var child in children.GetValueOrDefault(parent) ?? [])
                if (seen.Add(child.Id)) { result.Add(child); pending.Enqueue(child.Id); }
        return result;
    }

    private void MoveDemoFamily(ReminderItem reminder, string listId)
    {
        if (reminder.ListId == listId) return;
        if (reminder.ParentId is { } parent && _demoRows.FirstOrDefault(row => row.Id == parent)?.ListId != listId) reminder.ParentId = null;
        reminder.ListId = listId;
        foreach (var child in DemoDescendants(reminder.Id).Where(row => !row.Deleted)) child.ListId = listId;
    }

    private void CompleteDemoFamily(ReminderItem reminder, bool completed)
    {
        var children = DemoDescendants(reminder.Id).Where(row => !row.Deleted).ToArray();
        if (completed && (children.Length > 0 && reminder.Recurrence is not null || children.Any(row => !row.Completed && row.Recurrence is not null)))
            throw new InvalidOperationException("Complete repeating subtasks individually before completing their parent.");
        if (completed) foreach (var child in children) child.Completed = true;
        else
        {
            var seen = new HashSet<string> { reminder.Id };
            for (var parentId = reminder.ParentId; parentId is not null && seen.Add(parentId);)
            {
                var parent = _demoRows.FirstOrDefault(row => row.Id == parentId && !row.Deleted);
                if (parent is null) break;
                parent.Completed = false; parentId = parent.ParentId;
            }
        }
    }

    private List<ReminderItem> IncludeDemoFamily(List<ReminderItem> matches)
    {
        var rows = new List<ReminderItem>(matches); var included = matches.Select(row => row.Id).ToHashSet();
        foreach (var match in matches)
            foreach (var child in DemoDescendants(match.Id))
            {
                if (child.Deleted != match.Deleted || child.ListId != match.ListId || !included.Add(child.Id)) continue;
                if (_view.Key == "completed" ? !child.Completed : _view.Key != "deleted" && !ShowCompleted.IsOn && child.Completed) continue;
                child.ContextOnly = true; rows.Add(child);
            }
        var pending = new Queue<ReminderItem>(rows);
        while (pending.TryDequeue(out var child))
        {
            if (child.ParentId is not { } parentId || !included.Add(parentId)) continue;
            var parent = _demoRows.FirstOrDefault(row => row.Id == parentId && row.Deleted == child.Deleted && row.ListId == child.ListId);
            if (parent is null) continue;
            parent.ContextOnly = true; rows.Add(parent); pending.Enqueue(parent);
        }
        return rows;
    }
}
