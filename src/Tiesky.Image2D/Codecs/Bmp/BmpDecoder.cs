using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.Bmp;

/// <summary>Decodes Windows INFO, V4, and V5 BMP images.</summary>
internal static class BmpDecoder
{
    /// <summary>Decodes one BMP byte sequence.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> data, long maximumPixels)
    {
        BinaryPrimitivesEx.Ensure(data, 0, 54);
        if (data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            ThrowHelper.InvalidData("The BMP signature is invalid.");
        }

        uint declaredSize = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 2);
        int pixelOffset = checked((int)BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 10));
        int dibSize = checked((int)BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 14));
        if (dibSize < 40)
        {
            ThrowHelper.Unsupported("OS/2 BMP headers are not supported.");
        }

        BinaryPrimitivesEx.Ensure(data, 14, dibSize);
        int width = BinaryPrimitivesEx.ReadInt32LittleEndian(data, 18);
        int signedHeight = BinaryPrimitivesEx.ReadInt32LittleEndian(data, 22);
        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue)
        {
            ThrowHelper.InvalidData("The BMP dimensions are invalid.");
        }

        bool topDown = signedHeight < 0;
        int height = Math.Abs(signedHeight);
        ThrowHelper.ValidateDimensions(width, height, maximumPixels);

        ushort planes = BinaryPrimitivesEx.ReadUInt16LittleEndian(data, 26);
        ushort bitsPerPixel = BinaryPrimitivesEx.ReadUInt16LittleEndian(data, 28);
        uint compression = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 30);
        uint colorsUsed = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 46);
        if (planes != 1 || bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            ThrowHelper.Unsupported("The BMP plane count or bit depth is unsupported.");
        }

        if (compression is 4 or 5)
        {
            ThrowHelper.Unsupported("BMP files containing embedded JPEG or PNG data are not supported.");
        }

        bool compressionValid = compression switch
        {
            0 => true,
            1 => bitsPerPixel == 8,
            2 => bitsPerPixel == 4,
            3 or 6 => bitsPerPixel is 16 or 32,
            _ => false,
        };
        if (!compressionValid || (topDown && compression is 1 or 2))
        {
            ThrowHelper.Unsupported("The BMP compression and bit-depth combination is unsupported.");
        }

        if (declaredSize != 0 && declaredSize > data.Length)
        {
            ThrowHelper.UnexpectedEnd();
        }

        uint redMask = 0;
        uint greenMask = 0;
        uint blueMask = 0;
        uint alphaMask = 0;
        int paletteOffset = checked(14 + dibSize);

        if (compression is 3 or 6)
        {
            if (dibSize >= 52)
            {
                redMask = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 54);
                greenMask = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 58);
                blueMask = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 62);
                if (dibSize >= 56)
                {
                    alphaMask = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, 66);
                }
            }
            else
            {
                BinaryPrimitivesEx.Ensure(data, paletteOffset, compression == 6 ? 16 : 12);
                redMask = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, paletteOffset);
                greenMask = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, paletteOffset + 4);
                blueMask = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, paletteOffset + 8);
                if (compression == 6)
                {
                    alphaMask = BinaryPrimitivesEx.ReadUInt32LittleEndian(data, paletteOffset + 12);
                }

                paletteOffset += compression == 6 ? 16 : 12;
            }
        }
        else if (bitsPerPixel == 16)
        {
            redMask = 0x7C00;
            greenMask = 0x03E0;
            blueMask = 0x001F;
        }

        byte[]? palette = null;
        if (bitsPerPixel <= 8)
        {
            int maximumColors = 1 << bitsPerPixel;
            int colorCount = colorsUsed == 0 ? maximumColors : checked((int)colorsUsed);
            if (colorCount <= 0 || colorCount > maximumColors)
            {
                ThrowHelper.InvalidData("The BMP palette size is invalid.");
            }

            BinaryPrimitivesEx.Ensure(data, paletteOffset, checked(colorCount * 4));
            palette = new byte[colorCount * 4];
            for (int i = 0; i < colorCount; i++)
            {
                int source = paletteOffset + i * 4;
                int target = i * 4;
                palette[target] = data[source + 2];
                palette[target + 1] = data[source + 1];
                palette[target + 2] = data[source];
                palette[target + 3] = 255;
            }
        }

        if (pixelOffset < paletteOffset || pixelOffset > data.Length)
        {
            ThrowHelper.InvalidData("The BMP pixel offset is invalid.");
        }

        PixelBuffer pixels = new(width, height);
        try
        {
            if (compression is 1 or 2)
            {
                Span<byte> rgba = pixels.Span;
                for (int i = 3; i < rgba.Length; i += 4)
                {
                    rgba[i] = 255;
                }

                DecodeRle(data[pixelOffset..], pixels, palette!, compression == 2);
            }
            else
            {
                DecodeRows(data, pixelOffset, pixels, topDown, bitsPerPixel, palette, redMask, greenMask, blueMask, alphaMask);
            }

            bool isOpaque = alphaMask == 0 || PixelsAreOpaque(pixels);
            return new DecodedImage(pixels, ExifOrientation.Normal, isOpaque: isOpaque);
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    /// <summary>Decodes uncompressed or bitfield scanlines.</summary>
    private static void DecodeRows(
        ReadOnlySpan<byte> data,
        int pixelOffset,
        PixelBuffer pixels,
        bool topDown,
        int bitsPerPixel,
        byte[]? palette,
        uint redMask,
        uint greenMask,
        uint blueMask,
        uint alphaMask)
    {
        int stride = checked(((pixels.Width * bitsPerPixel + 31) / 32) * 4);
        BinaryPrimitivesEx.Ensure(data, pixelOffset, checked(stride * pixels.Height));
        BitfieldInfo bitfields = bitsPerPixel is 16 or 32 && redMask != 0
            ? new BitfieldInfo(redMask, greenMask, blueMask, alphaMask)
            : default;

        for (int sourceY = 0; sourceY < pixels.Height; sourceY++)
        {
            int targetY = topDown ? sourceY : pixels.Height - 1 - sourceY;
            ReadOnlySpan<byte> source = data.Slice(pixelOffset + sourceY * stride, stride);
            Span<byte> target = pixels.GetRowSpan(targetY);
            switch (bitsPerPixel)
            {
                case 1:
                    for (int x = 0; x < pixels.Width; x++)
                    {
                        WritePalette(target, x * 4, palette!, (source[x >> 3] >> (7 - (x & 7))) & 1);
                    }

                    break;
                case 4:
                    for (int x = 0; x < pixels.Width; x++)
                    {
                        int packed = source[x >> 1];
                        WritePalette(target, x * 4, palette!, (x & 1) == 0 ? packed >> 4 : packed & 15);
                    }

                    break;
                case 8:
                    for (int x = 0; x < pixels.Width; x++)
                    {
                        WritePalette(target, x * 4, palette!, source[x]);
                    }

                    break;
                case 16:
                    for (int x = 0; x < pixels.Width; x++)
                    {
                        uint value16 = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(x * 2, 2));
                        WriteMasked(target, x * 4, value16, bitfields);
                    }

                    break;
                case 24:
                    ConvertBgr24(source, target, pixels.Width);
                    break;
                case 32:
                    if (redMask == 0)
                    {
                        ConvertBgr32(source, target, pixels.Width);
                    }
                    else
                    {
                        for (int x = 0; x < pixels.Width; x++)
                        {
                            uint value32 = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(x * 4, 4));
                            WriteMasked(target, x * 4, value32, bitfields);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>Expands packed BGR24 rows four pixels at a time when SSSE3 is available.</summary>
    private static void ConvertBgr24(ReadOnlySpan<byte> source, Span<byte> target, int width)
    {
        int x = 0;
        int input = 0;
        int output = 0;
        if (SimdPrimitives.AllowSsse3)
        {
            Vector128<byte> shuffle = Vector128.Create(
                (byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80);
            Vector128<byte> alpha = Vector128.Create(
                (byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);
            ref byte sourceReference = ref MemoryMarshal.GetReference(source);
            ref byte targetReference = ref MemoryMarshal.GetReference(target);
            for (; x + 4 <= width && input + 16 <= source.Length; x += 4, input += 12, output += 16)
            {
                Vector128<byte> packed = Vector128.LoadUnsafe(ref sourceReference, (nuint)input);
                Vector128<byte> rgba = Ssse3.Shuffle(packed, shuffle) | alpha;
                rgba.StoreUnsafe(ref targetReference, (nuint)output);
            }
        }

        for (; x < width; x++, input += 3, output += 4)
        {
            target[output] = source[input + 2];
            target[output + 1] = source[input + 1];
            target[output + 2] = source[input];
            target[output + 3] = 255;
        }
    }

    /// <summary>Converts BGRX32 rows four pixels at a time while forcing opaque alpha.</summary>
    private static void ConvertBgr32(ReadOnlySpan<byte> source, Span<byte> target, int width)
    {
        int offset = 0;
        int length = width * 4;
        if (SimdPrimitives.AllowSsse3)
        {
            Vector128<byte> shuffle = Vector128.Create(
                (byte)2, 1, 0, 0x80, 6, 5, 4, 0x80, 10, 9, 8, 0x80, 14, 13, 12, 0x80);
            Vector128<byte> alpha = Vector128.Create(
                (byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);
            ref byte sourceReference = ref MemoryMarshal.GetReference(source);
            ref byte targetReference = ref MemoryMarshal.GetReference(target);
            for (; offset + 16 <= length; offset += 16)
            {
                Vector128<byte> bgra = Vector128.LoadUnsafe(ref sourceReference, (nuint)offset);
                Vector128<byte> rgba = Ssse3.Shuffle(bgra, shuffle) | alpha;
                rgba.StoreUnsafe(ref targetReference, (nuint)offset);
            }
        }

        for (; offset < length; offset += 4)
        {
            target[offset] = source[offset + 2];
            target[offset + 1] = source[offset + 1];
            target[offset + 2] = source[offset];
            target[offset + 3] = 255;
        }
    }

    /// <summary>Decodes Microsoft RLE4 or RLE8 commands into the palette image.</summary>
    private static void DecodeRle(ReadOnlySpan<byte> data, PixelBuffer pixels, byte[] palette, bool fourBit)
    {
        int x = 0;
        int y = pixels.Height - 1;
        int offset = 0;
        bool ended = false;

        while (offset < data.Length && !ended)
        {
            BinaryPrimitivesEx.Ensure(data, offset, 2);
            int count = data[offset++];
            int command = data[offset++];
            if (y < 0 && (count != 0 || command != 1))
            {
                ThrowHelper.InvalidData("BMP RLE data continues below the image bounds.");
            }

            if (count != 0)
            {
                WriteRleRun(pixels, palette, ref x, y, count, command, fourBit);
                continue;
            }

            switch (command)
            {
                case 0:
                    x = 0;
                    y--;
                    break;
                case 1:
                    ended = true;
                    break;
                case 2:
                    BinaryPrimitivesEx.Ensure(data, offset, 2);
                    x = checked(x + data[offset++]);
                    y = checked(y - data[offset++]);
                    // Encoders commonly move exactly one row past the bottom before EOB.
                    // Permit only that sentinel position; the next command must be EOB.
                    if (x > pixels.Width || y < -1)
                    {
                        ThrowHelper.InvalidData("A BMP RLE delta leaves the image bounds.");
                    }

                    break;
                default:
                    int literalCount = command;
                    int byteCount = fourBit ? (literalCount + 1) / 2 : literalCount;
                    BinaryPrimitivesEx.Ensure(data, offset, byteCount);
                    for (int i = 0; i < literalCount; i++)
                    {
                        int index = fourBit ? ((i & 1) == 0 ? data[offset + (i >> 1)] >> 4 : data[offset + (i >> 1)] & 15) : data[offset + i];
                        WriteRlePixel(pixels, palette, ref x, y, index);
                    }

                    offset += byteCount;
                    if ((byteCount & 1) != 0)
                    {
                        BinaryPrimitivesEx.Ensure(data, offset, 1);
                        offset++;
                    }

                    break;
            }
        }

        if (!ended)
        {
            ThrowHelper.InvalidData("The BMP RLE stream has no end marker.");
        }
    }

    /// <summary>Writes a palette entry and advances the RLE cursor.</summary>
    private static void WriteRlePixel(PixelBuffer pixels, byte[] palette, ref int x, int y, int index)
    {
        if ((uint)x >= (uint)pixels.Width || (uint)y >= (uint)pixels.Height)
        {
            ThrowHelper.InvalidData("BMP RLE data exceeds the image bounds.");
        }

        WritePalette(pixels.GetRowSpan(y), x * 4, palette, index);
        x++;
    }

    /// <summary>Bulk-writes one encoded RLE run after a single bounds check.</summary>
    private static void WriteRleRun(PixelBuffer pixels, byte[] palette, ref int x, int y, int count, int command, bool fourBit)
    {
        if ((uint)y >= (uint)pixels.Height || x < 0 || count > pixels.Width - x)
        {
            ThrowHelper.InvalidData("BMP RLE data exceeds the image bounds.");
        }

        Span<uint> target = MemoryMarshal.Cast<byte, uint>(pixels.GetRowSpan(y)).Slice(x, count);
        uint first = ReadPalette(palette, fourBit ? command >> 4 : command);
        if (!fourBit)
        {
            target.Fill(first);
        }
        else
        {
            uint second = ReadPalette(palette, command & 15);
            int index = 0;
            for (; index + 1 < target.Length; index += 2)
            {
                target[index] = first;
                target[index + 1] = second;
            }

            if (index < target.Length) target[index] = first;
        }

        x += count;
    }

    /// <summary>Copies one palette color to RGBA output.</summary>
    private static void WritePalette(Span<byte> target, int output, byte[] palette, int index)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target[output..], ReadPalette(palette, index));
    }

    /// <summary>Returns one packed RGBA palette value for direct lookup and bulk stores.</summary>
    private static uint ReadPalette(byte[] palette, int index)
    {
        int paletteOffset = checked(index * 4);
        if (paletteOffset + 3 >= palette.Length)
        {
            ThrowHelper.InvalidData("A BMP palette index is out of range.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(palette.AsSpan(paletteOffset, 4));
    }

    /// <summary>Extracts normalized channels from a BITFIELDS value.</summary>
    private static void WriteMasked(Span<byte> target, int output, uint value, BitfieldInfo fields)
    {
        target[output] = fields.Red.Extract(value);
        target[output + 1] = fields.Green.Extract(value);
        target[output + 2] = fields.Blue.Extract(value);
        target[output + 3] = fields.HasAlpha ? fields.Alpha.Extract(value) : (byte)255;
    }

    /// <summary>Tests whether channel masks overlap.</summary>
    private static bool MasksOverlap(uint red, uint green, uint blue, uint alpha) =>
        (red & green) != 0 || (red & blue) != 0 || (green & blue) != 0 ||
        (alpha != 0 && ((alpha & red) != 0 || (alpha & green) != 0 || (alpha & blue) != 0));

    /// <summary>Returns whether all decoded alpha bytes are fully opaque.</summary>
    private static bool PixelsAreOpaque(PixelBuffer pixels)
    {
        ReadOnlySpan<byte> rgba = pixels.Span;
        for (int offset = 3; offset < rgba.Length; offset += 4)
        {
            if (rgba[offset] != 255) return false;
        }

        return true;
    }

    /// <summary>Precomputes validated channel-mask data once per image.</summary>
    private readonly struct BitfieldInfo
    {
        public BitfieldInfo(uint red, uint green, uint blue, uint alpha)
        {
            if (red == 0 || green == 0 || blue == 0 || MasksOverlap(red, green, blue, alpha))
            {
                ThrowHelper.InvalidData("BMP bitfield masks are invalid.");
            }

            Red = new ChannelMask(red);
            Green = new ChannelMask(green);
            Blue = new ChannelMask(blue);
            Alpha = alpha == 0 ? default : new ChannelMask(alpha);
            HasAlpha = alpha != 0;
        }

        public ChannelMask Red { get; }
        public ChannelMask Green { get; }
        public ChannelMask Blue { get; }
        public ChannelMask Alpha { get; }
        public bool HasAlpha { get; }
    }

    /// <summary>Normalizes one validated contiguous bitfield to eight bits.</summary>
    private readonly struct ChannelMask
    {
        private readonly uint mask;
        private readonly int shift;
        private readonly int bits;

        public ChannelMask(uint mask)
        {
            this.mask = mask;
            shift = BitOperations.TrailingZeroCount(mask);
            uint shiftedMask = mask >> shift;
            if ((shiftedMask & (shiftedMask + 1)) != 0)
            {
                ThrowHelper.InvalidData("BMP channel masks must be contiguous.");
            }

            bits = BitOperations.PopCount(shiftedMask);
        }

        public byte Extract(uint value)
        {
            uint channel = (value & mask) >> shift;
            if (bits >= 8) return (byte)(channel >> (bits - 8));

            // Windows DIB expands narrow channels by repeating their most-significant
            // bits (5-bit 00011 becomes 00011000), rather than ratio rounding.
            uint expanded = 0;
            int filled = 0;
            while (filled < 8)
            {
                int take = Math.Min(bits, 8 - filled);
                expanded |= (channel >> (bits - take)) << (8 - filled - take);
                filled += take;
            }

            return (byte)expanded;
        }
    }
}
