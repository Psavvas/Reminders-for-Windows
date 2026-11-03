using System.Text.Json.Nodes;

namespace Reminders.Core;

public sealed partial class ReminderService
{
    private JsonNode LocalChange(JsonObject patch, string operation)
    {
        var result = cache.Transaction(() =>
        {
            var id = cache.ResolveId(patch.Required("id"));
            var current = cache.Reminder(id) ?? throw CoreException.Bad("This task is no longer available.");
            var descendants = cache.Descendants(id, sameFamilyOnly: true);
            if (patch.Flag("completed") && descendants.Count > 0 && current["recurrence"] is not null)
                throw CoreException.Bad("Complete a repeating task with subtasks on an Apple device to preserve its schedule.");
            var saved = LocalChangeOne(patch, operation);
            // A family move, deletion or completion is one durable local edit.
            // Uploads remain ordered per task and retry through the normal outbox.
            foreach (var child in descendants)
            {
                var childPatch = new JsonObject { ["id"] = child.Required("id") };
                if (operation == "delete" && !child.Flag("deleted")) LocalChangeOne(childPatch, "delete");
                else if (operation == "update" && !child.Flag("deleted"))
                {
                    if (patch.Text("list_id") is { } listId && listId != current.Text("list_id")) childPatch["list_id"] = listId;
                    if (patch.Flag("completed") && !child.Flag("completed"))
                    {
                        if (child["recurrence"] is not null) throw CoreException.Bad("Complete repeating subtasks individually before completing their parent.");
                        childPatch["completed"] = true;
                    }
                    if (childPatch.Count > 1) LocalChangeOne(childPatch, "update");
                }
            }
            if (operation == "update" && patch.ContainsKey("completed") && !patch.Flag("completed"))
            {
                var seen = new HashSet<string> { id };
                for (var parentId = saved.Text("parent_id"); parentId is not null && seen.Add(parentId);)
                {
                    var parent = cache.Reminder(parentId);
                    if (parent is null || parent.Flag("deleted") || parent.Text("list_id") != saved.Text("list_id")) break;
                    if (parent.Flag("completed")) LocalChangeOne(new() { ["id"] = parentId, ["completed"] = false }, "update");
                    parentId = parent.Text("parent_id");
                }
            }
            return saved;
        });
        QueueSync(); return result;
    }
}
