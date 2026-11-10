using System.IO.Compression;
using Reminders.Core;

internal static class AppleDocumentEditingChecks
{
    // Synthetic document saved by Apple's public 2638Build16 text engine after
    // two replicas edited "First draft 🛫" into "Old_Name". Includes a tombstone
    // and vector clocks; unlike our encoder's round trips this is independent.
    internal const string NativeDocument = "eJzjYBCaz8jBIMAgNYNRiMM/JyXeLzE3VUqAiwUkKMAgBaY1GMEiTAK8AhxSIJpBg0lKiIuFg1GAQYAPLMKhwKjBLCXGxcHBIPD//////AIMUnC2kgWXFJdAoZyZ/Pvw508LFlo+fvepU0+IiYNPiImDESTXlnP4h4Ksmm4t5wEukcDKnUJMHKJCTBycWkwcHACp5SB7";

    public static void Run(Action<bool, string> check)
    {
        check(AppleDocument.Decode(NativeDocument) == "Old_Name", "read independently saved native document with edit history");
        check(AppleDocument.Edit(NativeDocument, "Old_Name") == NativeDocument, "unchanged text retains the exact native document");
        var current = NativeDocument;
        foreach (var text in new[] { "New_Name", "", "café • 日本語 🛫", "Renamed again", "Old_Name" })
        {
            var previous = Inspect(current);
            current = AppleDocument.Edit(current, text);
            var changed = Inspect(current);
            check(AppleDocument.Decode(current) == text, "edited native text decodes exactly: " + text);
            check(previous.Nodes.Where(n => n.Length > 0).All(old => changed.Nodes.Any(n => n.Identity == old.Identity && n.Length == old.Length && n.Deleted)), "replacement retains and tombstones all previous character identities");
            check(changed.Nodes.Where(n => !n.Deleted).Sum(n => (long)n.Length) == text.Length, "replacement uses UTF-16 ranges, including emoji and clears");
            check(changed.Replicas.SequenceEqual(changed.Replicas.Order(StringComparer.Ordinal)), "replica indices stay in UUID sort order");
            check(previous.Replicas.All(changed.Replicas.Contains), "replacement retains every prior replica's causal history");
            check(changed.Nodes.Where(n => n.Length > 0).All(n => changed.Clocks[n.Replica][0] >= n.Clock + n.Length && changed.Clocks[n.StyleReplica][1] > n.StyleClock), "vector clocks cover character ranges and deletion timestamps");
        }
        foreach (var text in new[] { "", "Simple", "emoji 🛫" })
        {
            var created = AppleDocument.Edit(null, text);
            check(AppleDocument.Decode(AppleDocument.Edit(created, "Updated")) == "Updated", "new Windows documents remain editable: " + text);
        }
        var raw = Expand(NativeDocument);
        check(AppleDocument.Decode(AppleDocument.Edit(Convert.ToBase64String(raw), "Raw")) == "Raw", "uncompressed native documents remain editable");
        using var gzip = new MemoryStream();
        using (var stream = new GZipStream(gzip, CompressionLevel.Optimal, true)) stream.Write(raw);
        check(AppleDocument.Decode(AppleDocument.Edit(Convert.ToBase64String(gzip.ToArray()), "Gzip")) == "Gzip", "gzip native documents remain editable");
        foreach (var malformed in new[] { "not base64!", "EgkaBxIFSGVsbG8=", Convert.ToBase64String(new byte[] { 8, 1, 18, 0 }) })
        {
            try { AppleDocument.Edit(malformed, "Replacement"); check(false, "unsupported text must not create a fresh unrelated document"); }
            catch (CoreException error) { check(error.Code == "BAD_REQUEST", "unsupported text is protected before uploading"); }
        }
    }

    // Independent wire inspection checks character identity and causal history,
    // not just decoding our own output back into the replacement string.
    private sealed record Node(string Identity, int Replica, ulong Clock, ulong Length, bool Deleted, int StyleReplica, ulong StyleClock);
    private sealed record Document(List<Node> Nodes, List<string> Replicas, List<ulong[]> Clocks);
    private static Document Inspect(string value)
    {
        var root = Fields(Expand(value)); var version = Fields(root.Single(f => f.Id == 2).Data);
        var str = Fields(version.Single(f => f.Id == 3).Data);
        var entries = Fields(str.Single(f => f.Id == 4).Data).Where(f => f.Id == 1).Select(f => Fields(f.Data)).ToArray();
        var replicas = entries.Select(c => Convert.ToHexString(c.Single(f => f.Id == 1).Data)).ToList();
        var clocks = new List<ulong[]> { new ulong[] { 0, 0 } };
        clocks.AddRange(entries.Select(c => c.Where(f => f.Id == 2).Select(f => Integer(Fields(f.Data), 1)).ToArray()));
        var nodes = str.Where(f => f.Id == 3).Select(f =>
        {
            var node = Fields(f.Data); var id = Fields(node.Single(v => v.Id == 1).Data); var timestamp = Fields(node.Single(v => v.Id == 3).Data);
            var replica = (int)Integer(id, 1); var clock = Integer(id, 2);
            return new Node((replica == 0 ? "sentinel" : replicas[replica - 1]) + ":" + clock, replica, clock, Integer(node, 2), Integer(node, 4) != 0, (int)Integer(timestamp, 1), Integer(timestamp, 2));
        }).ToList();
        return new(nodes, replicas, clocks);
    }
    private sealed record Field(int Id, byte[] Data);
    private static List<Field> Fields(byte[] data)
    {
        var result = new List<Field>(); var position = 0;
        while (position < data.Length)
        {
            var tag = Read(data, ref position); var start = position;
            switch (tag & 7)
            {
                case 0: Read(data, ref position); break;
                case 2: var length = (int)Read(data, ref position); start = position; position += length; break;
                default: throw new InvalidDataException();
            }
            result.Add(new((int)(tag >> 3), data[start..position]));
        }
        return result;
    }
    private static ulong Integer(List<Field> fields, int id)
    {
        var data = fields.SingleOrDefault(f => f.Id == id)?.Data; var position = 0;
        return data is null ? 0 : Read(data, ref position);
    }
    private static ulong Read(byte[] bytes, ref int position)
    {
        ulong value = 0; var shift = 0; byte b;
        do { b = bytes[position++]; value |= (ulong)(b & 127) << shift; shift += 7; } while ((b & 128) != 0);
        return value;
    }
    private static byte[] Expand(string value)
    {
        using var input = new MemoryStream(Convert.FromBase64String(value)); using var stream = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
