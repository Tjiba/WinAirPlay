using System.Buffers.Binary;
using System.Text;

namespace WinAirPlay.Core;

public static class Plist
{
    public static byte[] Encode(object value)
    {
        var objects = new List<(object value, int[] references)>();
        int Add(object item)
        {
            var index = objects.Count;
            objects.Add((item, []));
            int[] references = item switch
            {
                Dictionary<string, object> map => map.Keys.Select(key => Add(key))
                    .Concat(map.Values.Select(Add)).ToArray(),
                object[] array => array.Select(Add).ToArray(),
                _ => []
            };
            objects[index] = (item, references);
            return index;
        }
        Add(value);
        var referenceSize = Size((ulong)objects.Count);
        using var stream = new MemoryStream();
        stream.Write("bplist00"u8);
        var offsets = new List<ulong>();
        void Length(int kind, int length)
        {
            stream.WriteByte((byte)(kind | Math.Min(length, 15)));
            if (length >= 15)
            {
                stream.WriteByte(0x12);
                Write(stream, (ulong)length, 4);
            }
        }
        foreach (var (item, references) in objects)
        {
            offsets.Add((ulong)stream.Position);
            switch (item)
            {
                case bool boolean:
                    stream.WriteByte(boolean ? (byte)9 : (byte)8);
                    break;
                case string text:
                    var unicode = text.Any(ch => ch > 127);
                    var data = (unicode ? Encoding.BigEndianUnicode : Encoding.ASCII).GetBytes(text);
                    Length(unicode ? 0x60 : 0x50, unicode ? data.Length / 2 : data.Length);
                    stream.Write(data);
                    break;
                case byte[] bytes:
                    Length(0x40, bytes.Length);
                    stream.Write(bytes);
                    break;
                case Dictionary<string, object> map:
                    Length(0xd0, map.Count);
                    foreach (var reference in references) Write(stream, (ulong)reference, referenceSize);
                    break;
                case object[] array:
                    Length(0xa0, array.Length);
                    foreach (var reference in references) Write(stream, (ulong)reference, referenceSize);
                    break;
                case int or long or uint or ulong:
                    stream.WriteByte(0x13);
                    Write(stream, Convert.ToUInt64(item), 8);
                    break;
                default: throw new InvalidDataException("Unsupported plist type");
            }
        }
        var table = (ulong)stream.Position;
        var offsetSize = Size(table);
        foreach (var offset in offsets) Write(stream, offset, offsetSize);
        stream.Write(new byte[6]);
        stream.WriteByte((byte)offsetSize);
        stream.WriteByte((byte)referenceSize);
        Write(stream, (ulong)objects.Count, 8);
        Write(stream, 0, 8);
        Write(stream, table, 8);
        return stream.ToArray();
    }

    public static object Decode(byte[] input)
    {
        if (input.Length < 40 || !input.AsSpan(0, 8).SequenceEqual("bplist00"u8))
            throw new InvalidDataException("Invalid binary plist");
        var trailer = input.Length - 32;
        var offsetSize = input[trailer + 6];
        var referenceSize = input[trailer + 7];
        var count = Read(input, trailer + 8, 8);
        var root = Read(input, trailer + 16, 8);
        var table = Read(input, trailer + 24, 8);
        if (count > 65536 || offsetSize is < 1 or > 8 || referenceSize is < 1 or > 8
            || table > (ulong)trailer || count > ((ulong)trailer - table) / offsetSize)
            throw new InvalidDataException("Invalid plist offset table");
        object Parse(ulong index, int depth)
        {
            if (depth > 32 || index >= count) throw new InvalidDataException("Invalid plist reference");
            var offset = checked((int)Read(input, checked((int)(table + index * offsetSize)), offsetSize));
            if (offset < 8 || offset >= (int)table) throw new InvalidDataException("Invalid plist object");
            var marker = input[offset++];
            var kind = marker >> 4;
            var length = marker & 15;
            if (kind == 0) return marker == 9;
            if (kind == 1) return Read(input, offset, 1 << length);
            if (length == 15)
            {
                var integer = input[offset++];
                if (integer >> 4 != 1) throw new InvalidDataException("Invalid plist length");
                var width = 1 << (integer & 15);
                length = checked((int)Read(input, offset, width));
                offset += width;
            }
            if (length > 1024 * 1024) throw new InvalidDataException("Plist object too large");
            if (kind == 4) return Slice(input, offset, length);
            if (kind == 5) return Encoding.ASCII.GetString(Slice(input, offset, length));
            if (kind == 6) return Encoding.BigEndianUnicode.GetString(Slice(input, offset, checked(length * 2)));
            ulong Reference(int i) => Read(input, checked(offset + i * referenceSize), referenceSize);
            if (kind == 10) return Enumerable.Range(0, length).Select(i => Parse(Reference(i), depth + 1)).ToArray();
            if (kind == 13)
            {
                var map = new Dictionary<string, object>();
                for (var i = 0; i < length; i++)
                    map[(string)Parse(Reference(i), depth + 1)] = Parse(Reference(i + length), depth + 1);
                return map;
            }
            throw new InvalidDataException($"Unsupported plist marker {marker:X2}");
        }
        return Parse(root, 0);
    }

    private static byte[] Slice(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length) throw new InvalidDataException("Truncated plist");
        return data.AsSpan(offset, length).ToArray();
    }
    private static ulong Read(byte[] data, int offset, int width)
    {
        if (width is < 1 or > 8) throw new InvalidDataException("Invalid integer size");
        ulong result = 0;
        foreach (var b in Slice(data, offset, width)) result = (result << 8) | b;
        return result;
    }
    private static int Size(ulong value) => value <= 255 ? 1 : value <= 65535 ? 2 : value <= uint.MaxValue ? 4 : 8;
    private static void Write(Stream stream, ulong value, int width)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer[(8 - width)..]);
    }
}
