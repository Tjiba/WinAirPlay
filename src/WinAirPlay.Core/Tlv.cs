namespace WinAirPlay.Core;

public static class Tlv
{
    public static byte[] Encode(params (byte tag, byte[] value)[] fields)
    {
        using var output = new MemoryStream();
        foreach (var (tag, value) in fields)
        {
            for (var offset = 0; offset < value.Length; offset += 255)
            {
                var length = Math.Min(255, value.Length - offset);
                output.WriteByte(tag);
                output.WriteByte((byte)length);
                output.Write(value, offset, length);
            }
        }
        return output.ToArray();
    }

    public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> input)
    {
        var result = new Dictionary<byte, byte[]>();
        for (var offset = 0; offset < input.Length;)
        {
            if (input.Length - offset < 2) throw new InvalidDataException("Truncated TLV header");
            var tag = input[offset++];
            var length = input[offset++];
            if (input.Length - offset < length) throw new InvalidDataException("Truncated TLV value");
            var previous = result.GetValueOrDefault(tag, []);
            result[tag] = [.. previous, .. input.Slice(offset, length)];
            offset += length;
        }
        return result;
    }
}
