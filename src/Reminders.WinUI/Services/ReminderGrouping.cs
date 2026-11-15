namespace Reminders.Windows.Services;

public interface IScheduledReminder
{
    string? DueDate { get; }
    bool AllDay { get; }
    bool Completed { get; }
    bool Deleted { get; }
    string Title { get; }
    long Priority { get; }
}

public enum ReminderSectionId
{
    NoDate, AllDay, Morning, Afternoon, Evening, Overdue, Today, Tomorrow, ThisWeek, NextWeek, ThisMonth, NextMonth, ThisYear, NextYear, Later
}

public sealed record ReminderGroup<T>(ReminderSectionId Section, IReadOnlyList<T> Items);

public static class ReminderGrouping
{
    private sealed record Section(int Order, ReminderSectionId Id);

    public static IReadOnlyList<ReminderGroup<T>> Group<T>(IEnumerable<T> reminders, string scope, string sort, DateTimeOffset now)
        where T : IScheduledReminder
    {
        if (scope is not ("today" or "upcoming")) throw new ArgumentException("Only Today and Upcoming use schedule sections.", nameof(scope));
        return reminders.Select(item =>
        {
            var due = DateTimeOffset.TryParse(item.DueDate, out var parsed) ? parsed : (DateTimeOffset?)null;
            var section = due is null ? new Section(99, ReminderSectionId.NoDate)
                : scope == "today" ? TimeSection(due.Value, item.AllDay) : DateSection(due.Value, now);
            var overdue = !item.Completed && !item.Deleted && due is not null
                && (item.AllDay ? due.Value.LocalDateTime.Date < now.LocalDateTime.Date : due.Value < now);
            return new { Item = item, Due = due, Section = section, Overdue = overdue };
        }).GroupBy(row => row.Section).OrderBy(group => group.Key.Order).Select(group =>
        {
            var ordered = group.OrderByDescending(row => row.Overdue).ThenBy(row => row.Overdue ? row.Due : null);
            ordered = sort switch
            {
                "title" => ordered.ThenBy(row => row.Item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Due),
                "priority" => ordered.ThenBy(row => row.Item.Priority is 1 or 5 or 9 ? row.Item.Priority : 10).ThenBy(row => row.Due),
                _ => ordered.ThenBy(row => row.Due).ThenBy(row => row.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            };
            return new ReminderGroup<T>(group.Key.Id, ordered.Select(row => row.Item).ToArray());
        }).ToArray();
    }

    private static Section TimeSection(DateTimeOffset due, bool allDay) => allDay ? new(0, ReminderSectionId.AllDay)
        : due.LocalDateTime.Hour < 12 ? new(1, ReminderSectionId.Morning)
        : due.LocalDateTime.Hour < 18 ? new(2, ReminderSectionId.Afternoon) : new(3, ReminderSectionId.Evening);

    private static Section DateSection(DateTimeOffset due, DateTimeOffset now)
    {
        var date = due.LocalDateTime.Date; var today = now.LocalDateTime.Date;
        if (date < today) return new(0, ReminderSectionId.Overdue);
        if (date == today) return new(1, ReminderSectionId.Today);
        if (date == today.AddDays(1)) return new(2, ReminderSectionId.Tomorrow);
        var week = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday through Sunday.
        if (date < week.AddDays(7)) return new(3, ReminderSectionId.ThisWeek);
        if (date < week.AddDays(14)) return new(4, ReminderSectionId.NextWeek);
        var month = new DateTime(today.Year, today.Month, 1);
        if (date < month.AddMonths(1)) return new(5, ReminderSectionId.ThisMonth);
        if (date < month.AddMonths(2)) return new(6, ReminderSectionId.NextMonth);
        var year = new DateTime(today.Year, 1, 1);
        if (date < year.AddYears(1)) return new(7, ReminderSectionId.ThisYear);
        if (date < year.AddYears(2)) return new(8, ReminderSectionId.NextYear);
        return new(9, ReminderSectionId.Later);
    }
}
