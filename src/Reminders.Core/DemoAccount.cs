using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Reminders.Core;

public static class DemoAccount
{
    // The public test credential is documented only in docs/demo-account.md on GitHub.
    private const string PasswordHash = "C564D60BAC19700EA115BC51228DB71BC83090014102547B426AC54DFDC85C59";

    public static bool IsDemoUsername(string? username) =>
        string.Equals(username?.Trim(), "demo", StringComparison.OrdinalIgnoreCase);

    public static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(password)), Convert.FromHexString(PasswordHash)))
            throw new CoreException("AUTH_REQUIRED", "The username or password is incorrect.");
    }

    internal static void Seed(Cache cache)
    {
        if (cache.Meta("demo_seeded") is not null) return;
        cache.ReplaceLists((JsonArray)J.Node(new[]
        {
            new { id = "demo-inbox", title = "Inbox", color_hex = "#0078D4" },
            new { id = "demo-work", title = "Work", color_hex = "#8764B8" },
            new { id = "demo-personal", title = "Personal", color_hex = "#107C10" }
        }));
        cache.Transaction(() =>
        {
            var now = DateTimeOffset.Now;
            void Add(string id, string list, string title, string notes, DateTimeOffset? due = null,
                int priority = 0, bool completed = false, bool flagged = false, bool deleted = false, bool allDay = false) =>
                cache.Upsert(J.Node(new { id, list_id = list, title, description = notes,
                    due_date = due is { } date ? TimeUtil.Normalize(JsonValue.Create(date.ToString("O")), allDay) : null,
                    priority, completed, flagged, deleted, all_day = allDay, created = TimeUtil.Now, modified = TimeUtil.Now,
                    completed_date = completed ? TimeUtil.Now : null }));
            Add("demo-plan", "demo-inbox", "Review the project plan", "Add notes before tomorrow's check-in.", now.AddHours(2), priority: 1, flagged: true);
            Add("demo-groceries", "demo-personal", "Pick up groceries", "Milk, bread, and apples.", now.AddDays(1));
            Add("demo-library", "demo-inbox", "Return the library book", "", now.AddDays(-2), allDay: true);
            Add("demo-mockups", "demo-work", "Send the updated mockups", "Try editing the title, notes, or priority.", priority: 5);
            Add("demo-done", "demo-work", "Organize project files", "A completed reminder.", completed: true);
            Add("demo-deleted", "demo-personal", "Old shopping list", "A deleted reminder.", deleted: true);
            cache.Execute("INSERT OR REPLACE INTO tags(id,name,reminder_id) VALUES(@p0,@p1,@p2)", "demo-errands", "errands", "demo-groceries");
            cache.SetMeta("demo_seeded", "1");
            return true;
        });
    }
}
