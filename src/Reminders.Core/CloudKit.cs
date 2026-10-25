using System.Text;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed class CloudKit(AppleAuth auth)
{
    internal static JsonNode Zone => J.Node(new { zoneName = "Reminders", ownerRecordName = "_defaultOwner", zoneType = "REGULAR_CUSTOM_ZONE" });
    internal static JsonNode? Field(JsonNode r, string name) => r["fields"]?[name]?["value"];
    internal static string? Text(JsonNode r, string name)
    {
        var value = Field(r, name); if (value is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        if (r["fields"]?[name].Text("type") != "ENCRYPTED_BYTES") return s;
        try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s)); } catch (Exception e) when (e is FormatException or DecoderFallbackException) { return null; }
    }
    internal static long Int(JsonNode r, string name) => r["fields"]?[name].Number("value") ?? 0;
    internal static JsonNode String(string value) => J.Node(new { type = "STRING", value });
    internal static JsonNode Number(long value) => J.Node(new { type = "INT64", value });
    internal static JsonNode Timestamp(long? value) => J.Node(new { type = "TIMESTAMP", value });
    internal static JsonNode Reference(string name) => J.Node(new { type = "REFERENCE", value = new { recordName = name, action = "VALIDATE" } });
    internal static JsonNode Operation(string kind, string name, string? tag, JsonObject fields, string type = "Reminder", string? parent = null)
    {
        var record = new JsonObject { ["recordName"] = name, ["recordType"] = type, ["fields"] = fields };
        // A reminder's list reference and CloudKit hierarchy move together.
        parent ??= type == "Reminder" ? fields["List"]?["value"].Text("recordName") : null;
        if (parent is not null) record["parent"] = J.Node(new { recordName = parent });
        if (tag is not null) record["recordChangeTag"] = tag;
        return new JsonObject { ["operationType"] = kind, ["record"] = record };
    }
    internal static JsonNode ResolutionTokens(IEnumerable<string> names, JsonNode? record = null)
    {
        var map = J.Parse(record is null ? null : Text(record, "ResolutionTokenMap"))?["map"]?.DeepClone() as JsonObject ?? new();
        foreach (var name in names.Distinct()) map[name] = J.Node(new { counter = map[name].Number("counter") + 1, modificationTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - 978307200, replicaID = Guid.NewGuid().ToString().ToUpperInvariant() });
        return String(new JsonObject { ["map"] = map }.ToJsonString());
    }
    internal static void Reject(IEnumerable<JsonNode?> items)
    {
        var error = items.FirstOrDefault(v => v?["serverErrorCode"] is not null); if (error is null) return;
        var code = error.Text("serverErrorCode");
        if (code is "CONFLICT" or "SERVER_RECORD_CHANGED") throw new CoreException("CONFLICT", "This reminder changed on another device while you were editing it.", error.ToJsonString());
        if (code is "AUTHENTICATION_FAILED" or "AUTHENTICATION_REQUIRED") throw new CoreException("AUTH_REQUIRED", "Your iCloud session expired.");
        if (code is "UNKNOWN_ITEM" or "NOT_FOUND" or "INVALID_ARGUMENTS" or "ACCESS_DENIED" or "PERMISSION_FAILURE" or "VALIDATION_FAILED" or "ZONE_NOT_FOUND" or "QUOTA_EXCEEDED")
            throw new CoreException("OPERATION_REJECTED", "iCloud refused a reminder operation. Review unsynced changes under Sync & storage in Settings.", code + ": " + error.Text("reason"));
        // Keep transient/unknown service failures retryable, including during
        // restoration. They do not establish a rejected local edit.
        throw new CoreException("NETWORK", "iCloud rejected a reminder operation", code + ": " + error.Text("reason"));
    }
    private async Task<JsonNode> Post(string operation, JsonNode body, CancellationToken token)
    {
        static string? ServiceUrl(JsonNode? service) => service is JsonValue value && value.TryGetValue<string>(out var url) ? url : service.Text("url");
        var services = auth.State["webservices"]; var raw = ServiceUrl(services?["ckdatabasews"]) ?? ServiceUrl(services?["reminders"]) ?? throw new CoreException("ERROR", "This iCloud session did not expose the Reminders service");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host == "icloud.com" || uri.Host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase)) || uri.UserInfo.Length != 0) throw new CoreException("ERROR", "iCloud returned an unsafe service URL");
        var url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/database/1/com.apple.reminders/production/private/" + operation + "?" + auth.Query + "&remapEnums=true&getCurrentSyncToken=true";
        var response = await auth.SendAsync(auth.Request(HttpMethod.Post, url, body, false), 32 * 1024 * 1024, token);
        if (response.Status is < 200 or >= 300)
        {
            if (response.Text.Contains("termsUpdateNeeded")) throw new CoreException("TERMS_REQUIRED", "Apple requires you to accept updated iCloud terms.");
            if (response.Status is 401 or 403 or 421) throw new CoreException("AUTH_REQUIRED", "Your iCloud session expired.");
            if (response.Status == 409) throw new CoreException("CONFLICT", "This reminder changed on another device while you were editing it.", response.Text);
            if (response.Status is >= 400 and < 500 && response.Status != 429)
                throw new CoreException("ERROR", $"iCloud rejected the sync request (HTTP {response.Status}).", AppleAuth.Detail(J.Parse(response.Text)));
            throw new CoreException("NETWORK", "Could not reach iCloud", $"HTTP {response.Status}: {AppleAuth.Detail(J.Parse(response.Text))}");
        }
        var result = JsonNode.Parse(response.Text) ?? throw new CoreException("ERROR", "Invalid CloudKit response");
        // HTTP 200 can still carry a per-record CloudKit failure. Log only the
        // endpoint and bounded error enum, never the record or server reason.
        var statuses = new[] { result }.Concat(result.Array("records"))
            .Concat(result.Array("zones")).Concat(result.Array("zones").SelectMany(z => z.Array("records")));
        foreach (var status in statuses)
            if (status.Text("serverErrorCode") is { } code) auth.ReportCloudKitError(operation, code);
        Reject([result]);
        return result;
    }
    private static void Advance(HashSet<string> seen, string? next)
    {
        if (string.IsNullOrEmpty(next) || seen.Count >= 1000 || !seen.Add(next)) throw new CoreException("ERROR", "iCloud pagination stopped advancing or exceeded 1,000 pages");
    }
    public async Task<JsonArray> Query(string type, JsonArray filters, CancellationToken token)
    {
        var records = new JsonArray(); var seen = new HashSet<string>(); string? marker = null;
        while (true)
        {
            var body = new JsonObject { ["query"] = new JsonObject { ["recordType"] = type, ["filterBy"] = filters.DeepClone() }, ["zoneID"] = Zone, ["resultsLimit"] = 200 };
            if (marker is not null) body["continuationMarker"] = marker;
            var response = await Post("records/query", body, token); var items = response.Array("records"); Reject(items);
            foreach (var r in items) if (r?["recordName"] is not null) records.Add(r.DeepClone());
            marker = response.Text("continuationMarker"); if (marker is null) return records; Advance(seen, marker);
        }
    }
    public async Task<(JsonArray Records, string? Cursor)> Changes(string? cursor, string[] types, CancellationToken token)
    {
        var records = new JsonArray(); var marker = cursor; var seen = new HashSet<string>(); if (cursor is not null) seen.Add(cursor);
        while (true)
        {
            var zone = new JsonObject { ["zoneID"] = Zone, ["desiredRecordTypes"] = new JsonArray(types.Select(t => JsonValue.Create(t)).ToArray()) };
            if (types.Length == 0) zone["desiredKeys"] = new JsonArray(); if (marker is not null) zone["syncToken"] = marker;
            var response = await Post("changes/zone", new JsonObject { ["zones"] = new JsonArray(zone), ["resultsLimit"] = 200 }, token);
            var page = response.Array("zones").FirstOrDefault() ?? response; Reject([page]); var items = page["records"] as JsonArray ?? page.Array("changes"); Reject(items);
            foreach (var r in items) records.Add(r?.DeepClone());
            var next = page.Text("syncToken") ?? page.Text("continuationMarker"); if (page.Flag("moreComing")) Advance(seen, next); marker = next ?? marker;
            if (!page.Flag("moreComing")) return (records, marker);
        }
    }
    public async Task<string?> Cursor(CancellationToken token)
    {
        var response = await Post("records/query", new JsonObject { ["query"] = J.Node(new { recordType = "reminderList" }), ["zoneID"] = Zone, ["resultsLimit"] = 1 }, token); Reject([response]);
        return response.Text("syncToken") is { Length: > 0 } cursor ? cursor : (await Changes(null, [], token)).Cursor;
    }
    public async Task CheckAccess(CancellationToken token)
    {
        // Validate access without writes or downloading the full change history.
        var response = await Post("records/query", new JsonObject { ["query"] = J.Node(new { recordType = "reminderList" }), ["zoneID"] = Zone, ["resultsLimit"] = 1 }, token);
        Reject([response]); Reject(response.Array("records"));
    }
    public async Task<JsonArray> Lookup(string id, CancellationToken token)
    {
        var response = await Post("records/lookup", new JsonObject { ["records"] = new JsonArray(J.Node(new { recordName = id })), ["zoneID"] = Zone }, token); var rows = response.Array("records"); Reject(rows); return rows;
    }
    public async Task<JsonArray> LookupMany(IEnumerable<string> ids, CancellationToken token, bool allowMissing = false, bool requireAcknowledgement = false)
    {
        var requested = ids.Distinct().ToArray();
        var response = await Post("records/lookup", new JsonObject { ["records"] = new JsonArray(requested.Select(id => J.Node(new { recordName = id })).ToArray()), ["zoneID"] = Zone }, token);
        var rows = response.Array("records");
        if (requireAcknowledgement && requested.Any(id => !rows.Any(row => row.Text("recordName") == id)))
            throw new CoreException("NETWORK", "iCloud did not return every requested reminder. No local changes were discarded.");
        // Web Services reports NOT_FOUND; some responses use the native error
        // name UNKNOWN_ITEM. Only optional per-record misses are expected here.
        var found = new JsonArray(rows.Where(r => !allowMissing || r.Text("serverErrorCode") is not ("NOT_FOUND" or "UNKNOWN_ITEM")).Select(r => r?.DeepClone()).ToArray());
        Reject(found); return found;
    }
    public async Task<JsonArray> Modify(JsonNode operation, CancellationToken token)
    {
        var operations = operation as JsonArray ?? new JsonArray(operation);
        var response = await Post("records/modify", new JsonObject { ["operations"] = operations, ["zoneID"] = Zone, ["atomic"] = true }, token); var rows = response.Array("records"); Reject(rows);
        if (operations.Any(op => !rows.Any(r => r.Text("recordName") == op?["record"].Text("recordName"))))
            throw new CoreException("NETWORK", "iCloud did not acknowledge every changed record. The operation will be retried.");
        return rows;
    }
}
