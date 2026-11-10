using System.IO.Compression;
using System.Text;

namespace Reminders.Core;

internal static partial class AppleDocument
{
    // TitleDocument/NotesDocument are mergeable strings, not replaceable blobs.
    // Retain the old character identities and explicitly tombstone their live
    // ranges, or an Apple device merges a rename as Old_NameNew_Name.
    public static string Edit(string? original, string text)
    {
        if (original is null) return Encode(text);
        if (Decode(original) == text) return original;
        try
        {
            var document = WireMessage.Read(Expand(original));
            var versions = document.Messages(2);
            if (document.Integer(1) != 0 || versions.Count != 1) throw new InvalidDataException();
            var version = versions[0];
            if (version.Integer(1) != 0 || version.Integer(2) != 0) throw new InvalidDataException();
            var str = version.Message(3);
            var nodes = str.Messages(3);
            var timestamp = str.Message(4);
            var clocks = timestamp.Messages(1);
            if (nodes.Count < 2 || clocks.Any(c => c.Blob(1).Length != 16)) throw new InvalidDataException();
            var start = nodes[0].Message(1);
            if (start.Integer(1) != 0 || start.Integer(2) != 0 || nodes[0].Integer(2) != 0) throw new InvalidDataException();

            ulong textClock = 0, styleClock = 0;
            foreach (var node in nodes)
            {
                var id = node.Message(1); var stamp = node.Message(3); var length = node.Integer(2);
                if (id.Integer(1) > (ulong)clocks.Count || stamp.Integer(1) > (ulong)clocks.Count) throw new InvalidDataException();
                if (length == 0) continue; // Exclude the end sentinel's MAX clock.
                textClock = Math.Max(textClock, checked(id.Integer(2) + length));
                styleClock = Math.Max(styleClock, stamp.Integer(2));
            }
            // A deletion outranks the previous style timestamp by eight in
            // Apple's merge ordering. The vector clock must cover it as well.
            styleClock = checked(styleClock + 8);
            if (checked(textClock + (ulong)text.Length) >= uint.MaxValue || styleClock >= uint.MaxValue) throw new InvalidDataException();
            var clock = new WireMessage();
            clock.SetBlob(1, Convert.FromHexString(Guid.NewGuid().ToString("N")));
            var textCounter = new WireMessage(); textCounter.SetInteger(1, textClock + (ulong)text.Length);
            var styleCounter = new WireMessage(); styleCounter.SetInteger(1, styleClock + 1);
            clock.SetMessages(2, [textCounter, styleCounter]);
            // Replica indices refer to UUID sort order, not insertion order.
            // Remap existing identities when adding our new replica.
            var oldOrder = clocks.Append(clock).ToList();
            clocks = oldOrder.OrderBy(c => Convert.ToHexString(c.Blob(1)), StringComparer.Ordinal).ToList();
            ulong Remap(ulong id) => id == 0 ? 0 : (ulong)clocks.IndexOf(oldOrder[(int)id - 1]) + 1;
            var replica = Remap((ulong)oldOrder.Count);
            var children = nodes[0].Integers(5).ToArray();
            foreach (var node in nodes)
            {
                var links = node.Integers(5).ToArray();
                if (links.Any(c => c >= (ulong)nodes.Count)) throw new InvalidDataException();
                node.SetIntegers(5, links.Select(c => c == 0 ? 0 : c + 1));
                foreach (var field in new ulong[] { 1, 3 })
                {
                    var identity = node.Message(field);
                    identity.SetInteger(1, Remap(identity.Integer(1)));
                    node.SetMessage(field, identity);
                }
                if (node.Integer(2) > 0 && node.Integer(4) == 0)
                {
                    node.SetInteger(4, 1);
                    node.SetMessage(3, Identity(replica, styleClock));
                }
            }
            var inserted = new WireMessage();
            inserted.SetMessage(1, Identity(replica, textClock));
            inserted.SetInteger(2, (ulong)text.Length); // Apple ranges use UTF-16.
            inserted.SetMessage(3, Identity(replica, 0));
            inserted.SetIntegers(5, children.Select(c => c == 0 ? 0 : c + 1));
            nodes[0].SetIntegers(5, [1]);
            nodes.Insert(1, inserted);
            timestamp.SetMessages(1, clocks);
            str.SetBlob(2, Encoding.UTF8.GetBytes(text));
            str.SetMessages(3, nodes); str.SetMessage(4, timestamp);
            var run = new WireMessage(); run.SetInteger(1, (ulong)text.Length);
            str.SetMessages(5, text.Length == 0 ? [] : [run]);
            version.SetMessage(3, str); document.SetMessage(2, version);
            var data = document.Write();
            if (data.Length > MaxBytes) throw new InvalidDataException();
            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true)) z.Write(data);
            return Convert.ToBase64String(compressed.ToArray());
        }
        catch (Exception error) when (error is FormatException or InvalidDataException or IOException or OverflowException or InvalidOperationException)
        {
            throw CoreException.Bad("This reminder's text document could not be edited safely. Refresh it or edit the text on an Apple device.");
        }
    }

    private static WireMessage Identity(ulong replica, ulong clock)
    {
        var id = new WireMessage(); id.SetInteger(1, replica); id.SetInteger(2, clock); return id;
    }

    // Preserve fields we do not modify, including document/version metadata.
    private sealed class WireMessage
    {
        private sealed record Entry(ulong Tag, byte[] Data);
        private readonly List<Entry> entries = [];
        public static WireMessage Read(byte[] data)
        {
            var result = new WireMessage(); var position = 0;
            while (position < data.Length)
            {
                var tag = Varint(data, ref position);
                if (tag >> 3 == 0) throw new InvalidDataException();
                var start = position;
                switch (tag & 7)
                {
                    case 0: Varint(data, ref position); break;
                    case 1: position = checked(position + 8); break;
                    case 2:
                        var length = Varint(data, ref position);
                        if (length > (ulong)(data.Length - position)) throw new InvalidDataException();
                        start = position; position += (int)length; break;
                    case 5: position = checked(position + 4); break;
                    default: throw new InvalidDataException();
                }
                if (position > data.Length) throw new InvalidDataException();
                result.entries.Add(new(tag, data.AsSpan(start, position - start).ToArray()));
            }
            return result;
        }
        public byte[] Write()
        {
            var result = new List<byte>();
            foreach (var entry in entries)
            {
                Put(result, entry.Tag);
                if ((entry.Tag & 7) == 2) Put(result, (ulong)entry.Data.Length);
                result.AddRange(entry.Data);
            }
            return result.ToArray();
        }
        public byte[] Blob(ulong field) => entries.SingleOrDefault(e => e.Tag == ((field << 3) | 2))?.Data ?? throw new InvalidDataException();
        public WireMessage Message(ulong field) => Read(Blob(field));
        public List<WireMessage> Messages(ulong field) => entries.Where(e => e.Tag == ((field << 3) | 2)).Select(e => Read(e.Data)).ToList();
        public ulong Integer(ulong field) => Integers(field).SingleOrDefault();
        public IEnumerable<ulong> Integers(ulong field)
        {
            foreach (var entry in entries.Where(e => e.Tag >> 3 == field))
            {
                if ((entry.Tag & 7) is not (0 or 2)) throw new InvalidDataException();
                var position = 0;
                while (position < entry.Data.Length) yield return Varint(entry.Data, ref position);
            }
        }
        public void SetBlob(ulong field, byte[] data) { Remove(field); entries.Add(new((field << 3) | 2, data)); }
        public void SetMessage(ulong field, WireMessage message) => SetBlob(field, message.Write());
        public void SetMessages(ulong field, IEnumerable<WireMessage> messages)
        {
            Remove(field); foreach (var message in messages) entries.Add(new((field << 3) | 2, message.Write()));
        }
        public void SetInteger(ulong field, ulong value) => SetIntegers(field, [value]);
        public void SetIntegers(ulong field, IEnumerable<ulong> values)
        {
            Remove(field); foreach (var value in values) { var bytes = new List<byte>(); Put(bytes, value); entries.Add(new(field << 3, bytes.ToArray())); }
        }
        private void Remove(ulong field) => entries.RemoveAll(e => e.Tag >> 3 == field);
    }
}
