using System.Buffers;

namespace Tiesky.Image2D.Internal;

/// <summary>Adapts an <see cref="IBufferWriter{T}"/> to the stream-oriented encoders.</summary>
internal sealed class BufferWriterStream : Stream
{
    private readonly IBufferWriter<byte> writer;

    /// <summary>Initializes a non-owning writer adapter.</summary>
    public BufferWriterStream(IBufferWriter<byte> writer) => this.writer = writer;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            Span<byte> destination = writer.GetSpan(buffer.Length);
            int length = Math.Min(destination.Length, buffer.Length);
            buffer[..length].CopyTo(destination);
            writer.Advance(length);
            buffer = buffer[length..];
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> destination = writer.GetSpan(1);
        destination[0] = value;
        writer.Advance(1);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
