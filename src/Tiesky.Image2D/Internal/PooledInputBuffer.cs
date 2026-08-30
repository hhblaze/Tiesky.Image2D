using System.Buffers;

namespace Tiesky.Image2D.Internal;

/// <summary>Owns a pooled contiguous copy of a non-seekable input stream.</summary>
internal sealed class PooledInputBuffer : IDisposable
{
    private byte[]? buffer;

    /// <summary>Initializes ownership of a rented array.</summary>
    private PooledInputBuffer(byte[] buffer, int length)
    {
        this.buffer = buffer;
        Length = length;
    }

    /// <summary>Gets the number of populated bytes.</summary>
    public int Length { get; }

    /// <summary>Gets populated input bytes.</summary>
    public ReadOnlySpan<byte> Span => (buffer ?? throw new ObjectDisposedException(nameof(PooledInputBuffer))).AsSpan(0, Length);

    /// <summary>Reads a stream to a bounded pooled buffer while leaving the stream open.</summary>
    public static PooledInputBuffer Read(Stream source, long maximumBytes)
    {
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "MaxInputBytes must be between 1 and Int32.MaxValue.");
        }

        int initial = source.CanSeek ? checked((int)Math.Min(Math.Max(source.Length - source.Position, 1), Math.Min(maximumBytes, 1024 * 1024))) : 64 * 1024;
        byte[] rented = ArrayPool<byte>.Shared.Rent(initial);
        int length = 0;
        try
        {
            while (true)
            {
                if (length == rented.Length)
                {
                    if (length >= maximumBytes)
                    {
                        throw new Image2DException(ImageErrorCode.InputTooLarge, $"The encoded input exceeds {maximumBytes} bytes.");
                    }

                    int nextLength = checked((int)Math.Min(maximumBytes, Math.Max((long)rented.Length * 2, rented.Length + 1L)));
                    byte[] next = ArrayPool<byte>.Shared.Rent(nextLength);
                    rented.AsSpan(0, length).CopyTo(next);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = next;
                }

                int read = source.Read(rented, length, rented.Length - length);
                if (read == 0)
                {
                    return new PooledInputBuffer(rented, length);
                }

                length += read;
                if (length > maximumBytes)
                {
                    throw new Image2DException(ImageErrorCode.InputTooLarge, $"The encoded input exceeds {maximumBytes} bytes.");
                }
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    /// <summary>Returns the rented input array.</summary>
    public void Dispose()
    {
        byte[]? value = Interlocked.Exchange(ref buffer, null);
        if (value is not null)
        {
            ArrayPool<byte>.Shared.Return(value);
        }
    }
}
