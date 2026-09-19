using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinAirPlay.Core;

public sealed class HapStream(Stream inner, byte[] writeKey, byte[] readKey) : Stream
{
    private readonly ChaCha20Poly1305 encryptor = new(writeKey);
    private readonly ChaCha20Poly1305 decryptor = new(readKey);
    private ulong writeCounter;
    private ulong readCounter;
    private byte[] pending = [];
    private int position;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        if (position == pending.Length)
        {
            var prefix = new byte[2];
            inner.ReadExactly(prefix);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
            if (length is 0 or > 1024) throw new InvalidDataException("Invalid encrypted frame length");
            var cipher = new byte[length + 16];
            inner.ReadExactly(cipher);
            pending = new byte[length];
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], readCounter++);
            decryptor.Decrypt(nonce, cipher.AsSpan(0, length), cipher.AsSpan(length), pending, prefix);
            position = 0;
        }
        var size = Math.Min(count, pending.Length - position);
        pending.AsSpan(position, size).CopyTo(buffer.AsSpan(offset, size));
        position += size;
        return size;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Span<byte> nonce = stackalloc byte[12];
        while (count > 0)
        {
            var length = Math.Min(count, 1024);
            var frame = new byte[length + 18];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)length);
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], writeCounter++);
            encryptor.Encrypt(nonce, buffer.AsSpan(offset, length), frame.AsSpan(2, length),
                frame.AsSpan(length + 2, 16), frame.AsSpan(0, 2));
            inner.Write(frame);
            offset += length;
            count -= length;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { encryptor.Dispose(); decryptor.Dispose(); inner.Dispose(); }
        base.Dispose(disposing);
    }
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
