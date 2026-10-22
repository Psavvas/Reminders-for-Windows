using System.Text.Json.Nodes;

namespace Reminders.Core;

public static class ReminderTags
{
    public const string Hint = "Separate tags with spaces or commas. Remove a name to remove its tag.";

    public static string[] Parse(string text) => Normalize(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(part => part.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    private static string[] Normalize(IEnumerable<string> names)
    {
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = raw.Trim().TrimStart('#');
            if (name.Length is 0 or > 128 || name.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-'))
                throw CoreException.Bad("Use letters, numbers, hyphens or underscores for each tag (up to 128 characters).");
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
        }
        return result.ToArray();
    }

    internal static string[] Names(JsonNode? tags)
    {
        if (tags is not JsonArray values || values.Any(v => v is not JsonValue s || !s.TryGetValue<string>(out _)))
            throw CoreException.Bad("Tags must be a list of names.");
        return Normalize(values.Select(v => v!.GetValue<string>()));
    }

    internal static string RecordName(string id) => id.StartsWith("Hashtag/", StringComparison.Ordinal) ? id : "Hashtag/" + id;

    // Only the incomplete shape emitted by the earlier Windows tag writer.
    // Apple-created tags (including Type=1) must retain their own metadata.
    internal static bool IsLegacy(JsonNode record) => record["fields"] is JsonObject fields
        && fields["Name"].Text("type") == "ENCRYPTED_BYTES"
        && !fields.ContainsKey("Type") && !fields.ContainsKey("Imported")
        && fields.All(pair => pair.Key is "Name" or "Reminder" or "CreationDate" or "Deleted")
        && CloudKit.Field(record, "Reminder").Text("recordName") is not null;
}
