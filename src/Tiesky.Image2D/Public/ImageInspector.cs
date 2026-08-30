using System.Buffers;
using Tiesky.Image2D.Codecs;

namespace Tiesky.Image2D;

/// <summary>Reads image container information without decoding pixels.</summary>
public static class ImageInspector
{
    /// <summary>Identifies an image stored in a byte array.</summary>
    public static ImageInfo Identify(byte[] input, ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Identify((ReadOnlySpan<byte>)input, readOptions);
    }

    /// <summary>Identifies an image stored in contiguous memory.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> input, ImageReadOptions? readOptions = null)
    {
        ReadLimits limits = SnapshotLimits(readOptions);
        if (input.Length > limits.MaxInputBytes)
        {
            throw new Image2DException(ImageErrorCode.InputTooLarge, $"The encoded input exceeds {limits.MaxInputBytes} bytes.");
        }

        if (ImageInfoReader.TryIdentify(input, complete: true, limits.MaxInputPixels, out ImageInfo? info))
        {
            return info!;
        }

        throw new Image2DException(ImageErrorCode.UnexpectedEndOfData, "The encoded image ended unexpectedly.");
    }

    /// <summary>
    /// Identifies an image from its current stream position. The stream remains open;
    /// seekable streams are restored, while non-seekable streams consume a header prefix.
    /// </summary>
    public static ImageInfo Identify(Stream input, ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(input));
        }

        ReadLimits limits = SnapshotLimits(readOptions);
        long originalPosition = 0;
        bool restore = input.CanSeek;
        if (restore)
        {
            originalPosition = input.Position;
            long remaining = input.Length - originalPosition;
            if (remaining < 0)
            {
                throw new IOException("The input stream position is beyond its length.");
            }

            if (remaining > limits.MaxInputBytes)
            {
                throw new Image2DException(ImageErrorCode.InputTooLarge, $"The encoded input exceeds {limits.MaxInputBytes} bytes.");
            }
        }

        int maximumBuffered = checked((int)Math.Min(limits.MaxInputBytes, int.MaxValue));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, maximumBuffered));
        int length = 0;
        try
        {
            while (true)
            {
                if (ImageInfoReader.TryIdentify(buffer.AsSpan(0, length), complete: false, limits.MaxInputPixels, out ImageInfo? info))
                {
                    return info!;
                }

                if (length == maximumBuffered)
                {
                    throw new Image2DException(ImageErrorCode.InputTooLarge, $"The image header exceeds {limits.MaxInputBytes} bytes.");
                }

                if (length == buffer.Length)
                {
                    int newLength = Math.Min(maximumBuffered, checked(Math.Max(buffer.Length + 1, buffer.Length * 2)));
                    byte[] replacement = ArrayPool<byte>.Shared.Rent(newLength);
                    buffer.AsSpan(0, length).CopyTo(replacement);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = replacement;
                }

                int read = input.Read(buffer.AsSpan(length, Math.Min(buffer.Length - length, maximumBuffered - length)));
                if (read == 0)
                {
                    if (ImageInfoReader.TryIdentify(buffer.AsSpan(0, length), complete: true, limits.MaxInputPixels, out info))
                    {
                        return info!;
                    }

                    throw new Image2DException(ImageErrorCode.UnexpectedEndOfData, "The encoded image ended unexpectedly.");
                }

                length += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (restore)
            {
                input.Position = originalPosition;
            }
        }
    }

    private static ReadLimits SnapshotLimits(ImageReadOptions? options)
    {
        long maximumPixels = options?.MaxInputPixels ?? 100_000_000;
        long maximumBytes = options?.MaxInputBytes ?? 512L * 1024 * 1024;
        if (maximumPixels <= 0 || maximumBytes <= 0)
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "Positive input limits are required.");
        }

        return new ReadLimits(maximumPixels, maximumBytes);
    }

    private readonly record struct ReadLimits(long MaxInputPixels, long MaxInputBytes);
}
