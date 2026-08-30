using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.WebP;

/// <summary>Decodes the complete static VP8L lossless bitstream.</summary>
internal static unsafe class Vp8LosslessDecoder
{
    private static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // Pairs are (horizontal scan displacement, rows above). The encoded distance is
    // horizontal + rowsAbove * width and is subtracted from the current scan index.
    private static ReadOnlySpan<sbyte> DistanceMap =>
    [
         0,1,  1,0,  1,1, -1,1,  0,2,  2,0,  1,2, -1,2,  2,1, -2,1,  2,2, -2,2,
         0,3,  3,0,  1,3, -1,3,  3,1, -3,1,  2,3, -2,3,  3,2, -3,2,  0,4,  4,0,
         1,4, -1,4,  4,1, -4,1,  3,3, -3,3,  2,4, -2,4,  4,2, -4,2,  0,5,  3,4,
        -3,4,  4,3, -4,3,  5,0,  1,5, -1,5,  5,1, -5,1,  2,5, -2,5,  5,2, -5,2,
         4,4, -4,4,  3,5, -3,5,  5,3, -5,3,  0,6,  6,0,  1,6, -1,6,  6,1, -6,1,
         2,6, -2,6,  6,2, -6,2,  4,5, -4,5,  5,4, -5,4,  3,6, -3,6,  6,3, -6,3,
         0,7,  7,0,  1,7, -1,7,  5,5, -5,5,  7,1, -7,1,  4,6, -4,6,  6,4, -6,4,
         2,7, -2,7,  7,2, -7,2,  3,7, -3,7,  7,3, -7,3,  5,6, -5,6,  6,5, -6,5,
         8,0,  4,7, -4,7,  7,4, -7,4,  8,1,  8,2,  6,6, -6,6,  8,3,  5,7, -5,7,
         7,5, -7,5,  8,4,  6,7, -6,7,  7,6, -7,6,  8,5,  7,7, -7,7,  8,6,  8,7,
    ];

    /// <summary>Decodes a VP8L payload into RGBA32.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> payload, long maximumPixels, int canvasWidth, int canvasHeight)
    {
        if (payload.Length < 5 || payload[0] != 0x2F)
        {
            ThrowHelper.InvalidData("The VP8L signature is invalid.");
        }

        Vp8lBitReader reader = new(payload[1..]);
        int width = reader.ReadBits(14) + 1;
        int height = reader.ReadBits(14) + 1;
        bool alphaUsed = reader.ReadBits(1) != 0;
        if (reader.ReadBits(3) != 0)
        {
            ThrowHelper.Unsupported("The VP8L version is unsupported.");
        }

        if ((canvasWidth != 0 && canvasWidth != width) || (canvasHeight != 0 && canvasHeight != height))
        {
            ThrowHelper.InvalidData("The WebP canvas and VP8L dimensions disagree.");
        }

        ThrowHelper.ValidateDimensions(width, height, maximumPixels);
        using Vp8lImage decoded = DecodeTransformed(ref reader, width, height);
        PixelBuffer pixels = ToPixelBuffer(decoded, alphaUsed, out bool isOpaque);
        return new DecodedImage(pixels, ExifOrientation.Normal, isOpaque: isOpaque);
    }

    /// <summary>Decodes the headerless VP8L image stream used by a compressed ALPH chunk.</summary>
    public static byte[] DecodeAlpha(ReadOnlySpan<byte> payload, int width, int height)
    {
        Vp8lBitReader reader = new(payload);
        using Vp8lImage decoded = DecodeTransformed(ref reader, width, height);
        byte[] alpha = GC.AllocateUninitializedArray<byte>(checked(width * height));
        ReadOnlySpan<uint> pixels = decoded.Span;
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = (byte)(pixels[i] >> 8);
        }

