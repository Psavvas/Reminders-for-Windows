using System.Text.Json;
using System.Text.Json.Nodes;
using Reminders.Core;
using Reminders.Windows.Services;

internal static class SubtaskChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "reminders-subtasks-" + Guid.NewGuid().ToString("N"));
        async Task<JsonNode> Call(ReminderService service, string method, object parameters) =>
            J.Parse((await service.CallAsync(method, JsonSerializer.SerializeToElement(parameters))).GetRawText())!;
        async Task Bad(Func<Task> action, string label)
        {
            try { await action(); check(false, label); } catch (CoreException error) { check(error.Code == "BAD_REQUEST", label); }
        }
        try
        {
            using (var seed = new Cache(Path.Combine(root, "cache.db")))
                seed.ReplaceLists(new JsonArray(J.Node(new { id = "List/a", title = "A" }), J.Node(new { id = "List/b", title = "B" })));
            string parentId, childId, grandchildId;
            await using (var service = new ReminderService(root, new MemorySecrets()))
            {
                Task<JsonNode> Create(string title, string? parent = null, string list = "List/a") => Call(service, "create_reminder", new { title, parent_id = parent, list_id = list });
                parentId = (await Create("Parent")).Required("id");
                childId = (await Create("Child", parentId)).Required("id");
                grandchildId = (await Create("Grandchild", childId)).Required("id");
                check((await Call(service, "reminder", new { id = childId })).Text("parent_id") == parentId, "created subtask retains its parent offline");
                await Bad(() => Create("Invalid", "missing"), "missing parent cannot create an orphan");
                await Bad(() => Create("Invalid", parentId, "List/b"), "subtask cannot be created in a different list");
                await Bad(() => Call(service, "create_reminder", new { list_id = "List/a", parent_id = 42 }), "malformed parent rejected");
                await Bad(() => Call(service, "update_reminder", new { id = parentId, parent_id = grandchildId }), "reparenting cannot create a cycle");
                var searched = (await Call(service, "reminders", new { search = "Grandchild", include_parents = true })).AsArray();
                check(searched.Count == 3 && searched.Count(row => row.Flag("context_only")) == 2, "search includes ancestors as context without losing the matching child");
                check((await Call(service, "reminders", new { search = "Parent", include_parents = true })).AsArray().Count == 3, "matching parents reveal undated subtasks");
                await Call(service, "update_reminder", new { id = grandchildId, due_date = DateTimeOffset.Now.ToString("O") });
                check((await Call(service, "reminders", new { scope = "today", include_parents = true })).AsArray().Count == 3, "a due child brings its undated ancestors into Today");
                var dueParent = (await Call(service, "create_reminder", new { list_id = "List/a", title = "Due family", due_date = DateTimeOffset.Now.ToString("O") })).Required("id");
                var undatedChild = (await Create("Undated preflight", dueParent)).Required("id");
                var dueSearch = (await Call(service, "reminders", new { scope = "today", search = "preflight", include_parents = true })).AsArray();
                check(dueSearch.Any(row => row.Text("id") == undatedChild && !row.Flag("context_only"))
                    && dueSearch.Any(row => row.Text("id") == dueParent && row.Flag("context_only")), "Today search can find an undated child already visible beneath its due parent");
                await Call(service, "update_reminder", new { id = parentId, completed = true });
                check((await Call(service, "reminders", new { scope = "completed" })).AsArray().Count == 3, "completing a parent completes all descendants");
                await Bad(() => Create("Invalid", parentId), "completed parent cannot gain new subtasks");
                await Call(service, "update_reminder", new { id = grandchildId, completed = false });
                check((await Call(service, "reminders", new { scope = "completed" })).AsArray().Count == 0, "reopening a child reopens its ancestors");
                await Call(service, "update_reminder", new { id = parentId, list_id = "List/b" });
                check((await Call(service, "reminders", new { list_id = "List/b" })).AsArray().Count == 3
                    && (await Call(service, "reminder", new { id = grandchildId })).Text("parent_id") == childId, "moving a family keeps descendants and parent links together");
                await Call(service, "update_reminder", new { id = childId, list_id = "List/a" });
                check((await Call(service, "reminder", new { id = childId }))["parent_id"] is null
                    && (await Call(service, "reminder", new { id = grandchildId })).Text("parent_id") == childId, "moving a child to another list detaches only that subtree");
                await Call(service, "delete_reminder", new { id = childId });
                check((await Call(service, "reminders", new { scope = "deleted" })).AsArray().Count == 2, "deleting a parent deletes all its descendants");
                await Bad(() => Create("Invalid", childId), "deleted parent cannot gain new subtasks");
                await Call(service, "restore_reminder", new { id = childId });
                check((await Call(service, "reminder", new { id = grandchildId })).Flag("deleted"), "restoring a parent never silently restores deleted children");
                await Call(service, "update_reminder", new { id = childId, list_id = "List/b" });
                await Call(service, "restore_reminder", new { id = grandchildId });
                check((await Call(service, "reminder", new { id = grandchildId }))["parent_id"] is null, "restoring a child after its parent moved detaches the invalid cross-list link");
                var repeating = await Call(service, "create_reminder", new { list_id = "List/a", title = "Repeat parent", due_date = DateTimeOffset.Now.ToString("O"), recurrence = new { frequency = 1 } });
                var repeatingId = repeating.Required("id"); var repeatChild = await Create("Repeat child", repeatingId);
                await Bad(() => Call(service, "update_reminder", new { id = repeatingId, completed = true, title = "Must roll back" }), "unsupported recurring family completion is rejected atomically");
                check((await Call(service, "reminder", new { id = repeatingId })).Text("title") == "Repeat parent"
                    && !(await Call(service, "reminder", new { id = repeatChild.Required("id") })).Flag("completed"), "rejected family edit cannot partially alter parent or children");
                var normalId = (await Create("Ordinary parent")).Required("id");
                await Call(service, "create_reminder", new { list_id = "List/a", parent_id = normalId, title = "Repeating child", due_date = DateTimeOffset.Now.ToString("O"), recurrence = new { frequency = 1 } });
                await Bad(() => Call(service, "update_reminder", new { id = normalId, completed = true, title = "Must also roll back" }), "a late recurring-child failure rolls back the entire family edit");
                check(!(await Call(service, "reminder", new { id = normalId })).Flag("completed")
                    && (await Call(service, "reminder", new { id = normalId })).Text("title") == "Ordinary parent", "rollback restores a parent already changed before child validation failed");
                var draftTargets = (await Call(service, "unsynced_reminders", new { })).AsArray();
                var parentTarget = draftTargets.First(row => row.Text("id") == repeatingId)!;
                await Bad(() => Call(service, "discard_unsynced", new { confirmed = true, targets = new[] { new { id = repeatingId, version = parentTarget.Text("version") } } }), "discard cannot leave unsynced children without their new parent");
            }
            await using (var reopened = new ReminderService(root, new MemorySecrets()))
                check((await Call(reopened, "reminder", new { id = grandchildId }))["parent_id"] is null
                    && (await Call(reopened, "reminder", new { id = childId })).Text("list_id") == "List/b", "subtask edits and detachments survive a service restart");
            using (var old = new Cache(Path.Combine(root, "migration.db")))
            {
                old.ApplyRecords(new JsonArray(J.Node(new { recordName = "Reminder/child", recordType = "Reminder", fields = new
                { List = new { value = new { recordName = "List/a" } }, ParentReminder = new { value = new { recordName = "Reminder/parent" } } } })));
                old.Execute("DROP INDEX idx_reminders_parent");
                old.Execute("ALTER TABLE reminders DROP COLUMN parent_id");
            }
            using (var migrated = new Cache(Path.Combine(root, "migration.db")))
                check(migrated.Reminder("Reminder/child").Text("parent_id") == "Reminder/parent", "existing caches recover parent links without resetting data");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        CheckHierarchy(check);
    }

    private static void CheckHierarchy(Action<bool, string> check)
    {
        Row Item(string id, string? parent = null, string list = "a") => new(id, parent, list);
        var input = new[] { Item("child", "parent"), Item("grandchild", "child"), Item("parent"), Item("other"), Item("orphan", "missing") };
        var families = ReminderHierarchy.Build(input, new HashSet<string>());
        check(families[0].Rows.Select(row => row.Item.Id).SequenceEqual(new[] { "parent", "child", "grandchild" }), "children remain under parents even when sorting placed them first");
        check(families[0].Rows.Select(row => row.Depth).SequenceEqual(new[] { 0, 1, 2 }), "nested depth is preserved");
        var collapsed = ReminderHierarchy.Build(input, new HashSet<string> { "parent" });
        check(collapsed.SelectMany(family => family.Rows).Count(row => !row.Hidden) == 3, "collapsing hides only descendants and keeps unrelated tasks");
        check(families.SelectMany(family => family.Rows).Any(row => row.Item.Id == "orphan" && row.Depth == 0), "missing parents never hide their tasks");
        var cycle = ReminderHierarchy.Build(new[] { Item("a", "b"), Item("b", "a"), Item("self", "self") }, new HashSet<string>());
        check(cycle.SelectMany(family => family.Rows).Count() == 3, "malformed cycles render each task once without looping");
        check(ReminderHierarchy.Build(new[] { Item("a"), Item("b", "a", "other") }, new HashSet<string>()).Count == 2, "cross-list tasks never become hidden under an unrelated list");
        var deep = Enumerable.Range(0, 10000).Select(index => Item(index.ToString(), index == 0 ? null : (index - 1).ToString())).ToArray();
        check(ReminderHierarchy.Build(deep, new HashSet<string>())[0].Rows.Count == deep.Length, "large and deeply nested families avoid stack overflows");
    }

    private sealed record Row(string Id, string? ParentId, string ListId) : IHierarchicalReminder
    {
        public bool ContextOnly => false;
        public string? DueDate => null;
        public bool AllDay => false;
        public bool Completed => false;
        public bool Deleted => false;
        public string Title => Id;
        public long Priority => 0;
    }
}
