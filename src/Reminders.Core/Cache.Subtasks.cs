using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class Cache
{
    internal IReadOnlyList<JsonObject> Descendants(string id, bool sameFamilyOnly = false)
    {
        var root = Reminder(id);
        var children = Query("SELECT * FROM reminders WHERE parent_id IS NOT NULL").OfType<JsonObject>()
            .GroupBy(row => row.Required("parent_id")).ToDictionary(group => group.Key, group => group.ToArray());
        var seen = new HashSet<string> { id }; var pending = new Queue<string>(); var result = new List<JsonObject>();
        pending.Enqueue(id);
        while (pending.TryDequeue(out var parent))
            if (children.TryGetValue(parent, out var items))
                foreach (var child in items)
                    if ((!sameFamilyOnly || child.Text("list_id") == root.Text("list_id") && child.Flag("deleted") == root.Flag("deleted"))
                        && seen.Add(child.Required("id")))
                    {
                        var full = Reminder(child.Required("id"))!;
                        result.Add(full); pending.Enqueue(child.Required("id"));
                    }
        return result;
    }

    private void IncludeParents(JsonArray rows, JsonNode query)
    {
        var included = rows.OfType<JsonObject>().Select(row => row.Required("id")).ToHashSet();
        var children = Query("SELECT id,parent_id FROM reminders WHERE parent_id IS NOT NULL").OfType<JsonObject>().GroupBy(row => row.Required("parent_id"))
            .ToDictionary(group => group.Key, group => group.Select(row => row.Required("id")).ToArray());
        var pending = new Queue<JsonObject>(rows.OfType<JsonObject>());
        while (pending.TryDequeue(out var match))
            foreach (var childId in children.GetValueOrDefault(match.Required("id")) ?? [])
            {
                if (!included.Add(childId)) continue;
                var child = Reminder(childId)!;
                if (child.Flag("deleted") != match.Flag("deleted") || child.Text("list_id") != match.Text("list_id")) continue;
                if (query.Text("scope") == "completed" ? !child.Flag("completed") : query.Text("scope") != "deleted" && !query.Flag("include_completed") && child.Flag("completed")) continue;
                child["context_only"] = true; rows.Add(child.DeepClone()); pending.Enqueue(child);
            }
        pending = new Queue<JsonObject>(rows.OfType<JsonObject>());
        while (pending.TryDequeue(out var child))
        {
            if (child.Text("parent_id") is not { } parentId || !included.Add(parentId)) continue;
            var parent = Reminder(parentId);
            if (parent is null || parent.Flag("deleted") != child.Flag("deleted") || parent.Text("list_id") != child.Text("list_id")) continue;
            parent["context_only"] = true; rows.Add(parent.DeepClone()); pending.Enqueue(parent);
        }
    }
}
