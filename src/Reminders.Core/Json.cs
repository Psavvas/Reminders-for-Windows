using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal static class J
{
    public static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value)!;
    public static JsonNode? Property(this JsonNode? n, string key) => n is JsonObject obj ? obj[key] : null;
    public static string? Text(this JsonNode? n, string key) => n.Property(key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static long Number(this JsonNode? n, string key, long fallback = 0)
    {
        if (n.Property(key) is not JsonValue v) return fallback;
        if (v.TryGetValue<long>(out var value)) return value;
        return v.TryGetValue<int>(out var integer) ? integer : fallback;
    }
    public static bool Flag(this JsonNode? n, string key) => n.Property(key) is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : n.Number(key) != 0);
    public static JsonArray Array(this JsonNode? n, string key) => n.Property(key) as JsonArray ?? new();
    public static JsonNode? Parse(string? value) { try { return value is null ? null : JsonNode.Parse(value); } catch (JsonException) { return null; } }
    public static string Required(this JsonNode n, string key, int max = 2048) => Optional(n, key, max) is { Length: > 0 } s ? s : throw CoreException.Bad($"missing {key}").WithPresentation("ErrorMissingField", key);
    public static string? Optional(this JsonNode n, string key, int max)
    {
        if (n[key] is null) return null;
        var s = n.Text(key) ?? throw CoreException.Bad($"{key} must be text").WithPresentation("ErrorFieldMustBeText", key);
        if (System.Text.Encoding.UTF8.GetByteCount(s) > max) throw CoreException.Bad($"{key} is longer than the supported {max} bytes").WithPresentation("ErrorFieldTooLong", key, max);
        return s;
    }
}

public sealed class CoreException(string code, string message, string detail = "") : Exception(message)
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
    internal JsonNode Body
    {
        get
        {
            var body = J.Node(new { code = Code, message = Message, detail = Detail });
            if (this.GetPresentation() is { } presentation) body["presentation"] = J.Node(presentation);
            return body;
        }
    }
    internal static CoreException Bad(string message) => new("BAD_REQUEST", message);
}