        return alpha;
    }

    /// <summary>Decodes a VP8L image stream whose dimensions were supplied by its container.</summary>
    private static Vp8lImage DecodeTransformed(ref Vp8lBitReader reader, int width, int height)
    {
        int codedWidth = width;
        List<Vp8lTransform> transforms = [];
        int seenTransforms = 0;
        Vp8lImage? current = null;
        try
        {
            while (reader.ReadBits(1) != 0)
            {
                int type = reader.ReadBits(2);
                int flag = 1 << type;
                if ((seenTransforms & flag) != 0)
                {
                    ThrowHelper.InvalidData("A VP8L transform is declared more than once.");
                }

                seenTransforms |= flag;
                switch (type)
                {
                    case 0:
                    case 1:
                        int sizeBits = reader.ReadBits(3) + 2;
                        int transformWidth = DivideRoundUp(codedWidth, 1 << sizeBits);
                        int transformHeight = DivideRoundUp(height, 1 << sizeBits);
                        Vp8lImage transformImage = DecodeImage(ref reader, transformWidth, transformHeight, allowMetaCodes: false);
                        transforms.Add(new Vp8lTransform(type, codedWidth, height, sizeBits, transformImage, 0));
                        break;
                    case 2:
                        transforms.Add(new Vp8lTransform(type, codedWidth, height, 0, null, 0));
                        break;
                    case 3:
                        int colorCount = reader.ReadBits(8) + 1;
                        Vp8lImage palette = DecodeImage(ref reader, colorCount, 1, allowMetaCodes: false);
                        ExpandPaletteDeltas(palette);
                        int widthBits = colorCount <= 2 ? 3 : colorCount <= 4 ? 2 : colorCount <= 16 ? 1 : 0;
                        transforms.Add(new Vp8lTransform(type, codedWidth, height, widthBits, palette, colorCount));
                        codedWidth = DivideRoundUp(codedWidth, 1 << widthBits);
                        break;
                }
            }

            using Vp8lImage coded = DecodeImage(ref reader, codedWidth, height, allowMetaCodes: true);
            current = coded.DetachAlias();
            for (int i = transforms.Count - 1; i >= 0; i--)
            {
                Vp8lTransform transform = transforms[i];
                Vp8lImage next = transform.Type switch
                {
                    0 => ApplyPredictor(current, transform),
                    1 => ApplyColor(current, transform),
                    2 => ApplySubtractGreen(current),
                    3 => ApplyColorIndexing(current, transform),
                    _ => throw new InvalidOperationException(),
                };
                if (!ReferenceEquals(next, current))
                {
                    current.Dispose();
                }

                current = next;
            }

            if (current.Width != width || current.Height != height)
            {
                ThrowHelper.InvalidData("VP8L transforms produced inconsistent dimensions.");
            }

            Vp8lImage result = current;
            current = null;
            return result;
        }
        finally
        {
            current?.Dispose();
            foreach (Vp8lTransform transform in transforms)
            {
                transform.Image?.Dispose();
            }
        }
    }

    /// <summary>Decodes one transform/data image including Huffman groups and LZ77 copies.</summary>
    private static Vp8lImage DecodeImage(ref Vp8lBitReader reader, int width, int height, bool allowMetaCodes)
    {
        int colorCacheBits = 0;
        if (reader.ReadBits(1) != 0)
        {
            colorCacheBits = reader.ReadBits(4);
            if (colorCacheBits is < 1 or > 11)
            {
                ThrowHelper.InvalidData("The VP8L color-cache size is invalid.");
            }
        }

        int colorCacheSize = colorCacheBits == 0 ? 0 : 1 << colorCacheBits;
        uint[]? entropyImage = null;
        int prefixBits = 0;
        int prefixWidth = 0;
        int groupCount = 1;

        if (allowMetaCodes && reader.ReadBits(1) != 0)
        {
            prefixBits = reader.ReadBits(3) + 2;
            prefixWidth = DivideRoundUp(width, 1 << prefixBits);
            int prefixHeight = DivideRoundUp(height, 1 << prefixBits);
            using Vp8lImage entropy = DecodeImage(ref reader, prefixWidth, prefixHeight, allowMetaCodes: false);
            entropyImage = entropy.Span.ToArray();
            int maximum = 0;
            foreach (uint pixel in entropyImage)
            {
                maximum = Math.Max(maximum, (int)((pixel >> 8) & 0xFFFF));
            }

            groupCount = checked(maximum + 1);
            if (groupCount > width * (long)height)
            {
                ThrowHelper.InvalidData("The VP8L entropy image references too many Huffman groups.");
            }
        }

        Vp8lHuffmanGroup[] groups = new Vp8lHuffmanGroup[groupCount];
        for (int i = 0; i < groups.Length; i++)
        {
            groups[i] = new Vp8lHuffmanGroup(
                ReadHuffman(ref reader, 256 + 24 + colorCacheSize),
                ReadHuffman(ref reader, 256),
                ReadHuffman(ref reader, 256),
                ReadHuffman(ref reader, 256),
                ReadHuffman(ref reader, 40));
        }

        uint[]? colorCache = colorCacheSize == 0 ? null : new uint[colorCacheSize];
        Vp8lImage image = new(width, height);
        Span<uint> pixels = image.Span;
        try
        {
            if (entropyImage is null)
            {
                DecodeSingleGroup(ref reader, pixels, groups[0], colorCache, colorCacheBits, width);
            }
            else
            {
                DecodeMetaGroups(ref reader, pixels, groups, entropyImage, prefixBits, prefixWidth, colorCache, colorCacheBits, width);
            }
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>Decodes the common single-Huffman-group stream without coordinate division.</summary>
    private static void DecodeSingleGroup(ref Vp8lBitReader reader, Span<uint> pixels, Vp8lHuffmanGroup group, uint[]? colorCache, int colorCacheBits, int width)
    {
        int position = 0;
        while (position < pixels.Length)
        {
            int symbol = group.Green.Decode(ref reader);
            if (symbol < 256)
            {
                int red = group.Red.Decode(ref reader);
                int blue = group.Blue.Decode(ref reader);
                int alpha = group.Alpha.Decode(ref reader);
                uint color = (uint)((alpha << 24) | (red << 16) | (symbol << 8) | blue);
                pixels[position++] = color;
                UpdateCache(colorCache, colorCacheBits, color);
            }
            else if (symbol < 280)
            {
                int length = ReadPrefixValue(ref reader, symbol - 256);
                int distanceCode = ReadPrefixValue(ref reader, group.Distance.Decode(ref reader));
                int distance = MapDistance(distanceCode, width);
                if (distance > position || length > pixels.Length - position)
                {
                    ThrowHelper.InvalidData("A VP8L backward reference leaves the decoded image bounds.");
                }

                if (colorCache is null)
                {
                    CopyLzRun(pixels, ref position, distance, length);
                }
                else
                {
                    for (int i = 0; i < length; i++)
                    {
                        uint color = pixels[position - distance];
                        pixels[position++] = color;
                        UpdateCache(colorCache, colorCacheBits, color);
                    }
                }
            }
            else
            {
                int cacheIndex = symbol - 280;
                if (colorCache is null || (uint)cacheIndex >= (uint)colorCache.Length)
                {
                    ThrowHelper.InvalidData("A VP8L color-cache index is invalid.");
                }

                uint color = colorCache[cacheIndex];
                pixels[position++] = color;
                UpdateCache(colorCache, colorCacheBits, color);
            }
        }
    }

    /// <summary>Decodes spatial Huffman groups while maintaining row coordinates incrementally.</summary>
    private static void DecodeMetaGroups(ref Vp8lBitReader reader, Span<uint> pixels, Vp8lHuffmanGroup[] groups, uint[] entropyImage, int prefixBits, int prefixWidth, uint[]? colorCache, int colorCacheBits, int width)
    {
        int position = 0;
        int y = 0;
        int rowStart = 0;
        int nextRow = width;
        while (position < pixels.Length)
        {
            while (position >= nextRow)
            {
                y++;
                rowStart = nextRow;
                nextRow += width;
            }

            int x = position - rowStart;
            int groupIndex = (int)((entropyImage[(y >> prefixBits) * prefixWidth + (x >> prefixBits)] >> 8) & 0xFFFF);
            if ((uint)groupIndex >= (uint)groups.Length)
            {
                ThrowHelper.InvalidData("A VP8L entropy group index is out of range.");
            }

            Vp8lHuffmanGroup group = groups[groupIndex];
                int symbol = group.Green.Decode(ref reader);
                if (symbol < 256)
                {
                    int red = group.Red.Decode(ref reader);
                    int blue = group.Blue.Decode(ref reader);
                    int alpha = group.Alpha.Decode(ref reader);
                    uint color = (uint)((alpha << 24) | (red << 16) | (symbol << 8) | blue);
                    pixels[position++] = color;
                    UpdateCache(colorCache, colorCacheBits, color);
                }
                else if (symbol < 280)
                {
                    int length = ReadPrefixValue(ref reader, symbol - 256);
                    int distanceCode = ReadPrefixValue(ref reader, group.Distance.Decode(ref reader));
                    int distance = MapDistance(distanceCode, width);
                    if (distance > position || length > pixels.Length - position)
                    {
                        ThrowHelper.InvalidData("A VP8L backward reference leaves the decoded image bounds.");
                    }

                    if (colorCache is null)
                    {
                        CopyLzRun(pixels, ref position, distance, length);
                    }
                    else
                    {
                        for (int i = 0; i < length; i++)
                        {
                            uint color = pixels[position - distance];
                            pixels[position++] = color;
                            UpdateCache(colorCache, colorCacheBits, color);
                        }
                    }
                }
                else
                {
                    int cacheIndex = symbol - 280;
                    if (colorCache is null || (uint)cacheIndex >= (uint)colorCache.Length)
                    {
                        ThrowHelper.InvalidData("A VP8L color-cache index is invalid.");
                    }

                    uint color = colorCache[cacheIndex];
                    pixels[position++] = color;
                    UpdateCache(colorCache, colorCacheBits, color);
                }
            }
        }

    /// <summary>Reads a simple or normal VP8L canonical Huffman tree.</summary>
    private static Vp8lHuffman ReadHuffman(ref Vp8lBitReader reader, int alphabetSize)
    {
        byte[] lengths = new byte[alphabetSize];
        if (reader.ReadBits(1) != 0)
        {
            int symbolCount = reader.ReadBits(1) + 1;
            int first = reader.ReadBits(reader.ReadBits(1) == 0 ? 1 : 8);
            if ((uint)first >= (uint)alphabetSize)
            {
                ThrowHelper.InvalidData("A VP8L simple Huffman symbol is out of range.");
            }

            lengths[first] = 1;
            if (symbolCount == 2)
            {
                int second = reader.ReadBits(8);
                if ((uint)second >= (uint)alphabetSize)
                {
                    ThrowHelper.InvalidData("A VP8L simple Huffman symbol is out of range.");
                }

                lengths[second] = 1;
            }

            return new Vp8lHuffman(lengths);
        }

        int codeLengthCount = reader.ReadBits(4) + 4;
        byte[] codeLengthLengths = new byte[19];
        for (int i = 0; i < codeLengthCount; i++)
        {
            codeLengthLengths[CodeLengthOrder[i]] = (byte)reader.ReadBits(3);
        }

        Vp8lHuffman codeLengthTree = new(codeLengthLengths);
        int maximumSymbols = alphabetSize;
        if (reader.ReadBits(1) != 0)
        {
            int lengthBits = 2 + 2 * reader.ReadBits(3);
            maximumSymbols = 2 + reader.ReadBits(lengthBits);
            if (maximumSymbols > alphabetSize)
            {
                ThrowHelper.InvalidData("A VP8L Huffman alphabet exceeds its declared size.");
            }
        }

        int position = 0;
        int previous = 8;
        int remainingCodeSymbols = maximumSymbols;
        while (position < alphabetSize && remainingCodeSymbols-- > 0)
        {
            int value = codeLengthTree.Decode(ref reader);
            if (value <= 15)
            {
                lengths[position++] = (byte)value;
                if (value != 0)
                {
                    previous = value;
                }
            }
            else
            {
                int repeat;
                int repeatedValue;
                if (value == 16)
                {
                    repeat = 3 + reader.ReadBits(2);
                    repeatedValue = previous;
                }
                else if (value == 17)
                {
                    repeat = 3 + reader.ReadBits(3);
                    repeatedValue = 0;
                }
                else if (value == 18)
                {
                    repeat = 11 + reader.ReadBits(7);
                    repeatedValue = 0;
                }
                else
                {
                    ThrowHelper.InvalidData("A VP8L code-length symbol is invalid.");
                    return null!;
                }

                // A repeat may cross the optional max_symbol loop boundary. Its complete
                // run is still part of the tree as long as it fits the real alphabet.
                if (repeat > alphabetSize - position)
                {
                    ThrowHelper.InvalidData("A VP8L code-length repeat exceeds the alphabet.");
                }

                lengths.AsSpan(position, repeat).Fill((byte)repeatedValue);
                position += repeat;
            }
        }

        return new Vp8lHuffman(lengths);
    }

    /// <summary>Decodes the VP8L exponential prefix integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadPrefixValue(ref Vp8lBitReader reader, int prefix)
    {
        if (prefix < 4)
        {
            return prefix + 1;
        }

        int extraBits = (prefix - 2) >> 1;
        int offset = (2 + (prefix & 1)) << extraBits;
        return checked(offset + reader.ReadBits(extraBits) + 1);
    }

    /// <summary>Maps a two-dimensional locality code to a scan-order distance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MapDistance(int code, int width)
    {
        if (code <= 0)
        {
            ThrowHelper.InvalidData("A VP8L distance code is invalid.");
        }

        if (code > 120)
        {
            return code - 120;
        }

        int index = (code - 1) * 2;
        return Math.Max(1, DistanceMap[index] + DistanceMap[index + 1] * width);
    }

    /// <summary>Updates the hash-addressed color cache.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateCache(uint[]? cache, int bits, uint color)
    {
        if (cache is not null)
        {
            cache[(uint)(0x1E35A7BDu * color) >> (32 - bits)] = color;
        }
    }

    /// <summary>Copies an overlap-safe LZ run in exponentially growing blocks.</summary>
    private static void CopyLzRun(Span<uint> pixels, ref int position, int distance, int length)
    {
        if (distance == 1)
        {
            pixels.Slice(position, length).Fill(pixels[position - 1]);
            position += length;
            return;
        }

        int copied = Math.Min(distance, length);
        pixels.Slice(position - distance, copied).CopyTo(pixels.Slice(position, copied));
        position += copied;
        while (copied < length)
        {
            int chunk = Math.Min(copied, length - copied);
            pixels.Slice(position - copied, chunk).CopyTo(pixels.Slice(position, chunk));
            position += chunk;
            copied += chunk;
        }
    }

    /// <summary>Converts delta-coded palette entries to cumulative colors.</summary>
    private static void ExpandPaletteDeltas(Vp8lImage palette)
    {
        Span<uint> colors = palette.Span;
        uint previous = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            previous = AddChannels(previous, colors[i]);
            colors[i] = previous;
        }
    }

    /// <summary>Applies the inverse spatial predictor in scan order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vp8lImage ApplyPredictor(Vp8lImage image, Vp8lTransform transform)
    {
        Span<uint> pixels = image.Span;
        ReadOnlySpan<uint> modes = transform.Image!.Span;
        int modeWidth = transform.Image.Width;
        int width = image.Width;
        pixels[0] = AddChannels(pixels[0], 0xFF000000);
        for (int x = 1; x < width; x++)
        {
            pixels[x] = AddChannels(pixels[x], pixels[x - 1]);
        }

        int blockSize = 1 << transform.Bits;
        for (int y = 1; y < image.Height; y++)
        {
            int row = y * width;
            pixels[row] = AddChannels(pixels[row], pixels[row - width]);
            int modeRow = (y >> transform.Bits) * modeWidth;
            int x = 1;
            while (x < width)
            {
                int mode = (int)((modes[modeRow + (x >> transform.Bits)] >> 8) & 0xFF);
                int blockEnd = Math.Min(width, ((x >> transform.Bits) + 1) * blockSize);
                ApplyPredictorBlock(pixels, row, width, x, blockEnd, mode);
                x = blockEnd;
            }
        }

        return image;
    }

    /// <summary>Applies one predictor mode to a complete transform block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ApplyPredictorBlock(Span<uint> pixels, int row, int width, int start, int end, int mode)
    {
        switch (mode)
        {
            case 0:
                AddConstantRange(pixels, row + start, end - start, 0xFF000000u);
                return;
            case 1:
                for (int x = start; x < end; x++)
                {
                    int position = row + x;
                    pixels[position] = AddChannels(pixels[position], pixels[position - 1]);
                }
                return;
            case 2:
                AddCopiedRange(pixels, row + start, row - width + start, end - start);
                return;
            case 3:
                int copiedEnd = Math.Min(end, width - 1);
                AddCopiedRange(pixels, row + start, row - width + start + 1, copiedEnd - start);
                if (end == width)
                {
                    int position = row + width - 1;
                    pixels[position] = AddChannels(pixels[position], pixels[row]);
                }
                return;
            case 4:
                AddCopiedRange(pixels, row + start, row - width + start - 1, end - start);
                return;
            case 11:
                if (Sse2.IsSupported && SimdPrimitives.AllowPortableVector)
                {
                    for (int x = start; x < end; x++)
                    {
                        int position = row + x;
                        uint left = pixels[position - 1];
                        uint top = pixels[position - width];
                        uint topLeft = pixels[position - width - 1];
                        pixels[position] = AddChannels(pixels[position], SelectSse2(left, top, topLeft));
                    }
                }
                else
                {
                    for (int x = start; x < end; x++)
                    {
                        int position = row + x;
                        uint left = pixels[position - 1];
                        uint top = pixels[position - width];
                        uint topLeft = pixels[position - width - 1];
                        pixels[position] = AddChannels(pixels[position], Select(left, top, topLeft));
                    }
                }
                return;
        }

        if ((uint)(mode - 5) > 8)
        {
            ThrowHelper.InvalidData("A VP8L predictor mode is invalid.");
        }

        for (int x = start; x < end; x++)
        {
            int position = row + x;
            uint left = pixels[position - 1];
            uint top = pixels[position - width];
            uint topLeft = pixels[position - width - 1];
            uint topRight = x == width - 1 ? pixels[row] : pixels[position - width + 1];
            uint prediction = mode switch
            {
                5 => Average(Average(left, topRight), top),
                6 => Average(left, topLeft),
                7 => Average(left, top),
                8 => Average(topLeft, top),
                9 => Average(top, topRight),
                10 => Average(Average(left, topLeft), Average(top, topRight)),
                11 => Select(left, top, topLeft),
                12 => ClampAddSubtract(left, top, topLeft),
                _ => ClampHalf(Average(left, top), topLeft),
            };
            pixels[position] = AddChannels(pixels[position], prediction);
        }
    }

    /// <summary>Adds a constant byte vector to a contiguous pixel range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddConstantRange(Span<uint> pixels, int destination, int count, uint value)
    {
        int i = 0;
        ref uint target = ref pixels[destination];
        if (SimdPrimitives.AllowAvx2)
        {
            Vector256<byte> add = Vector256.Create(value).AsByte();
            for (; i + 8 <= count; i += 8)
            {
                Vector256<byte> residual = Vector256.LoadUnsafe(ref target, (nuint)i).AsByte();
                Avx2.Add(residual, add).AsUInt32().StoreUnsafe(ref target, (nuint)i);
            }
        }

        for (; i < count; i++) pixels[destination + i] = AddChannels(pixels[destination + i], value);
    }

    /// <summary>Adds corresponding predictor pixels to a contiguous residual range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddCopiedRange(Span<uint> pixels, int destination, int source, int count)
    {
        if (count <= 0) return;
        int i = 0;
        ref uint target = ref pixels[destination];
        ref uint predictor = ref pixels[source];
        if (SimdPrimitives.AllowAvx2)
        {
            for (; i + 8 <= count; i += 8)
            {
                Vector256<byte> residual = Vector256.LoadUnsafe(ref target, (nuint)i).AsByte();
                Vector256<byte> predicted = Vector256.LoadUnsafe(ref predictor, (nuint)i).AsByte();
                Avx2.Add(residual, predicted).AsUInt32().StoreUnsafe(ref target, (nuint)i);
            }
        }

        for (; i < count; i++) pixels[destination + i] = AddChannels(pixels[destination + i], pixels[source + i]);
    }

    /// <summary>Applies inverse color correlation to red and blue.</summary>
    private static Vp8lImage ApplyColor(Vp8lImage image, Vp8lTransform transform)
    {
        Vp8lImage transformImage = transform.Image!;
        int coefficientWidth = transformImage.Width;
        bool parallel = ParallelExecution.ShouldRun(image.Length, 1_000_000);
        ParallelExecution.For(0, image.Height, parallel, y =>
        {
            Span<uint> row = image.Span.Slice(y * image.Width, image.Width);
            ReadOnlySpan<uint> coefficients = transformImage.Span;
            int coefficientRow = (y >> transform.Bits) * coefficientWidth;
            int blockSize = 1 << transform.Bits;
            for (int blockX = 0; blockX < image.Width; blockX += blockSize)
            {
                uint transformColor = coefficients[coefficientRow + (blockX >> transform.Bits)];
                int greenToRed = unchecked((sbyte)transformColor);
                int greenToBlue = unchecked((sbyte)(transformColor >> 8));
                int redToBlue = unchecked((sbyte)(transformColor >> 16));
                int blockEnd = Math.Min(image.Width, blockX + blockSize);
                for (int x = blockX; x < blockEnd; x++)
                {
                    uint color = row[x];
                    int green = (byte)(color >> 8);
                    int red = ((byte)(color >> 16) + ColorDelta(greenToRed, green)) & 255;
                    int blue = ((byte)color + ColorDelta(greenToBlue, green) + ColorDelta(redToBlue, red)) & 255;
                    row[x] = (color & 0xFF00FF00u) | ((uint)red << 16) | (uint)blue;
                }
            }
        });

        return image;
    }

    /// <summary>Adds green into red and blue modulo 256.</summary>
    private static Vp8lImage ApplySubtractGreen(Vp8lImage image)
    {
        Span<uint> pixels = image.Span;
        Span<byte> bytes = MemoryMarshal.AsBytes(pixels);
        ref byte start = ref MemoryMarshal.GetReference(bytes);
        int byteOffset = 0;
        Vector128<byte> shuffle128 = Vector128.Create(
            (byte)1, 0x80, 1, 0x80, 5, 0x80, 5, 0x80,
            9, 0x80, 9, 0x80, 13, 0x80, 13, 0x80);
        if (SimdPrimitives.AllowAvx2)
        {
            Vector256<byte> shuffle256 = Vector256.Create(shuffle128, shuffle128);
            for (; byteOffset + 32 <= bytes.Length; byteOffset += 32)
            {
                Vector256<byte> value = Vector256.LoadUnsafe(ref start, (nuint)byteOffset);
                Avx2.Add(value, Avx2.Shuffle(value, shuffle256)).StoreUnsafe(ref start, (nuint)byteOffset);
            }
        }

        if (SimdPrimitives.AllowSsse3)
        {
            for (; byteOffset + 16 <= bytes.Length; byteOffset += 16)
            {
                Vector128<byte> value = Vector128.LoadUnsafe(ref start, (nuint)byteOffset);
                Sse2.Add(value, Ssse3.Shuffle(value, shuffle128)).StoreUnsafe(ref start, (nuint)byteOffset);
            }
        }

        for (int i = byteOffset >> 2; i < pixels.Length; i++)
        {
            uint color = pixels[i];
            uint green = (color >> 8) & 255;
            uint redBlue = ((color & 0x00FF00FFu) + green * 0x00010001u) & 0x00FF00FFu;
            pixels[i] = (color & 0xFF00FF00u) | redBlue;
        }

        return image;
    }

    /// <summary>Expands packed palette indices to the pre-transform width.</summary>
    private static Vp8lImage ApplyColorIndexing(Vp8lImage image, Vp8lTransform transform)
    {
        Vp8lImage expanded = new(transform.Width, transform.Height);
        try
        {
            ReadOnlySpan<uint> source = image.Span;
            ReadOnlySpan<uint> palette = transform.Image!.Span;
            Span<uint> destination = expanded.Span;
            int pixelsPerPacked = 1 << transform.Bits;
            int bitsPerIndex = 8 >> transform.Bits;
            int mask = (1 << bitsPerIndex) - 1;
            for (int y = 0; y < transform.Height; y++)
            {
                for (int x = 0; x < transform.Width; x++)
                {
                    uint packed = source[y * image.Width + (x >> transform.Bits)];
                    int shift = (x & (pixelsPerPacked - 1)) * bitsPerIndex;
                    int index = ((byte)(packed >> 8) >> shift) & mask;
                    destination[y * transform.Width + x] = index < transform.ColorCount ? palette[index] : 0;
                }
            }

            return expanded;
        }
        catch
        {
            expanded.Dispose();
            throw;
        }
    }

    /// <summary>Adds four byte channels independently modulo 256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddChannels(uint left, uint right)
    {
        const uint EvenBytes = 0x00FF00FF;
        uint even = (left & EvenBytes) + (right & EvenBytes);
        uint odd = ((left >> 8) & EvenBytes) + ((right >> 8) & EvenBytes);
        return (even & EvenBytes) | ((odd & EvenBytes) << 8);
    }

    /// <summary>Averages four byte channels without cross-channel carries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average(uint left, uint right) => (left & right) + (((left ^ right) & 0xFEFEFEFEu) >> 1);

    /// <summary>Selects the neighboring color closest to a gradient estimate.</summary>
    private static uint Select(uint left, uint top, uint topLeft)
    {
        int leftDistance = Math.Abs((byte)top - (byte)topLeft)
            + Math.Abs((byte)(top >> 8) - (byte)(topLeft >> 8))
            + Math.Abs((byte)(top >> 16) - (byte)(topLeft >> 16))
            + Math.Abs((byte)(top >> 24) - (byte)(topLeft >> 24));
        int topDistance = Math.Abs((byte)left - (byte)topLeft)
            + Math.Abs((byte)(left >> 8) - (byte)(topLeft >> 8))
            + Math.Abs((byte)(left >> 16) - (byte)(topLeft >> 16))
            + Math.Abs((byte)(left >> 24) - (byte)(topLeft >> 24));

        return leftDistance < topDistance ? left : top;
    }

    /// <summary>Selects the closest predictor using the exact four-byte SAD operation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SelectSse2(uint left, uint top, uint topLeft)
    {
        Vector128<byte> reference = Vector128.CreateScalar(topLeft).AsByte();
        int leftDistance = Sse2.SumAbsoluteDifferences(Vector128.CreateScalar(top).AsByte(), reference).GetElement(0);
        int topDistance = Sse2.SumAbsoluteDifferences(Vector128.CreateScalar(left).AsByte(), reference).GetElement(0);
        return leftDistance < topDistance ? left : top;
    }

    /// <summary>Clamps L + T - TL independently per channel.</summary>
    private static uint ClampAddSubtract(uint left, uint top, uint topLeft)
    {
        uint c0 = (uint)Math.Clamp((byte)left + (byte)top - (byte)topLeft, 0, 255);
        uint c1 = (uint)Math.Clamp((byte)(left >> 8) + (byte)(top >> 8) - (byte)(topLeft >> 8), 0, 255);
        uint c2 = (uint)Math.Clamp((byte)(left >> 16) + (byte)(top >> 16) - (byte)(topLeft >> 16), 0, 255);
        uint c3 = (uint)Math.Clamp((byte)(left >> 24) + (byte)(top >> 24) - (byte)(topLeft >> 24), 0, 255);
        return c0 | (c1 << 8) | (c2 << 16) | (c3 << 24);
    }

    /// <summary>Clamps average + half its difference from top-left per channel.</summary>
    private static uint ClampHalf(uint average, uint topLeft)
    {
        int a0 = (byte)average;
        int a1 = (byte)(average >> 8);
        int a2 = (byte)(average >> 16);
        int a3 = (byte)(average >> 24);
        uint c0 = (uint)Math.Clamp(a0 + (a0 - (byte)topLeft) / 2, 0, 255);
        uint c1 = (uint)Math.Clamp(a1 + (a1 - (byte)(topLeft >> 8)) / 2, 0, 255);
        uint c2 = (uint)Math.Clamp(a2 + (a2 - (byte)(topLeft >> 16)) / 2, 0, 255);
        uint c3 = (uint)Math.Clamp(a3 + (a3 - (byte)(topLeft >> 24)) / 2, 0, 255);
        return c0 | (c1 << 8) | (c2 << 16) | (c3 << 24);
    }

    /// <summary>Applies one signed 3.5 fixed-point color coefficient.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ColorDelta(int coefficient, int channel) => (coefficient * unchecked((sbyte)channel)) >> 5;

    /// <summary>Copies native ARGB words into RGB24 when opaque, otherwise RGBA32.</summary>
    private static PixelBuffer ToPixelBuffer(Vp8lImage image, bool alphaUsed, out bool isOpaque)
    {
        ReadOnlySpan<uint> input = image.Span;
        isOpaque = !alphaUsed || IsOpaque(input);
        return isOpaque ? ToRgb(image) : ToRgba(image);
    }

    /// <summary>Determines whether every decoded alpha byte is fully opaque.</summary>
    private static bool IsOpaque(ReadOnlySpan<uint> input)
    {
        int i = 0;
        ref uint source = ref MemoryMarshal.GetReference(input);
        if (SimdPrimitives.AllowAvx2)
        {
            Vector256<uint> alphaMask = Vector256.Create(0xFF000000u);
            for (; i + 8 <= input.Length; i += 8)
            {
                Vector256<uint> value = Vector256.LoadUnsafe(ref source, (nuint)i);
                Vector256<uint> alpha = Avx2.And(value, alphaMask);
                if (Avx2.MoveMask(Avx2.CompareEqual(alpha, alphaMask).AsByte()) != -1) return false;
            }
        }

        for (; i < input.Length; i++)
        {
            if ((input[i] >> 24) != 255) return false;
        }

        return true;
    }

    /// <summary>Compacts native BGRA bytes to tightly packed RGB24.</summary>
    private static PixelBuffer ToRgb(Vp8lImage image)
    {
        PixelBuffer pixels = new(image.Width, image.Height, 3);
        Span<byte> output = pixels.Span;
        ReadOnlySpan<byte> input = MemoryMarshal.AsBytes(image.Span);
        int pixel = 0;
        if (SimdPrimitives.AllowSsse3)
        {
            ref byte source = ref MemoryMarshal.GetReference(input);
            ref byte destination = ref MemoryMarshal.GetReference(output);
            Vector128<byte> shuffle = Vector128.Create(
                (byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80);
            for (; pixel + 4 <= image.Length; pixel += 4)
            {
                Vector128<byte> rgb = Ssse3.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(pixel * 4)), shuffle);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, pixel * 3), rgb.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, pixel * 3 + 8), rgb.AsUInt32().GetElement(2));
            }
        }

        ReadOnlySpan<uint> words = image.Span;
        for (; pixel < image.Length; pixel++)
        {
            uint color = words[pixel];
            int offset = pixel * 3;
            output[offset] = (byte)(color >> 16);
            output[offset + 1] = (byte)(color >> 8);
            output[offset + 2] = (byte)color;
        }

        return pixels;
    }

    /// <summary>Copies native ARGB words into the library RGBA byte layout.</summary>
    private static PixelBuffer ToRgba(Vp8lImage image)
    {
        PixelBuffer pixels = new(image.Width, image.Height);
        Span<byte> output = pixels.Span;
        ReadOnlySpan<uint> input = image.Span;
        int i = 0;
        if (SimdPrimitives.AllowSsse3)
        {
            ReadOnlySpan<byte> inputBytes = MemoryMarshal.AsBytes(input);
            ref byte inputReference = ref MemoryMarshal.GetReference(inputBytes);
            ref byte outputReference = ref MemoryMarshal.GetReference(output);
            Vector128<byte> shuffle = Vector128.Create(
                (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            for (; i + 4 <= input.Length; i += 4)
            {
                int byteOffset = i * 4;
                Vector128<byte> argb = Vector128.LoadUnsafe(ref inputReference, (nuint)byteOffset);
                Vector128<byte> rgba = Ssse3.Shuffle(argb, shuffle);
                rgba.StoreUnsafe(ref outputReference, (nuint)byteOffset);
            }
        }

        for (; i < input.Length; i++)
        {
            uint color = input[i];
            int offset = i * 4;
            output[offset] = (byte)(color >> 16);
            output[offset + 1] = (byte)(color >> 8);
            output[offset + 2] = (byte)color;
            output[offset + 3] = (byte)(color >> 24);
        }

        return pixels;
    }

    /// <summary>Rounds an integer division upward.</summary>
    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    /// <summary>Describes one transform and owns its parameter image.</summary>
    private sealed class Vp8lTransform
    {
        /// <summary>Initializes transform state.</summary>
        public Vp8lTransform(int type, int width, int height, int bits, Vp8lImage? image, int colorCount)
        {
            Type = type;
            Width = width;
            Height = height;
            Bits = bits;
            Image = image;
            ColorCount = colorCount;
        }

        public int Type { get; }
        public int Width { get; }
        public int Height { get; }
        public int Bits { get; }
        public Vp8lImage? Image { get; }
        public int ColorCount { get; }
    }

    /// <summary>Owns a 32-byte-aligned native ARGB word image.</summary>
    private sealed class Vp8lImage : IDisposable
    {
        private uint* pointer;
        private bool owns = true;

        /// <summary>Allocates a zeroed word image.</summary>
        public Vp8lImage(int width, int height)
        {
            Width = width;
            Height = height;
            Length = checked(width * height);
            pointer = (uint*)NativeMemory.AlignedAlloc(checked((nuint)Length * 4), 32);
            if (pointer is null)
            {
                throw new OutOfMemoryException();
            }

            NativeMemory.Clear(pointer, checked((nuint)Length * 4));
        }

        /// <summary>Initializes a temporary alias used to transfer ownership.</summary>
        private Vp8lImage(uint* pointer, int width, int height)
        {
            this.pointer = pointer;
            Width = width;
            Height = height;
            Length = checked(width * height);
        }

        public int Width { get; }
        public int Height { get; }
        public int Length { get; }
        public Span<uint> Span => new(pointer, Length);

        /// <summary>Transfers the allocation to a second owner before a using scope exits.</summary>
        public Vp8lImage DetachAlias()
        {
            owns = false;
            return new Vp8lImage(pointer, Width, Height);
        }

        /// <summary>Releases native word storage.</summary>
        public void Dispose()
        {
            if (owns && pointer is not null)
            {
                NativeMemory.AlignedFree(pointer);
            }

            pointer = null;
        }
    }

    /// <summary>Holds the five trees selected for a spatial entropy region.</summary>
    private sealed class Vp8lHuffmanGroup
    {
        /// <summary>Initializes one tree group.</summary>
        public Vp8lHuffmanGroup(Vp8lHuffman green, Vp8lHuffman red, Vp8lHuffman blue, Vp8lHuffman alpha, Vp8lHuffman distance)
        {
            Green = green;
            Red = red;
            Blue = blue;
            Alpha = alpha;
            Distance = distance;
        }

        public Vp8lHuffman Green { get; }
        public Vp8lHuffman Red { get; }
        public Vp8lHuffman Blue { get; }
        public Vp8lHuffman Alpha { get; }
        public Vp8lHuffman Distance { get; }
    }

    /// <summary>Decodes a bit-reversed canonical VP8L prefix code.</summary>
    private sealed class Vp8lHuffman
    {
        private readonly List<Node> nodes = [new Node()];
        private readonly int constantSymbol = -1;
        private readonly int rootBits;
        private readonly int[]? rootTable;
        private readonly int[]? secondaryTable;

        /// <summary>Builds a full tree from transmitted code lengths.</summary>
        public Vp8lHuffman(ReadOnlySpan<byte> lengths)
        {
            Span<int> counts = stackalloc int[16];
            int nonzero = 0;
            int onlySymbol = 0;
            int maximumLength = 0;
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length > 15)
                {
                    ThrowHelper.InvalidData("A VP8L Huffman code length exceeds 15 bits.");
                }

                if (length != 0)
                {
                    counts[length]++;
                    nonzero++;
                    onlySymbol = symbol;
                    maximumLength = Math.Max(maximumLength, length);
                }
            }

            if (nonzero == 0)
            {
                constantSymbol = 0;
                return;
            }

            if (nonzero == 1)
            {
                constantSymbol = onlySymbol;
                return;
            }

            Span<int> nextCode = stackalloc int[16];
            int[] reversedCodes = new int[lengths.Length];
            int code = 0;
            for (int bits = 1; bits <= 15; bits++)
            {
                code = (code + counts[bits - 1]) << 1;
                nextCode[bits] = code;
                if (code + counts[bits] > (1 << bits))
                {
                    ThrowHelper.InvalidData("A VP8L Huffman tree is oversubscribed.");
                }
            }

            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0)
                {
                    continue;
                }

                int symbolCode = nextCode[length]++;
                reversedCodes[symbol] = ReverseBits(symbolCode, length);
                int nodeIndex = 0;
                for (int bitIndex = 0; bitIndex < length; bitIndex++)
                {
                    int bit = (symbolCode >> (length - 1 - bitIndex)) & 1;
                    Node node = nodes[nodeIndex];
                    int next = bit == 0 ? node.Zero : node.One;
                    if (next < 0)
                    {
                        next = nodes.Count;
                        nodes.Add(new Node());
                        if (bit == 0)
                        {
                            node.Zero = next;
                        }
                        else
                        {
                            node.One = next;
                        }

                        nodes[nodeIndex] = node;
                    }

                    nodeIndex = next;
                }

                Node leaf = nodes[nodeIndex];
                if (leaf.Symbol >= 0 || leaf.Zero >= 0 || leaf.One >= 0)
                {
                    ThrowHelper.InvalidData("A VP8L Huffman tree contains duplicate codes.");
                }

                leaf.Symbol = symbol;
                nodes[nodeIndex] = leaf;
            }

            rootBits = Math.Min(maximumLength, 10);
            rootTable = new int[1 << rootBits];
            int rootMask = rootTable.Length - 1;
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0 || length > rootBits) continue;
                int packed = (symbol << 5) | length;
                int step = 1 << length;
                for (int prefix = reversedCodes[symbol]; prefix < rootTable.Length; prefix += step)
                {
                    rootTable[prefix] = packed;
                }
            }

            int[] suffixBitsByPrefix = new int[rootTable.Length];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length > rootBits)
                {
                    int prefix = reversedCodes[symbol] & rootMask;
                    suffixBitsByPrefix[prefix] = Math.Max(suffixBitsByPrefix[prefix], length - rootBits);
                }
            }

            int secondaryLength = 0;
            for (int prefix = 0; prefix < suffixBitsByPrefix.Length; prefix++)
            {
                int bits = suffixBitsByPrefix[prefix];
                if (bits == 0) continue;
                rootTable[prefix] = ~((secondaryLength << 4) | bits);
                secondaryLength += 1 << bits;
            }

            secondaryTable = secondaryLength == 0 ? null : new int[secondaryLength];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length <= rootBits) continue;
                int reversed = reversedCodes[symbol];
                int prefix = reversed & rootMask;
                int info = ~rootTable[prefix];
                int offset = info >> 4;
                int tableBits = info & 15;
                int suffixLength = length - rootBits;
                int suffix = (reversed >> rootBits) & ((1 << suffixLength) - 1);
                int packed = (symbol << 5) | length;
                int step = 1 << suffixLength;
                for (int index = suffix; index < (1 << tableBits); index += step)
                {
                    secondaryTable![offset + index] = packed;
                }
            }
        }

        /// <summary>Decodes one tree symbol, consuming no bits for a single-leaf tree.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Decode(ref Vp8lBitReader reader)
        {
            if (constantSymbol >= 0)
            {
                return constantSymbol;
            }

            if (reader.TryPeekBits(rootBits, out int prefix))
            {
                int entry = rootTable![prefix];
                if (entry > 0)
                {
                    reader.ConsumeBits(entry & 31);
                    return entry >> 5;
                }

                if (entry < 0)
                {
                    int info = ~entry;
                    int suffixBits = info & 15;
                    if (reader.TryPeekBits(rootBits + suffixBits, out int extended))
                    {
                        int secondary = secondaryTable![(info >> 4) + (extended >> rootBits)];
                        if (secondary > 0)
                        {
                            reader.ConsumeBits(secondary & 31);
                            return secondary >> 5;
                        }
                    }
                }
            }

            int nodeIndex = 0;
            for (int depth = 0; depth < 15; depth++)
            {
                Node node = nodes[nodeIndex];
                nodeIndex = reader.ReadBits(1) == 0 ? node.Zero : node.One;
                if (nodeIndex < 0 || nodeIndex >= nodes.Count)
                {
                    ThrowHelper.InvalidData("VP8L data contains an invalid Huffman code.");
                }

                int symbol = nodes[nodeIndex].Symbol;
                if (symbol >= 0)
                {
                    return symbol;
                }
            }

            ThrowHelper.InvalidData("VP8L Huffman code exceeds 15 bits.");
            return 0;
        }

        /// <summary>Reverses a canonical code for direct lookup in the LSB-first reservoir.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReverseBits(int value, int count)
        {
            int reversed = 0;
            for (int i = 0; i < count; i++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }

            return reversed;
        }

        /// <summary>Represents one mutable tree node during construction.</summary>
        private struct Node
        {
            public int Zero = -1;
            public int One = -1;
            public int Symbol = -1;

            public Node()
            {
            }
        }
    }

    /// <summary>Reads VP8L integers least-significant bit first.</summary>
    private ref struct Vp8lBitReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int bytePosition;
        private ulong bits;
        private int bitCount;

        /// <summary>Initializes a bit reader.</summary>
        public Vp8lBitReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            bytePosition = 0;
            bits = 0;
            bitCount = 0;
        }

        /// <summary>Reads up to 24 little-endian bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadBits(int count)
        {
            if ((uint)count > 24)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (!TryFill(count))
            {
                ThrowHelper.UnexpectedEnd();
            }

            int value = (int)(bits & ((1UL << count) - 1));
            bits >>= count;
            bitCount -= count;
            return value;
        }

        /// <summary>Peeks without consuming, returning false at a byte-exact stream end.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPeekBits(int count, out int value)
        {
            if (!TryFill(count))
            {
                value = 0;
                return false;
            }

            value = (int)(bits & ((1UL << count) - 1));
            return true;
        }

        /// <summary>Consumes bits previously made available by a successful peek.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ConsumeBits(int count)
        {
            bits >>= count;
            bitCount -= count;
        }

        /// <summary>Refills four bytes at a time when possible.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFill(int count)
        {
            while (bitCount < count)
            {
                if (bytePosition >= data.Length) return false;
                if (data.Length - bytePosition >= 4 && bitCount <= 23)
                {
                    ref byte source = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), bytePosition);
                    bits |= (ulong)Unsafe.ReadUnaligned<uint>(ref source) << bitCount;
                    bytePosition += 4;
                    bitCount += 32;
                }
                else
                {
                    bits |= (ulong)data[bytePosition++] << bitCount;
                    bitCount += 8;
                }
            }

            return true;
        }
    }
}
