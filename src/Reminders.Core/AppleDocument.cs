using System.IO.Compression;
using System.Text;

namespace Reminders.Core;

internal static partial class AppleDocument
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private static void Put(List<byte> output, ulong n) { while (n >= 128) { output.Add((byte)((n & 127) | 128)); n >>= 7; } output.Add((byte)n); }
    private static void Number(List<byte> output, ulong field, ulong n) { Put(output, field << 3); Put(output, n); }
    private static void Bytes(List<byte> output, ulong field, byte[] bytes) { Put(output, (field << 3) | 2); Put(output, (ulong)bytes.Length); output.AddRange(bytes); }
    private static byte[] Substring(ulong replica, ulong clock, ulong length, ulong? child)
    {
        var id = new List<byte>(); Number(id, 1, replica); Number(id, 2, clock);
        var output = new List<byte>(); Bytes(output, 1, id.ToArray()); Number(output, 2, length); Bytes(output, 3, id.ToArray()); if (child is { } c) Number(output, 5, c); return output.ToArray();
    }
    public static string Encode(string text)
    {
        var length = (ulong)text.Length; var str = new List<byte>(); Bytes(str, 2, Encoding.UTF8.GetBytes(text)); Bytes(str, 3, Substring(0, 0, 0, 1));
        if (length > 0) Bytes(str, 3, Substring(1, 0, length, 2)); Bytes(str, 3, Substring(0, uint.MaxValue, 0, null));
        var clock = new List<byte>(); Bytes(clock, 1, Convert.FromHexString(Guid.NewGuid().ToString("N")));
        foreach (var l in new[] { length, 1UL }) { var r = new List<byte>(); Number(r, 1, l); Bytes(clock, 2, r.ToArray()); }
        var timestamp = new List<byte>(); Bytes(timestamp, 1, clock.ToArray()); Bytes(str, 4, timestamp.ToArray());
        if (length > 0) { var run = new List<byte>(); Number(run, 1, length); Bytes(str, 5, run.ToArray()); }
        var version = new List<byte>(); Number(version, 1, 0); Number(version, 2, 0); Bytes(version, 3, str.ToArray());
        var document = new List<byte>(); Number(document, 1, 0); Bytes(document, 2, version.ToArray());
        using var compressed = new MemoryStream(); using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true)) z.Write(document.ToArray()); return Convert.ToBase64String(compressed.ToArray());
    }
    public static string? Decode(string? value)
    {
        if (value is null || value.Length > MaxBytes * 2) return null;
        try
        {
            var data = Expand(value); var raw = Field(Field(Field(data, 2), 3), 2) ?? Field(Field(data, 3), 2) ?? Field(data, 2);
            return raw is null ? null : new UTF8Encoding(false, true).GetString(raw);
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IOException or DecoderFallbackException) { return null; }
    }
    private static byte[] Expand(string value)
    {
        if (value.Length > MaxBytes * 2) throw new InvalidDataException();
        var bytes = Convert.FromBase64String(value + new string('=', (4 - value.Length % 4) % 4));
        using var input = new MemoryStream(bytes); using Stream reader = bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b ? new GZipStream(input, CompressionMode.Decompress) : bytes.Length > 0 && bytes[0] == 0x78 ? new ZLibStream(input, CompressionMode.Decompress) : input;
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) { if (output.Length + count > MaxBytes) throw new InvalidDataException(); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private static ulong Varint(byte[] data, ref int position)
    {
        ulong n = 0; for (var shift = 0; shift < 70; shift += 7) { if (position >= data.Length) throw new InvalidDataException(); var b = data[position++]; if (shift == 63 && b > 1) throw new InvalidDataException(); n |= (ulong)(b & 127) << shift; if ((b & 128) == 0) return n; } throw new InvalidDataException();
    }
    private static byte[]? Field(byte[]? data, ulong wanted)
    {
        if (data is null) return null; var pos = 0;
        while (pos < data.Length)
        {
            var tag = Varint(data, ref pos); if (tag >> 3 == 0) throw new InvalidDataException();
            switch (tag & 7)
            {
                case 0: Varint(data, ref pos); break;
                case 1: pos = checked(pos + 8); break;
                case 2:
                    var length = Varint(data, ref pos); if (length > (ulong)(data.Length - pos)) throw new InvalidDataException();
                    if (tag >> 3 == wanted) return data.AsSpan(pos, (int)length).ToArray(); pos += (int)length; break;
                case 5: pos = checked(pos + 4); break;
                default: throw new InvalidDataException();
            }
            if (pos > data.Length) throw new InvalidDataException();
        }
        return null;
    }
}
