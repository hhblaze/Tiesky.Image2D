using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.Png;

/// <summary>Encodes RGB24 or RGBA32 pixels as a static eight-bit PNG.</summary>
internal static class PngEncoder
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Writes a complete PNG while leaving the destination open.</summary>
    public static void Encode(PixelBuffer pixels, Stream destination, PngEncoderOptions options, bool isOpaque = false)
    {
        if ((uint)options.CompressionLevel > 9)
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "PNG compression level must be between 0 and 9.");
        }

        isOpaque |= pixels.BytesPerPixel == 3;
        destination.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)pixels.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)pixels.Height);
        header[8] = 8;
        header[9] = isOpaque ? (byte)2 : (byte)6;
        WriteChunk(destination, "IHDR"u8, header);

        CompressionLevel compression = options.CompressionLevel switch
        {
            0 => CompressionLevel.NoCompression,
            <= 3 => CompressionLevel.Fastest,
            >= 8 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        using (PngChunkStream chunks = new(destination))
        {
            using ZLibStream zlib = new(chunks, compression, leaveOpen: true);
            WriteFilteredRows(pixels, zlib, isOpaque);
        }

        WriteChunk(destination, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Streams fixed-Paeth rows to zlib, packing opaque RGBA storage only when needed.</summary>
    private static void WriteFilteredRows(PixelBuffer pixels, Stream zlib, bool isOpaque)
    {
        int bytesPerPixel = isOpaque ? 3 : 4;
        int rowLength = checked(pixels.Width * bytesPerPixel);
        bool rowsAreDirect = pixels.BytesPerPixel == bytesPerPixel;
        if (rowsAreDirect && ParallelExecution.ShouldRun((long)rowLength * pixels.Height, 4L * 1024 * 1024))
        {
            WriteFilteredRowsParallel(pixels, zlib, bytesPerPixel, rowLength);
            return;
        }

        bool packRgba = isOpaque && pixels.BytesPerPixel == 4;
        byte[] filteredArray = ArrayPool<byte>.Shared.Rent(rowLength);
        byte[]? currentArray = packRgba ? ArrayPool<byte>.Shared.Rent(rowLength) : null;
        byte[]? previousArray = packRgba ? ArrayPool<byte>.Shared.Rent(rowLength) : null;
        try
        {
            for (int y = 0; y < pixels.Height; y++)
            {
                ReadOnlySpan<byte> row;
                ReadOnlySpan<byte> previous;
                if (packRgba)
                {
                    Span<byte> packed = currentArray!.AsSpan(0, rowLength);
                    PackRgb(pixels.GetRowSpan(y), packed);
                    row = packed;
                    previous = y == 0 ? default : previousArray!.AsSpan(0, rowLength);
                }
                else
                {
                    row = pixels.GetRowSpan(y);
                    previous = y == 0 ? default : pixels.GetRowSpan(y - 1);
                }

                Span<byte> filtered = filteredArray.AsSpan(0, rowLength);
                FilterPaeth(row, previous, filtered, bytesPerPixel);
                zlib.WriteByte(4);
                zlib.Write(filtered);
                if (packRgba)
                {
                    (currentArray, previousArray) = (previousArray, currentArray);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(filteredArray);
            if (currentArray is not null) ArrayPool<byte>.Shared.Return(currentArray);
            if (previousArray is not null) ArrayPool<byte>.Shared.Return(previousArray);
        }
    }

    /// <summary>Filters bounded row batches concurrently, then submits each batch to zlib in source order.</summary>
    private static void WriteFilteredRowsParallel(PixelBuffer pixels, Stream zlib, int bytesPerPixel, int rowLength)
    {
        int encodedStride = checked(rowLength + 1);
        int batchRows = Math.Max(1, Math.Min(pixels.Height, (4 * 1024 * 1024) / encodedStride));
        byte[] batchArray = ArrayPool<byte>.Shared.Rent(checked(encodedStride * batchRows));
        try
        {
            for (int firstRow = 0; firstRow < pixels.Height; firstRow += batchRows)
            {
                int rowCount = Math.Min(batchRows, pixels.Height - firstRow);
                int batchStart = firstRow;
                ParallelExecution.For(0, rowCount, parallel: true, localRow =>
                {
                    int y = batchStart + localRow;
                    Span<byte> encoded = batchArray.AsSpan(localRow * encodedStride, encodedStride);
                    encoded[0] = 4;
                    FilterPaeth(
                        pixels.GetRowSpan(y),
                        y == 0 ? default : pixels.GetRowSpan(y - 1),
                        encoded[1..],
                        bytesPerPixel);
                });

                zlib.Write(batchArray.AsSpan(0, rowCount * encodedStride));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batchArray);
        }
    }

    /// <summary>Packs one tightly stored RGBA row into RGB without inspecting alpha.</summary>
    private static void PackRgb(ReadOnlySpan<byte> rgba, Span<byte> rgb)
    {
        ref byte source = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(rgba);
        ref byte target = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(rgb);
        int pixels = rgb.Length / 3;
        for (int x = 0; x < pixels; x++)
        {
            int input = x * 4;
            int output = x * 3;
            Unsafe.Add(ref target, output) = Unsafe.Add(ref source, input);
            Unsafe.Add(ref target, output + 1) = Unsafe.Add(ref source, input + 1);
            Unsafe.Add(ref target, output + 2) = Unsafe.Add(ref source, input + 2);
        }
    }

    /// <summary>Applies the PNG Paeth filter using unfiltered source neighbors.</summary>
    private static void FilterPaeth(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, Span<byte> output, int bytesPerPixel)
    {
        ref byte current = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(row);
        ref byte prior = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(previous);
        ref byte target = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(output);
        int leading = Math.Min(bytesPerPixel, row.Length);
        if (previous.IsEmpty)
        {
            for (int x = 0; x < leading; x++) Unsafe.Add(ref target, x) = Unsafe.Add(ref current, x);
            for (int x = bytesPerPixel; x < row.Length; x++)
                Unsafe.Add(ref target, x) = unchecked((byte)(Unsafe.Add(ref current, x) - Unsafe.Add(ref current, x - bytesPerPixel)));
            return;
        }

        for (int x = 0; x < leading; x++)
            Unsafe.Add(ref target, x) = unchecked((byte)(Unsafe.Add(ref current, x) - Unsafe.Add(ref prior, x)));
        for (int x = bytesPerPixel; x < row.Length; x++)
        {
            int left = Unsafe.Add(ref current, x - bytesPerPixel);
            int above = Unsafe.Add(ref prior, x);
            int upperLeft = Unsafe.Add(ref prior, x - bytesPerPixel);
            Unsafe.Add(ref target, x) = unchecked((byte)(Unsafe.Add(ref current, x) - Paeth(left, above, upperLeft)));
        }
    }

    /// <summary>Applies and scores one forward PNG filter in a single pass.</summary>
    private static long FilterAndScore(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, Span<byte> output, int bytesPerPixel, int filter)
    {
        long score = 0;
        switch (filter)
        {
            case 1:
                for (int x = 0; x < bytesPerPixel && x < row.Length; x++)
                    score += StoreResidual(output, x, row[x]);
                for (int x = bytesPerPixel; x < row.Length; x++)
                    score += StoreResidual(output, x, row[x] - row[x - bytesPerPixel]);
                break;
            case 2:
                if (previous.IsEmpty)
                {
                    row.CopyTo(output);
                    return Score(output[..row.Length]);
                }

                for (int x = 0; x < row.Length; x++)
                    score += StoreResidual(output, x, row[x] - previous[x]);
                break;
            case 3:
                for (int x = 0; x < row.Length; x++)
                {
                    int left = x >= bytesPerPixel ? row[x - bytesPerPixel] : 0;
                    int above = previous.IsEmpty ? 0 : previous[x];
                    score += StoreResidual(output, x, row[x] - ((left + above) >> 1));
                }

                break;
            case 4:
                for (int x = 0; x < row.Length; x++)
                {
                    int left = x >= bytesPerPixel ? row[x - bytesPerPixel] : 0;
                    int above = previous.IsEmpty ? 0 : previous[x];
                    int upperLeft = previous.IsEmpty || x < bytesPerPixel ? 0 : previous[x - bytesPerPixel];
                    score += StoreResidual(output, x, row[x] - Paeth(left, above, upperLeft));
                }

                break;
        }

        return score;
    }

    /// <summary>Stores one modulo-256 residual and returns its signed magnitude.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StoreResidual(Span<byte> output, int index, int residual)
    {
        byte value = unchecked((byte)residual);
        output[index] = value;
        return Math.Abs((int)unchecked((sbyte)value));
    }

    /// <summary>Scores filtered bytes as signed residual magnitudes.</summary>
    private static long Score(ReadOnlySpan<byte> row)
    {
        long score = 0;
        foreach (byte value in row)
        {
            score += Math.Abs((int)unchecked((sbyte)value));
        }

        return score;
    }

    /// <summary>Writes a length-delimited PNG chunk with CRC.</summary>
    internal static void WriteChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> prefix = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)payload.Length);
        type.CopyTo(prefix[4..]);
        destination.Write(prefix);
        destination.Write(payload);
        Span<byte> suffix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(suffix, Crc32.Compute(type, payload));
        destination.Write(suffix);
    }

    /// <summary>Returns the Paeth predictor for forward filtering.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Paeth(int left, int above, int upperLeft)
    {
        int leftDistance = Math.Abs(above - upperLeft);
        int aboveDistance = Math.Abs(left - upperLeft);
        int upperLeftDistance = Math.Abs(left + above - (upperLeft << 1));
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance ? left : aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    /// <summary>Buffers zlib output into bounded IDAT chunks.</summary>
    private sealed class PngChunkStream : Stream
    {
        private readonly Stream destination;
        private byte[]? buffer;
        private int length;
        private bool disposed;

        /// <summary>Initializes a chunking stream.</summary>
        public PngChunkStream(Stream destination)
        {
            this.destination = destination;
            buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        }

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override void Flush() => FlushChunk();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] source, int offset, int count) => Write(source.AsSpan(offset, count));

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> source)
        {
            while (!source.IsEmpty)
            {
                byte[] target = buffer ?? throw new ObjectDisposedException(nameof(PngChunkStream));
                int copy = Math.Min(source.Length, target.Length - length);
                source[..copy].CopyTo(target.AsSpan(length));
                length += copy;
                source = source[copy..];
                if (length == target.Length)
                {
                    FlushChunk();
                }
            }
        }

        /// <summary>Flushes pending bytes as one IDAT chunk.</summary>
        private void FlushChunk()
        {
            if (length != 0)
            {
                WriteChunk(destination, "IDAT"u8, buffer.AsSpan(0, length));
                length = 0;
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                FlushChunk();
                ArrayPool<byte>.Shared.Return(buffer!);
                buffer = null;
                disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
