using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.Png;

/// <summary>Decodes static PNG images into RGB24 or RGBA32 storage.</summary>
internal static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 =
    [
        (0, 0, 8, 8),
        (4, 0, 8, 8),
        (0, 4, 4, 8),
        (2, 0, 4, 4),
        (0, 2, 2, 4),
        (1, 0, 2, 2),
        (0, 1, 1, 2),
    ];

    /// <summary>Decodes one complete PNG byte sequence.</summary>
    public static unsafe DecodedImage Decode(ReadOnlySpan<byte> data, long maximumPixels)
    {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
        {
            ThrowHelper.InvalidData("The PNG signature is invalid.");
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = -1;
        int interlace = 0;
        bool sawHeader = false;
        bool sawEnd = false;
        byte[]? palette = null;
        byte[]? transparency = null;
        List<(int Offset, int Length)> idat = new();
        int compressedLength = 0;
        int offset = Signature.Length;

        while (offset < data.Length)
        {
            BinaryPrimitivesEx.Ensure(data, offset, 12);
            uint lengthValue = BinaryPrimitivesEx.ReadUInt32BigEndian(data, offset);
            if (lengthValue > int.MaxValue)
            {
                ThrowHelper.InvalidData("A PNG chunk is too large.");
            }

            int length = (int)lengthValue;
            BinaryPrimitivesEx.Ensure(data, offset + 8, checked(length + 4));
            ReadOnlySpan<byte> type = data.Slice(offset + 4, 4);
            ReadOnlySpan<byte> payload = data.Slice(offset + 8, length);
            uint declaredCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + length, 4));
            if (Crc32.Compute(type, payload) != declaredCrc)
            {
                ThrowHelper.InvalidData("A PNG chunk has an invalid CRC.");
            }

            uint chunkType = BinaryPrimitives.ReadUInt32BigEndian(type);
            switch (chunkType)
            {
                case 0x49484452: // IHDR
                    if (sawHeader || length != 13 || offset != Signature.Length)
                    {
                        ThrowHelper.InvalidData("The PNG header is invalid or out of order.");
                    }

                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload[4..]));
                    bitDepth = payload[8];
                    colorType = payload[9];
                    if (payload[10] != 0 || payload[11] != 0 || payload[12] > 1)
                    {
                        ThrowHelper.Unsupported("The PNG compression, filter, or interlace method is unsupported.");
                    }

                    interlace = payload[12];
                    ValidateColorType(colorType, bitDepth);
                    ThrowHelper.ValidateDimensions(width, height, maximumPixels);
                    sawHeader = true;
                    break;

                case 0x504C5445: // PLTE
                    if (!sawHeader || length == 0 || length > 768 || length % 3 != 0)
                    {
                        ThrowHelper.InvalidData("The PNG palette is invalid.");
                    }

                    palette = payload.ToArray();
                    break;

                case 0x74524E53: // tRNS
                    transparency = payload.ToArray();
                    break;

                case 0x49444154: // IDAT
                    if (!sawHeader)
                    {
                        ThrowHelper.InvalidData("PNG image data precedes its header.");
                    }

                    idat.Add((offset + 8, length));
                    compressedLength = checked(compressedLength + length);
                    break;

                case 0x6163544C: // acTL
                    ThrowHelper.Unsupported("Animated PNG is not supported.");
                    break;

                case 0x49454E44: // IEND
                    sawEnd = true;
                    offset = checked(offset + 12 + length);
                    goto Parsed;

                default:
                    // Unknown critical chunks cannot be ignored safely. Ancillary chunks carry
                    // metadata only and are deliberately stripped by the decode/encode pipeline.
                    if ((type[0] & 0x20) == 0)
                    {
                        ThrowHelper.Unsupported($"Unsupported critical PNG chunk '{System.Text.Encoding.ASCII.GetString(type)}'.");
                    }

                    break;
            }

            offset = checked(offset + 12 + length);
        }

    Parsed:
        if (!sawHeader || !sawEnd || idat.Count == 0)
        {
            ThrowHelper.InvalidData("The PNG stream is incomplete.");
        }

        if (colorType == 3 && palette is null)
        {
            ThrowHelper.InvalidData("An indexed PNG has no palette.");
        }

        bool usePackedRgb = colorType == 2 && bitDepth == 8 && transparency is null && interlace == 0;
        PixelBuffer pixels = new(width, height, usePackedRgb ? 3 : 4);
        try
        {
            fixed (byte* input = data)
            {
                using IdatStream compressedStream = new(input, data.Length, idat, compressedLength);
                using ZLibStream zlib = new(compressedStream, CompressionMode.Decompress, leaveOpen: false);
                if (interlace == 0)
                {
                    DecodePass(zlib, pixels, width, height, 0, 0, 1, 1, colorType, bitDepth, palette, transparency);
                }
                else
                {
                    foreach ((int x, int y, int dx, int dy) in Adam7)
                    {
                        int passWidth = PassLength(width, x, dx);
                        int passHeight = PassLength(height, y, dy);
                        if (passWidth != 0 && passHeight != 0)
                        {
                            DecodePass(zlib, pixels, passWidth, passHeight, x, y, dx, dy, colorType, bitDepth, palette, transparency);
                        }
                    }
                }

                if (zlib.ReadByte() != -1)
                {
                    ThrowHelper.InvalidData("PNG image data contains trailing decompressed bytes.");
                }
            }

            bool isOpaque = colorType switch
            {
                0 or 2 => transparency is null,
                3 => PaletteIsOpaque(transparency),
                _ => PixelsAreOpaque(pixels),
            };
            return new DecodedImage(pixels, ExifOrientation.Normal, isOpaque: isOpaque);
        }
        catch (InvalidDataException exception)
        {
            pixels.Dispose();
            throw new Image2DException(ImageErrorCode.InvalidData, "PNG deflate data is invalid.", exception);
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    /// <summary>Decodes and de-filters one non-interlaced or Adam7 pass.</summary>
    private static void DecodePass(
        Stream stream,
        PixelBuffer pixels,
        int passWidth,
        int passHeight,
        int startX,
        int startY,
        int stepX,
        int stepY,
        int colorType,
        int bitDepth,
        byte[]? palette,
        byte[]? transparency)
    {
        if (colorType == 2 && bitDepth == 8 && transparency is null && startX == 0 && startY == 0 && stepX == 1 && stepY == 1)
        {
            DecodeOpaqueRgb8(stream, pixels, passWidth, passHeight);
            return;
        }

        int channels = GetChannelCount(colorType);
        int bitsPerPixel = checked(channels * bitDepth);
        int rowLength = checked((passWidth * bitsPerPixel + 7) / 8);
        int filterBytesPerPixel = Math.Max(1, (bitsPerPixel + 7) / 8);
        byte[] previousArray = ArrayPool<byte>.Shared.Rent(rowLength);
        byte[] currentArray = ArrayPool<byte>.Shared.Rent(rowLength);
        Span<byte> previous = previousArray.AsSpan(0, rowLength);
        Span<byte> current = currentArray.AsSpan(0, rowLength);
        previous.Clear();

        try
        {
            for (int passY = 0; passY < passHeight; passY++)
            {
                int filter = stream.ReadByte();
                if (filter < 0)
                {
                    ThrowHelper.UnexpectedEnd();
                }

                ReadExactly(stream, current);
                Unfilter(current, previous, filterBytesPerPixel, filter, passY != 0);
                WritePixels(current, pixels, passWidth, startX, startY + passY * stepY, stepX, colorType, bitDepth, palette, transparency);
                (previousArray, currentArray) = (currentArray, previousArray);
                previous = previousArray.AsSpan(0, rowLength);
                current = currentArray.AsSpan(0, rowLength);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(previousArray);
            ArrayPool<byte>.Shared.Return(currentArray);
        }
    }

    /// <summary>Reverses RGB8 filters directly in the packed opaque destination.</summary>
    private static void DecodeOpaqueRgb8(Stream stream, PixelBuffer pixels, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            int filter = stream.ReadByte();
            if ((uint)filter > 4)
            {
                if (filter < 0) ThrowHelper.UnexpectedEnd();
                ThrowHelper.InvalidData("The PNG row uses an unknown filter.");
            }

            Span<byte> target = pixels.GetRowSpan(y);
            ReadExactly(stream, target);
            ReadOnlySpan<byte> previous = y == 0 ? default : pixels.GetRowSpan(y - 1);
            DecodeOpaqueRgb8Row(target, previous, target, filter);
        }
    }

    /// <summary>Decodes one packed RGB residual row directly into RGB24 storage.</summary>
    private static void DecodeOpaqueRgb8Row(ReadOnlySpan<byte> residual, ReadOnlySpan<byte> previous, Span<byte> target, int filter)
    {
        if (filter == 4)
        {
            if (!previous.IsEmpty && Avx2.IsSupported && SimdPrimitives.ForcedMode == SimdMode.Avx2)
            {
                DecodeOpaqueRgb8PaethAvx2(residual, previous, target);
            }
            else if (!previous.IsEmpty && SimdPrimitives.AllowSsse3)
            {
                DecodeOpaqueRgb8PaethSsse3(residual, previous, target);
            }
            else
            {
                DecodeOpaqueRgb8Paeth(residual, previous, target);
            }

            return;
        }

        ref byte source = ref MemoryMarshal.GetReference(residual);
        ref byte prior = ref MemoryMarshal.GetReference(previous);
        ref byte output = ref MemoryMarshal.GetReference(target);
        int pixels = residual.Length / 3;
        bool hasPrevious = !previous.IsEmpty;
        for (int x = 0; x < pixels; x++)
        {
            int input = x * 3;
            int destination = input;
            int leftOffset = destination - 3;
            for (int channel = 0; channel < 3; channel++)
            {
                int left = x == 0 ? 0 : Unsafe.Add(ref output, leftOffset + channel);
                int above = hasPrevious ? Unsafe.Add(ref prior, destination + channel) : 0;
                int upperLeft = x == 0 || !hasPrevious ? 0 : Unsafe.Add(ref prior, leftOffset + channel);
                int predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    _ => (left + above) >> 1,
                };
                Unsafe.Add(ref output, destination + channel) = unchecked((byte)(Unsafe.Add(ref source, input + channel) + predictor));
            }

        }
    }

    /// <summary>Provides an exact forced-AVX2 implementation for cross-path verification.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecodeOpaqueRgb8PaethAvx2(ReadOnlySpan<byte> residual, ReadOnlySpan<byte> previous, Span<byte> target)
    {
        ref byte source = ref MemoryMarshal.GetReference(residual);
        ref byte prior = ref MemoryMarshal.GetReference(previous);
        ref byte output = ref MemoryMarshal.GetReference(target);
        Vector256<short> above = Vector256<short>.Zero;
        Vector256<short> decoded = Vector256<short>.Zero;
        Vector256<short> byteMask = Vector256.Create((short)255);
        int offset = 0;
        int vectorEnd = residual.Length - 3;
        for (; offset < vectorEnd; offset += 3)
        {
            Vector256<short> upperLeft = above;
            Vector256<short> left = decoded;
            Vector128<byte> packedAbove = Vector128.CreateScalar(Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref prior, offset))).AsByte();
            Vector128<byte> packedResidual = Vector128.CreateScalar(Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref source, offset))).AsByte();
            above = Vector256.Create(Sse2.UnpackLow(packedAbove, Vector128<byte>.Zero).AsInt16(), Vector128<short>.Zero);
            decoded = Vector256.Create(Sse2.UnpackLow(packedResidual, Vector128<byte>.Zero).AsInt16(), Vector128<short>.Zero);

            Vector256<short> leftDistance = Avx2.Abs(Avx2.Subtract(above, upperLeft)).AsInt16();
            Vector256<short> aboveDistance = Avx2.Abs(Avx2.Subtract(left, upperLeft)).AsInt16();
            Vector256<short> upperLeftDistance = Avx2.Abs(Avx2.Add(Avx2.Subtract(above, upperLeft), Avx2.Subtract(left, upperLeft))).AsInt16();
            Vector256<short> smallest = Avx2.Min(upperLeftDistance, Avx2.Min(leftDistance, aboveDistance));
            Vector256<short> chooseAbove = Avx2.CompareEqual(smallest, aboveDistance);
            Vector256<short> predictor = Avx2.Or(Avx2.And(chooseAbove, above), Avx2.AndNot(chooseAbove, upperLeft));
            Vector256<short> chooseLeft = Avx2.CompareEqual(smallest, leftDistance);
            predictor = Avx2.Or(Avx2.And(chooseLeft, left), Avx2.AndNot(chooseLeft, predictor));
            decoded = Avx2.And(Avx2.Add(decoded, predictor), byteMask);

            uint packed = (uint)Sse2.ConvertToInt32(Avx2.PackUnsignedSaturate(decoded, Vector256<short>.Zero).GetLower().AsInt32());
            Unsafe.Add(ref output, offset) = (byte)packed;
            Unsafe.Add(ref output, offset + 1) = (byte)(packed >> 8);
            Unsafe.Add(ref output, offset + 2) = (byte)(packed >> 16);
        }

        if (offset < residual.Length)
        {
            byte aboveRed = Unsafe.Add(ref prior, offset);
            byte aboveGreen = Unsafe.Add(ref prior, offset + 1);
            byte aboveBlue = Unsafe.Add(ref prior, offset + 2);
            byte leftRed = (byte)decoded.GetElement(0);
            byte leftGreen = (byte)decoded.GetElement(1);
            byte leftBlue = (byte)decoded.GetElement(2);
            byte upperLeftRed = (byte)above.GetElement(0);
            byte upperLeftGreen = (byte)above.GetElement(1);
            byte upperLeftBlue = (byte)above.GetElement(2);
            Unsafe.Add(ref output, offset) = unchecked((byte)(Unsafe.Add(ref source, offset) + Paeth(leftRed, aboveRed, upperLeftRed)));
            Unsafe.Add(ref output, offset + 1) = unchecked((byte)(Unsafe.Add(ref source, offset + 1) + Paeth(leftGreen, aboveGreen, upperLeftGreen)));
            Unsafe.Add(ref output, offset + 2) = unchecked((byte)(Unsafe.Add(ref source, offset + 2) + Paeth(leftBlue, aboveBlue, upperLeftBlue)));
        }
    }

    /// <summary>Evaluates the three independent RGB Paeth chains in parallel 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecodeOpaqueRgb8PaethSsse3(ReadOnlySpan<byte> residual, ReadOnlySpan<byte> previous, Span<byte> target)
    {
        ref byte source = ref MemoryMarshal.GetReference(residual);
        ref byte prior = ref MemoryMarshal.GetReference(previous);
        ref byte output = ref MemoryMarshal.GetReference(target);
        Vector128<short> above = Vector128<short>.Zero;
        Vector128<short> decoded = Vector128<short>.Zero;
        Vector128<short> byteMask = Vector128.Create((short)255);
        int offset = 0;
        int vectorEnd = residual.Length - 3;
        for (; offset < vectorEnd; offset += 3)
        {
            Vector128<short> upperLeft = above;
            Vector128<short> left = decoded;
            Vector128<byte> packedAbove = Vector128.CreateScalar(Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref prior, offset))).AsByte();
            Vector128<byte> packedResidual = Vector128.CreateScalar(Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref source, offset))).AsByte();
            above = Sse2.UnpackLow(packedAbove, Vector128<byte>.Zero).AsInt16();
            decoded = Sse2.UnpackLow(packedResidual, Vector128<byte>.Zero).AsInt16();

            Vector128<short> leftDistance = Ssse3.Abs(Sse2.Subtract(above, upperLeft)).AsInt16();
            Vector128<short> aboveDistance = Ssse3.Abs(Sse2.Subtract(left, upperLeft)).AsInt16();
            Vector128<short> upperLeftDistance = Ssse3.Abs(Sse2.Add(Sse2.Subtract(above, upperLeft), Sse2.Subtract(left, upperLeft))).AsInt16();
            Vector128<short> smallest = Sse2.Min(upperLeftDistance, Sse2.Min(leftDistance, aboveDistance));
            Vector128<short> chooseAbove = Sse2.CompareEqual(smallest, aboveDistance);
            Vector128<short> predictor = Sse2.Or(Sse2.And(chooseAbove, above), Sse2.AndNot(chooseAbove, upperLeft));
            Vector128<short> chooseLeft = Sse2.CompareEqual(smallest, leftDistance);
            predictor = Sse2.Or(Sse2.And(chooseLeft, left), Sse2.AndNot(chooseLeft, predictor));
            decoded = Sse2.And(Sse2.Add(decoded, predictor), byteMask);

            uint packed = (uint)Sse2.ConvertToInt32(Sse2.PackUnsignedSaturate(decoded, Vector128<short>.Zero).AsInt32());
            Unsafe.Add(ref output, offset) = (byte)packed;
            Unsafe.Add(ref output, offset + 1) = (byte)(packed >> 8);
            Unsafe.Add(ref output, offset + 2) = (byte)(packed >> 16);
        }

        if (offset < residual.Length)
        {
            byte aboveRed = Unsafe.Add(ref prior, offset);
            byte aboveGreen = Unsafe.Add(ref prior, offset + 1);
            byte aboveBlue = Unsafe.Add(ref prior, offset + 2);
            byte leftRed = (byte)decoded.GetElement(0);
            byte leftGreen = (byte)decoded.GetElement(1);
            byte leftBlue = (byte)decoded.GetElement(2);
            byte upperLeftRed = (byte)above.GetElement(0);
            byte upperLeftGreen = (byte)above.GetElement(1);
            byte upperLeftBlue = (byte)above.GetElement(2);
            Unsafe.Add(ref output, offset) = unchecked((byte)(Unsafe.Add(ref source, offset) + Paeth(leftRed, aboveRed, upperLeftRed)));
            Unsafe.Add(ref output, offset + 1) = unchecked((byte)(Unsafe.Add(ref source, offset + 1) + Paeth(leftGreen, aboveGreen, upperLeftGreen)));
            Unsafe.Add(ref output, offset + 2) = unchecked((byte)(Unsafe.Add(ref source, offset + 2) + Paeth(leftBlue, aboveBlue, upperLeftBlue)));
        }
    }

    /// <summary>Reverses packed RGB Paeth residuals three channels at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecodeOpaqueRgb8Paeth(ReadOnlySpan<byte> residual, ReadOnlySpan<byte> previous, Span<byte> target)
    {
        ref byte source = ref MemoryMarshal.GetReference(residual);
        ref byte prior = ref MemoryMarshal.GetReference(previous);
        ref byte output = ref MemoryMarshal.GetReference(target);
        int pixels = residual.Length / 3;
        if (pixels == 0) return;

        if (previous.IsEmpty)
        {
            byte leftRed = Unsafe.Add(ref source, 0);
            byte leftGreen = Unsafe.Add(ref source, 1);
            byte leftBlue = Unsafe.Add(ref source, 2);
            Unsafe.Add(ref output, 0) = leftRed;
            Unsafe.Add(ref output, 1) = leftGreen;
            Unsafe.Add(ref output, 2) = leftBlue;
            for (int input = 3; input < residual.Length; input += 3)
            {
                leftRed = unchecked((byte)(Unsafe.Add(ref source, input) + leftRed));
                leftGreen = unchecked((byte)(Unsafe.Add(ref source, input + 1) + leftGreen));
                leftBlue = unchecked((byte)(Unsafe.Add(ref source, input + 2) + leftBlue));
                Unsafe.Add(ref output, input) = leftRed;
                Unsafe.Add(ref output, input + 1) = leftGreen;
                Unsafe.Add(ref output, input + 2) = leftBlue;
            }

            return;
        }

        byte aboveRed = Unsafe.Add(ref prior, 0);
        byte aboveGreen = Unsafe.Add(ref prior, 1);
        byte aboveBlue = Unsafe.Add(ref prior, 2);
        byte decodedRed = unchecked((byte)(Unsafe.Add(ref source, 0) + aboveRed));
        byte decodedGreen = unchecked((byte)(Unsafe.Add(ref source, 1) + aboveGreen));
        byte decodedBlue = unchecked((byte)(Unsafe.Add(ref source, 2) + aboveBlue));
        Unsafe.Add(ref output, 0) = decodedRed;
        Unsafe.Add(ref output, 1) = decodedGreen;
        Unsafe.Add(ref output, 2) = decodedBlue;
        for (int input = 3; input < residual.Length; input += 3)
        {
            byte nextAboveRed = Unsafe.Add(ref prior, input);
            byte nextAboveGreen = Unsafe.Add(ref prior, input + 1);
            byte nextAboveBlue = Unsafe.Add(ref prior, input + 2);
            decodedRed = unchecked((byte)(Unsafe.Add(ref source, input) + Paeth(decodedRed, nextAboveRed, aboveRed)));
            decodedGreen = unchecked((byte)(Unsafe.Add(ref source, input + 1) + Paeth(decodedGreen, nextAboveGreen, aboveGreen)));
            decodedBlue = unchecked((byte)(Unsafe.Add(ref source, input + 2) + Paeth(decodedBlue, nextAboveBlue, aboveBlue)));
            Unsafe.Add(ref output, input) = decodedRed;
            Unsafe.Add(ref output, input + 1) = decodedGreen;
            Unsafe.Add(ref output, input + 2) = decodedBlue;
            aboveRed = nextAboveRed;
            aboveGreen = nextAboveGreen;
            aboveBlue = nextAboveBlue;
        }
    }

    /// <summary>Reverses a PNG row filter in-place.</summary>
    private static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int bytesPerPixel, int filter, bool hasPrevious)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (int x = bytesPerPixel; x < row.Length; x++)
                {
                    row[x] = unchecked((byte)(row[x] + row[x - bytesPerPixel]));
                }

                return;
            case 2:
                if (!hasPrevious) return;
                int vectorLength = Vector<byte>.Count;
                int vectorEnd = row.Length - (row.Length % vectorLength);
                int vectorX = 0;
                if (Vector.IsHardwareAccelerated && SimdPrimitives.AllowPortableVector)
                {
                    for (; vectorX < vectorEnd; vectorX += vectorLength)
                    {
                        Vector<byte> current = new(row.Slice(vectorX, vectorLength));
                        Vector<byte> above = new(previous.Slice(vectorX, vectorLength));
                        (current + above).CopyTo(row.Slice(vectorX, vectorLength));
                    }
                }

                for (int x = vectorX; x < row.Length; x++)
                {
                    row[x] = unchecked((byte)(row[x] + previous[x]));
                }

                return;
            case 3:
                for (int x = 0; x < row.Length; x++)
                {
                    int left = x >= bytesPerPixel ? row[x - bytesPerPixel] : 0;
                    row[x] = unchecked((byte)(row[x] + ((left + previous[x]) >> 1)));
                }

                return;
            case 4:
                ref byte currentRef = ref MemoryMarshal.GetReference(row);
                ref byte priorRef = ref MemoryMarshal.GetReference(previous);
                if (!hasPrevious)
                {
                    for (int x = bytesPerPixel; x < row.Length; x++)
                    {
                        Unsafe.Add(ref currentRef, x) = unchecked((byte)(Unsafe.Add(ref currentRef, x) + Unsafe.Add(ref currentRef, x - bytesPerPixel)));
                    }

                    return;
                }

                int leading = Math.Min(bytesPerPixel, row.Length);
                for (int x = 0; x < leading; x++)
                    Unsafe.Add(ref currentRef, x) = unchecked((byte)(Unsafe.Add(ref currentRef, x) + Unsafe.Add(ref priorRef, x)));
                for (int x = bytesPerPixel; x < row.Length; x++)
                {
                    int left = Unsafe.Add(ref currentRef, x - bytesPerPixel);
                    int above = Unsafe.Add(ref priorRef, x);
                    int upperLeft = Unsafe.Add(ref priorRef, x - bytesPerPixel);
                    Unsafe.Add(ref currentRef, x) = unchecked((byte)(Unsafe.Add(ref currentRef, x) + Paeth(left, above, upperLeft)));
                }

                return;
            default:
                ThrowHelper.InvalidData("The PNG row uses an unknown filter.");
                return;
        }
    }

    /// <summary>Converts one unpacked scanline into RGBA32 output positions.</summary>
    private static void WritePixels(
        ReadOnlySpan<byte> row,
        PixelBuffer pixels,
        int passWidth,
        int startX,
        int y,
        int stepX,
        int colorType,
        int bitDepth,
        byte[]? palette,
        byte[]? transparency)
    {
        if (bitDepth == 8 && startX == 0 && stepX == 1)
        {
            Span<byte> fastTarget = pixels.GetRowSpan(y);
            if (colorType == 6)
            {
                row.CopyTo(fastTarget);
                return;
            }

            if (colorType == 2 && transparency is null)
            {
                int source = 0;
                int output = 0;
                for (int x = 0; x < passWidth; x++)
                {
                    fastTarget[output] = row[source];
                    fastTarget[output + 1] = row[source + 1];
                    fastTarget[output + 2] = row[source + 2];
                    fastTarget[output + 3] = 255;
                    source += 3;
                    output += 4;
                }

                return;
            }

            if (colorType == 0 && transparency is null)
            {
                int output = 0;
                for (int x = 0; x < passWidth; x++)
                {
                    byte gray = row[x];
                    fastTarget[output] = gray;
                    fastTarget[output + 1] = gray;
                    fastTarget[output + 2] = gray;
                    fastTarget[output + 3] = 255;
                    output += 4;
                }

                return;
            }

            if (colorType == 4)
            {
                int source = 0;
                int output = 0;
                for (int x = 0; x < passWidth; x++)
                {
                    byte gray = row[source];
                    fastTarget[output] = gray;
                    fastTarget[output + 1] = gray;
                    fastTarget[output + 2] = gray;
                    fastTarget[output + 3] = row[source + 1];
                    source += 2;
                    output += 4;
                }

                return;
            }
        }

        int channels = GetChannelCount(colorType);
        int sampleIndex = 0;
        Span<byte> target = pixels.GetRowSpan(y);
        ushort transparentGray = transparency is { Length: >= 2 } ? BinaryPrimitives.ReadUInt16BigEndian(transparency) : ushort.MaxValue;
        ushort transparentRed = transparency is { Length: >= 6 } ? BinaryPrimitives.ReadUInt16BigEndian(transparency) : ushort.MaxValue;
        ushort transparentGreen = transparency is { Length: >= 6 } ? BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) : ushort.MaxValue;
        ushort transparentBlue = transparency is { Length: >= 6 } ? BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)) : ushort.MaxValue;

        for (int x = 0; x < passWidth; x++)
        {
            int output = checked((startX + x * stepX) * 4);
            int s0 = ReadSample(row, sampleIndex++, bitDepth);
            int s1 = channels > 1 ? ReadSample(row, sampleIndex++, bitDepth) : 0;
            int s2 = channels > 2 ? ReadSample(row, sampleIndex++, bitDepth) : 0;
            int s3 = channels > 3 ? ReadSample(row, sampleIndex++, bitDepth) : 0;

            switch (colorType)
            {
                case 0:
                    byte gray = ScaleSample(s0, bitDepth);
                    target[output] = gray;
                    target[output + 1] = gray;
                    target[output + 2] = gray;
                    target[output + 3] = transparency is not null && s0 == transparentGray ? (byte)0 : (byte)255;
                    break;
                case 2:
                    target[output] = ScaleSample(s0, bitDepth);
                    target[output + 1] = ScaleSample(s1, bitDepth);
                    target[output + 2] = ScaleSample(s2, bitDepth);
                    target[output + 3] = transparency is not null && s0 == transparentRed && s1 == transparentGreen && s2 == transparentBlue ? (byte)0 : (byte)255;
                    break;
                case 3:
                    int paletteOffset = checked(s0 * 3);
                    if (palette is null || paletteOffset + 2 >= palette.Length)
                    {
                        ThrowHelper.InvalidData("A PNG palette index is out of range.");
                    }

                    target[output] = palette[paletteOffset];
                    target[output + 1] = palette[paletteOffset + 1];
                    target[output + 2] = palette[paletteOffset + 2];
                    target[output + 3] = transparency is not null && s0 < transparency.Length ? transparency[s0] : (byte)255;
                    break;
                case 4:
                    gray = ScaleSample(s0, bitDepth);
                    target[output] = gray;
                    target[output + 1] = gray;
                    target[output + 2] = gray;
                    target[output + 3] = ScaleSample(s1, bitDepth);
                    break;
                case 6:
                    target[output] = ScaleSample(s0, bitDepth);
                    target[output + 1] = ScaleSample(s1, bitDepth);
                    target[output + 2] = ScaleSample(s2, bitDepth);
                    target[output + 3] = ScaleSample(s3, bitDepth);
                    break;
            }
        }
    }

    /// <summary>Reads one packed or byte-aligned sample.</summary>
    private static int ReadSample(ReadOnlySpan<byte> row, int index, int bitDepth)
    {
        if (bitDepth == 16)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(row.Slice(index * 2, 2));
        }

        if (bitDepth == 8)
        {
            return row[index];
        }

        int bitOffset = checked(index * bitDepth);
        int shift = 8 - bitDepth - (bitOffset & 7);
        return (row[bitOffset >> 3] >> shift) & ((1 << bitDepth) - 1);
    }

    /// <summary>Scales an integer sample to eight bits with correct endpoint mapping.</summary>
    private static byte ScaleSample(int sample, int bitDepth)
    {
        if (bitDepth == 8)
        {
            return (byte)sample;
        }

        if (bitDepth == 16)
        {
            return (byte)((sample + 128) / 257);
        }

        int maximum = (1 << bitDepth) - 1;
        return (byte)((sample * 255 + maximum / 2) / maximum);
    }

    /// <summary>Returns the channel count encoded by a PNG color type.</summary>
    private static int GetChannelCount(int colorType) => colorType switch
    {
        0 => 1,
        2 => 3,
        3 => 1,
        4 => 2,
        6 => 4,
        _ => throw new Image2DException(ImageErrorCode.InvalidData, "Unknown PNG color type."),
    };

    /// <summary>Validates the legal color-type and bit-depth matrix.</summary>
    private static void ValidateColorType(int colorType, int bitDepth)
    {
        bool valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false,
        };

        if (!valid)
        {
            ThrowHelper.Unsupported("The PNG color type and bit depth combination is unsupported or invalid.");
        }
    }

    /// <summary>Computes one Adam7 pass dimension.</summary>
    private static int PassLength(int fullLength, int start, int step) => fullLength <= start ? 0 : (fullLength - start + step - 1) / step;

    /// <summary>Reads exactly one scanline from the deflate stream.</summary>
    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                ThrowHelper.UnexpectedEnd();
            }

            offset += read;
        }
    }

    /// <summary>Returns the PNG Paeth predictor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Paeth(int left, int above, int upperLeft)
    {
        int leftDistance = Math.Abs(above - upperLeft);
        int aboveDistance = Math.Abs(left - upperLeft);
        int upperLeftDistance = Math.Abs(left + above - (upperLeft << 1));
        return (byte)(leftDistance <= aboveDistance && leftDistance <= upperLeftDistance ? left : aboveDistance <= upperLeftDistance ? above : upperLeft);
    }

    /// <summary>Returns whether a palette transparency table proves all entries opaque.</summary>
    private static bool PaletteIsOpaque(byte[]? transparency)
    {
        if (transparency is null) return true;
        foreach (byte alpha in transparency)
        {
            if (alpha != 255) return false;
        }

        return true;
    }

    /// <summary>Scans explicit alpha formats once while pixels are hot after decode.</summary>
    private static bool PixelsAreOpaque(PixelBuffer pixels)
    {
        ReadOnlySpan<byte> rgba = pixels.Span;
        for (int offset = 3; offset < rgba.Length; offset += 4)
        {
            if (rgba[offset] != 255) return false;
        }

        return true;
    }

    /// <summary>Exposes validated IDAT payloads as one forward-only stream without concatenating them.</summary>
    private sealed unsafe class IdatStream : Stream
    {
        private readonly byte* input;
        private readonly int inputLength;
        private readonly List<(int Offset, int Length)> segments;
        private readonly int totalLength;
        private int segmentIndex;
        private int segmentOffset;
        private int position;

        public IdatStream(byte* input, int inputLength, List<(int Offset, int Length)> segments, int totalLength)
        {
            this.input = input;
            this.inputLength = inputLength;
            this.segments = segments;
            this.totalLength = totalLength;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalLength;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int written = 0;
            while (!buffer.IsEmpty && segmentIndex < segments.Count)
            {
                (int offset, int length) = segments[segmentIndex];
                int available = length - segmentOffset;
                if (available == 0)
                {
                    segmentIndex++;
                    segmentOffset = 0;
                    continue;
                }

                int copy = Math.Min(available, buffer.Length);
                int sourceOffset = checked(offset + segmentOffset);
                if ((uint)sourceOffset > (uint)inputLength || copy > inputLength - sourceOffset)
                {
                    ThrowHelper.UnexpectedEnd();
                }

                new ReadOnlySpan<byte>(input + sourceOffset, copy).CopyTo(buffer);
                buffer = buffer[copy..];
                written += copy;
                position += copy;
                segmentOffset += copy;
            }

            return written;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
