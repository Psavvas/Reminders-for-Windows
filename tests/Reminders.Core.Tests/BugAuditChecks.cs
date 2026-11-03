using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;

internal static class BugAuditChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-bug-audit-" + Guid.NewGuid().ToString("N"));
        async Task<JsonNode> Call(ReminderService service, string method, object parameters) =>
            J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
        try
        {
            using (var seed = new Cache(Path.Combine(root, "cache.db")))
            {
                seed.ReplaceLists(new JsonArray(J.Node(new { id = "a", title = "A" }), J.Node(new { id = "b", title = "B" })));
                seed.Upsert(J.Node(new { id = "parent", list_id = "a", title = "Parent" }));
                seed.Upsert(J.Node(new { id = "child", list_id = "a", parent_id = "parent", title = "Child" }));
                seed.Upsert(J.Node(new { id = "other-list", list_id = "b", parent_id = "parent", title = "Other list" }));
                seed.Upsert(J.Node(new { id = "deleted-child", list_id = "a", parent_id = "parent", title = "Deleted child", deleted = true }));
                seed.Upsert(J.Node(new { id = "orphan", list_id = "a", parent_id = "deleted-child", title = "Independent active task" }));
            }
            await using var service = new ReminderService(root, new MemorySecrets());
            await Call(service, "update_reminder", new { id = "parent", completed = true });
            check((await Call(service, "reminder", new { id = "child" })).Flag("completed"), "valid family descendants still complete with their parent");
            check(!(await Call(service, "reminder", new { id = "other-list" })).Flag("completed"), "a broken cross-list reference cannot complete another list's task");
            check(!(await Call(service, "reminder", new { id = "orphan" })).Flag("completed"), "family operations do not cross a deleted-parent boundary");
            await Call(service, "update_reminder", new { id = "child", completed = true, title = "Renamed completed task" });
            check((await Call(service, "reminder", new { id = "child" })).Text("title") == "Renamed completed task", "a repeated completion flag does not silently discard other edits");
            await Call(service, "update_reminder", new { id = "child", list_id = "b", completed = false });
            check((await Call(service, "reminder", new { id = "parent" })).Flag("completed"), "moving and reopening a child does not reopen its former parent");
            check((await Call(service, "reminder", new { id = "child" }))["parent_id"] is null, "a moved child remains detached when it is reopened");
            await Call(service, "delete_reminder", new { id = "parent" });
            check(!(await Call(service, "reminder", new { id = "other-list" })).Flag("deleted"), "a broken cross-list reference cannot delete another list's task");
            check(!(await Call(service, "reminder", new { id = "orphan" })).Flag("deleted"), "deleting a family cannot delete a visible task beneath an already-deleted ancestor");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
