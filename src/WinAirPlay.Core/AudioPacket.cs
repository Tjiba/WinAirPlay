using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinAirPlay.Core;

public static class AudioPacket
{
    public const int Frames = 352;
    public const int PcmBytes = Frames * 4;
    public const int PacketBytes = 12 + PcmBytes + 16 + 8;

    public static void Encode(Span<byte> packet, ReadOnlySpan<byte> pcm, ushort sequence,
        uint timestamp, uint streamId, ulong counter, bool first, ChaCha20Poly1305 cipher)
    {
        if (pcm.Length != PcmBytes || packet.Length != PacketBytes) throw new ArgumentException("Invalid audio packet length");
        packet[0] = 0x80;
        packet[1] = first ? (byte)0xe0 : (byte)0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], streamId);
        Span<byte> bigEndian = stackalloc byte[PcmBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            bigEndian[i] = pcm[i + 1];
            bigEndian[i + 1] = pcm[i];
        }
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
        cipher.Encrypt(nonce, bigEndian, packet.Slice(12, PcmBytes), packet.Slice(12 + PcmBytes, 16), packet.Slice(4, 8));
        nonce[4..].CopyTo(packet[^8..]);
    }

    public static byte[] Sync(uint playedTimestamp, uint headTimestamp, ulong ntp, bool first)
    {
        var packet = new byte[20];
        packet[0] = first ? (byte)0x90 : (byte)0x80;
        packet[1] = 0xd4;
        // 0x04 is the AirPlay sync variant without the legacy RAOP latency hint.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), playedTimestamp);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), headTimestamp);
        return packet;
    }
}
