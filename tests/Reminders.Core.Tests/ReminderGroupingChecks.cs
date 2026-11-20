using Reminders.Windows.Services;

internal static class ReminderGroupingChecks
{
    private sealed record Row(string Title, string? DueDate, bool AllDay = false, bool Completed = false, bool Deleted = false, long Priority = 0) : IScheduledReminder;
    private static DateTimeOffset Local(int year, int month, int day, int hour = 0, int minute = 0)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    internal static void Run(Action<bool, string> check)
    {
        Row At(string title, DateTimeOffset due, bool allDay = false, long priority = 0) => new(title, due.ToString("O"), allDay, Priority: priority);
        void Equal(string expected, string actual, string label) => check(expected == actual, $"{label}: expected {expected}, got {actual}");
        var now = Local(2026, 10, 5, 14, 30);
        var groups = ReminderGrouping.Group(new[]
        {
            At("Evening", Local(2026, 10, 5, 18)), At("Afternoon", Local(2026, 10, 5, 16)),
            At("Morning", Local(2026, 10, 5, 11)), At("All day", Local(2026, 10, 5), true),
            At("Older morning", Local(2026, 10, 4, 9)), At("Older evening", Local(2026, 10, 4, 22)),
            At("Overdue afternoon", Local(2026, 10, 5, 13)), At("Older all day", Local(2026, 10, 4), true)
        }, "today", "title", now);
        Equal("AllDay,Morning,Afternoon,Evening", string.Join(',', groups.Select(group => group.Section)), "Today section order");
        Equal("Older all day", groups[0].Items[0].Title, "overdue all-day reminder leads All day");
        Equal("Older morning", groups[1].Items[0].Title, "older morning reminder stays in Morning");
        Equal("Overdue afternoon", groups[2].Items[0].Title, "overdue afternoon overrides alphabetical sort");
        Equal("Older evening", groups[3].Items[0].Title, "overdue evening remains in Evening");
        check(groups.Sum(group => group.Items.Count) == 8, "grouping preserves every reminder once");

        var boundaries = ReminderGrouping.Group(new[] { 0, 11, 12, 17, 18, 23 }.Select(hour => At(hour.ToString(), Local(2026, 10, 5, hour))), "today", "due", now);
        Equal("0,11", string.Join(',', boundaries[0].Items.Select(row => row.Title)), "midnight and 11 are Morning");
        Equal("12,17", string.Join(',', boundaries[1].Items.Select(row => row.Title)), "noon and 17 are Afternoon");
        Equal("18,23", string.Join(',', boundaries[2].Items.Select(row => row.Title)), "18 and 23 are Evening");
        var noonAllDay = ReminderGrouping.Group(new[] { At("All day", Local(2026, 10, 5, 18), true) }, "today", "due", now);
        Equal("AllDay", noonAllDay[0].Section.ToString(), "all-day flag overrides its timestamp hour");

        var clockRows = new[] { At("Low priority", Local(2026, 10, 5, 14), priority: 9), At("High priority", Local(2026, 10, 5, 15), priority: 1) };
        Equal("High priority", ReminderGrouping.Group(clockRows, "today", "priority", Local(2026, 10, 5, 13))[0].Items[0].Title, "priority applies within nonoverdue rows");
        Equal("Low priority", ReminderGrouping.Group(clockRows, "today", "priority", now)[0].Items[0].Title, "clock change promotes overdue reminder within its section");
        var completedRows = new[] { new Row("A completed", Local(2026, 10, 5, 8).ToString("O"), Completed: true), At("Z overdue", Local(2026, 10, 5, 9)) };
        Equal("Z overdue", ReminderGrouping.Group(completedRows, "today", "title", now)[0].Items[0].Title, "completed reminders are not overdue");
        var allDayRows = new[] { At("Z today", Local(2026, 10, 5), true), At("A future", Local(2026, 10, 6), true) };
        Equal("A future", ReminderGrouping.Group(allDayRows, "today", "title", now)[0].Items[0].Title, "today's all-day task is not overdue before the day ends");

        var upcoming = ReminderGrouping.Group(new[]
        {
            At("Later", Local(2028, 1, 1)), At("This month", Local(2026, 10, 19)), At("Next month", Local(2026, 11, 6)),
            At("Next year", Local(2027, 1, 1)), At("This year", Local(2026, 12, 1)), At("Tomorrow", Local(2026, 10, 6)),
            At("Next week", Local(2026, 10, 12)), At("This week", Local(2026, 10, 7)), At("Today", Local(2026, 10, 5, 16)),
            At("Overdue", Local(2026, 10, 4)), At("Missed today", Local(2026, 10, 5, 10))
        }, "upcoming", "title", now);
        Equal("Overdue,Today,Tomorrow,ThisWeek,NextWeek,ThisMonth,NextMonth,ThisYear,NextYear,Later", string.Join(',', upcoming.Select(group => group.Section)), "Upcoming chronological sections");
        Equal("Missed today", upcoming[1].Items[0].Title, "today's overdue task leads Today in Upcoming");
        check(upcoming.Sum(group => group.Items.Count) == 11, "Upcoming groups are exclusive");

        void Section(DateTimeOffset reference, DateTimeOffset due, string expected, string label)
            => Equal(expected, ReminderGrouping.Group(new[] { At("Task", due) }, "upcoming", "due", reference)[0].Section.ToString(), label);
        Section(Local(2026, 10, 10), Local(2026, 10, 11), "Tomorrow", "tomorrow takes precedence over this week");
        Section(Local(2026, 10, 10), Local(2026, 10, 12), "NextWeek", "Monday starts next week");
        Section(Local(2026, 10, 11), Local(2026, 10, 12), "Tomorrow", "Sunday's tomorrow takes precedence over next week");
        Section(Local(2026, 10, 11), Local(2026, 10, 13), "NextWeek", "Sunday uses Monday-based weeks");
        Section(Local(2026, 10, 31), Local(2026, 11, 1), "Tomorrow", "tomorrow crosses a month boundary");
        Section(Local(2026, 10, 31), Local(2026, 11, 2), "NextWeek", "next week crosses a month boundary");
        Section(Local(2026, 10, 31), Local(2026, 11, 10), "NextMonth", "next month follows next week");
        Section(Local(2026, 12, 31), Local(2027, 1, 1), "Tomorrow", "tomorrow crosses a year boundary");
        Section(Local(2026, 12, 31), Local(2027, 1, 2), "ThisWeek", "this week crosses a year boundary");
        Section(Local(2026, 12, 31), Local(2027, 1, 4), "NextWeek", "next week crosses a year boundary");
        Section(Local(2026, 12, 31), Local(2027, 1, 15), "NextMonth", "January is next month in December");
        Section(Local(2026, 12, 31), Local(2027, 2, 15), "NextYear", "later next year");
        var offsetRow = new Row("Different offset", Local(2026, 10, 5, 23).ToOffset(TimeSpan.FromHours(12)).ToString("O"));
        Equal("Today", ReminderGrouping.Group(new[] { offsetRow }, "upcoming", "due", now)[0].Section.ToString(), "group by local date rather than serialized offset date");
        Equal("Evening", ReminderGrouping.Group(new[] { offsetRow }, "today", "due", now)[0].Section.ToString(), "group by local hour rather than serialized offset hour");
        check(ReminderGrouping.Group(Array.Empty<Row>(), "today", "due", now).Count == 0, "empty results have no headers");
        Equal("NoDate", ReminderGrouping.Group(new[] { new Row("Invalid", "bad-date"), new Row("Undated", null) }, "upcoming", "due", now)[0].Section.ToString(), "invalid and absent dates do not crash grouping");
    }
}
