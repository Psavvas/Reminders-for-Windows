namespace Reminders.Windows.Services;

public interface IHierarchicalReminder : IScheduledReminder
{
    string Id { get; }
    string? ParentId { get; }
    string ListId { get; }
    bool ContextOnly { get; }
}

public sealed record HierarchyRow<T>(T Item, int Depth, bool HasChildren, bool Hidden);

public sealed class ReminderFamily<T>(T root, IReadOnlyList<HierarchyRow<T>> rows) : IScheduledReminder where T : class, IHierarchicalReminder
{
    public T Root { get; } = root;
    public IReadOnlyList<HierarchyRow<T>> Rows { get; } = rows;
    private T Representative => Rows.FirstOrDefault(row => !row.Item.ContextOnly)?.Item ?? Root;
    public string? DueDate => Representative.DueDate;
    public bool AllDay => Representative.AllDay;
    public bool Completed => Representative.Completed;
    public bool Deleted => Representative.Deleted;
    public string Title => Root.Title;
    public long Priority => Representative.Priority;
}

public static class ReminderHierarchy
{
    public static IReadOnlyList<ReminderFamily<T>> Build<T>(IEnumerable<T> source, IReadOnlySet<string> collapsed) where T : class, IHierarchicalReminder
    {
        var items = source.DistinctBy(item => item.Id).ToArray();
        var byId = items.ToDictionary(item => item.Id);
        bool HasParent(T item) => item.ParentId is { } id && id != item.Id && byId.TryGetValue(id, out var parent)
            && parent.ListId == item.ListId && parent.Deleted == item.Deleted;
        var children = items.Where(HasParent).GroupBy(item => item.ParentId!).ToDictionary(group => group.Key, group => group.ToArray());
        var seen = new HashSet<string>(); var families = new List<ReminderFamily<T>>();
        // A cycle or a missing parent must never hide a task or recurse forever.
        foreach (var root in items.Where(item => !HasParent(item)).Concat(items))
        {
            if (seen.Contains(root.Id)) continue;
            var rows = new List<HierarchyRow<T>>(); var pending = new Stack<(T Item, int Depth, bool Hidden)>();
            pending.Push((root, 0, false));
            while (pending.TryPop(out var entry))
            {
                if (!seen.Add(entry.Item.Id)) continue;
                var descendants = children.GetValueOrDefault(entry.Item.Id) ?? [];
                rows.Add(new(entry.Item, entry.Depth, descendants.Length > 0, entry.Hidden));
                var hidden = entry.Hidden || collapsed.Contains(entry.Item.Id);
                for (var index = descendants.Length - 1; index >= 0; index--)
                    pending.Push((descendants[index], entry.Depth + 1, hidden));
            }
            families.Add(new(root, rows));
        }
        return families;
    }
}
