using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.Jpeg;

/// <summary>Encodes RGBA32 pixels as an eight-bit baseline Huffman JPEG.</summary>
internal static class JpegEncoder
{
    private const int PreparedAcWordBits = 56;
    private const int MaximumPreparedAcWords = 30;
    private static ReadOnlySpan<byte> ZigZag =>
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static ReadOnlySpan<byte> LuminanceQuantization =>
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68,109,103, 77,
        24, 35, 55, 64, 81,104,113, 92,
        49, 64, 78, 87,103,121,120,101,
        72, 92, 95, 98,112,100,103, 99,
    ];

    private static ReadOnlySpan<byte> ChrominanceQuantization =>
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static ReadOnlySpan<byte> DcLuminanceCounts => [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static ReadOnlySpan<byte> DcChrominanceCounts => [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static ReadOnlySpan<byte> DcValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static ReadOnlySpan<byte> AcLuminanceCounts => [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 125];
    private static ReadOnlySpan<byte> AcChrominanceCounts => [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 119];

    private static ReadOnlySpan<byte> AcLuminanceValues =>
    [
        0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,
        0x22,0x71,0x14,0x32,0x81,0x91,0xA1,0x08,0x23,0x42,0xB1,0xC1,0x15,0x52,0xD1,0xF0,
        0x24,0x33,0x62,0x72,0x82,0x09,0x0A,0x16,0x17,0x18,0x19,0x1A,0x25,0x26,0x27,0x28,
        0x29,0x2A,0x34,0x35,0x36,0x37,0x38,0x39,0x3A,0x43,0x44,0x45,0x46,0x47,0x48,0x49,
        0x4A,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
        0x6A,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
        0x8A,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9A,0xA2,0xA3,0xA4,0xA5,0xA6,0xA7,
        0xA8,0xA9,0xAA,0xB2,0xB3,0xB4,0xB5,0xB6,0xB7,0xB8,0xB9,0xBA,0xC2,0xC3,0xC4,0xC5,
        0xC6,0xC7,0xC8,0xC9,0xCA,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,0xE1,0xE2,
        0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,0xE9,0xEA,0xF1,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,
        0xF9,0xFA,
    ];

    private static ReadOnlySpan<byte> AcChrominanceValues =>
    [
        0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,
        0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,0xA1,0xB1,0xC1,0x09,0x23,0x33,0x52,0xF0,
        0x15,0x62,0x72,0xD1,0x0A,0x16,0x24,0x34,0xE1,0x25,0xF1,0x17,0x18,0x19,0x1A,0x26,
        0x27,0x28,0x29,0x2A,0x35,0x36,0x37,0x38,0x39,0x3A,0x43,0x44,0x45,0x46,0x47,0x48,
        0x49,0x4A,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,0x63,0x64,0x65,0x66,0x67,0x68,
        0x69,0x6A,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x82,0x83,0x84,0x85,0x86,0x87,
        0x88,0x89,0x8A,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9A,0xA2,0xA3,0xA4,0xA5,
        0xA6,0xA7,0xA8,0xA9,0xAA,0xB2,0xB3,0xB4,0xB5,0xB6,0xB7,0xB8,0xB9,0xBA,0xC2,0xC3,
        0xC4,0xC5,0xC6,0xC7,0xC8,0xC9,0xCA,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,
        0xE2,0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,0xE9,0xEA,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,
        0xF9,0xFA,
    ];

    /// <summary>Writes a complete baseline JPEG while leaving the destination open.</summary>
    public static void Encode(PixelBuffer pixels, Stream destination, JpegEncoderOptions options)
    {
        if (options.Quality is < 1 or > 100 || !Enum.IsDefined(options.ChromaSubsampling))
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "JPEG quality or chroma subsampling is invalid.");
        }

        if (pixels.Width > ushort.MaxValue || pixels.Height > ushort.MaxValue)
        {
            throw new Image2DException(ImageErrorCode.UnsupportedFeature, "JPEG dimensions cannot exceed 65535 pixels.");
        }

        byte[] luminanceQuantization = ScaleQuantization(LuminanceQuantization, options.Quality);
        byte[] chrominanceQuantization = ScaleQuantization(ChrominanceQuantization, options.Quality);
        float[] luminanceReciprocals = CreateQuantizationReciprocals(luminanceQuantization);
        float[] chrominanceReciprocals = CreateQuantizationReciprocals(chrominanceQuantization);
        EncodingHuffmanTable dcLuminance = new(DcLuminanceCounts, DcValues);
        EncodingHuffmanTable acLuminance = new(AcLuminanceCounts, AcLuminanceValues);
        EncodingHuffmanTable dcChrominance = new(DcChrominanceCounts, DcValues);
        EncodingHuffmanTable acChrominance = new(AcChrominanceCounts, AcChrominanceValues);

        WriteMarker(destination, 0xD8);
        WriteApp0(destination);
        WriteQuantization(destination, luminanceQuantization, chrominanceQuantization);
        WriteFrame(destination, pixels.Width, pixels.Height, options.ChromaSubsampling);
        WriteHuffman(destination, 0, 0, DcLuminanceCounts, DcValues);
        WriteHuffman(destination, 1, 0, AcLuminanceCounts, AcLuminanceValues);
        WriteHuffman(destination, 0, 1, DcChrominanceCounts, DcValues);
        WriteHuffman(destination, 1, 1, AcChrominanceCounts, AcChrominanceValues);
        WriteScanHeader(destination);

        JpegBitWriter writer = new(destination);
        int previousY = 0;
        int previousCb = 0;
        int previousCr = 0;
        int mcuSize = options.ChromaSubsampling == JpegChromaSubsampling.Yuv420 ? 16 : 8;
        int blocksPerMcu = mcuSize == 16 ? 6 : 3;
        int coefficientsPerMcu = blocksPerMcu * 64;
        int mcuColumns = (pixels.Width + mcuSize - 1) / mcuSize;
        int mcuRows = (pixels.Height + mcuSize - 1) / mcuSize;
        int mcuCount = checked(mcuColumns * mcuRows);
        const int MaximumCoefficientBytes = 1 * 1024 * 1024;
        int mcusPerBatch = Math.Max(1, Math.Min(mcuCount, MaximumCoefficientBytes / checked(coefficientsPerMcu * sizeof(int))));
        int[] coefficientBuffer = ArrayPool<int>.Shared.Rent(checked(mcusPerBatch * coefficientsPerMcu));
        int maximumBlocksPerBatch = checked(mcusPerBatch * blocksPerMcu);
        ulong[] preparedAcWords = ArrayPool<ulong>.Shared.Rent(checked(maximumBlocksPerBatch * MaximumPreparedAcWords));
        byte[] preparedAcWordCounts = ArrayPool<byte>.Shared.Rent(maximumBlocksPerBatch);
        byte[] preparedAcLastBits = ArrayPool<byte>.Shared.Rent(maximumBlocksPerBatch);
        bool parallel = ParallelExecution.ShouldRun((long)pixels.Width * pixels.Height, 1_000_000);

        try
        {
            for (int batchStart = 0; batchStart < mcuCount; batchStart += mcusPerBatch)
            {
                int batchCount = Math.Min(mcusPerBatch, mcuCount - batchStart);
                ParallelExecution.For(0, batchCount, parallel && batchCount > 1, localMcu =>
                {
                    int mcu = batchStart + localMcu;
                    int mcuX = (mcu % mcuColumns) * mcuSize;
                    int mcuY = (mcu / mcuColumns) * mcuSize;
                    Span<int> destinationCoefficients = coefficientBuffer.AsSpan(localMcu * coefficientsPerMcu, coefficientsPerMcu);
                    PrepareMcu(
                        pixels,
                        mcuX,
                        mcuY,
                        mcuSize,
                        options,
                        luminanceReciprocals,
                        chrominanceReciprocals,
                        destinationCoefficients,
                        acLuminance,
                        acChrominance,
                        preparedAcWords.AsSpan(localMcu * blocksPerMcu * MaximumPreparedAcWords, blocksPerMcu * MaximumPreparedAcWords),
                        preparedAcWordCounts.AsSpan(localMcu * blocksPerMcu, blocksPerMcu),
                        preparedAcLastBits.AsSpan(localMcu * blocksPerMcu, blocksPerMcu));
                }, maximumDegreeOfParallelism: 4);

                for (int localMcu = 0; localMcu < batchCount; localMcu++)
                {
                    Span<int> mcu = coefficientBuffer.AsSpan(localMcu * coefficientsPerMcu, coefficientsPerMcu);
                    int luminanceBlocks = mcuSize == 16 ? 4 : 1;
                    for (int block = 0; block < luminanceBlocks; block++)
                    {
                        int preparedBlock = localMcu * blocksPerMcu + block;
                        EncodeDc(ref writer, mcu.Slice(block * 64, 64), ref previousY, dcLuminance);
                        WritePreparedAc(ref writer, preparedAcWords, preparedAcWordCounts, preparedAcLastBits, preparedBlock);
                    }

                    int blueBlock = localMcu * blocksPerMcu + luminanceBlocks;
                    EncodeDc(ref writer, mcu.Slice(luminanceBlocks * 64, 64), ref previousCb, dcChrominance);
                    WritePreparedAc(ref writer, preparedAcWords, preparedAcWordCounts, preparedAcLastBits, blueBlock);
                    int redBlock = blueBlock + 1;
                    EncodeDc(ref writer, mcu.Slice((luminanceBlocks + 1) * 64, 64), ref previousCr, dcChrominance);
                    WritePreparedAc(ref writer, preparedAcWords, preparedAcWordCounts, preparedAcLastBits, redBlock);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(coefficientBuffer);
            ArrayPool<ulong>.Shared.Return(preparedAcWords);
            ArrayPool<byte>.Shared.Return(preparedAcWordCounts);
            ArrayPool<byte>.Shared.Return(preparedAcLastBits);
        }

        writer.Flush();
        WriteMarker(destination, 0xD9);
    }

    /// <summary>Converts and transforms one independent MCU into quantized natural-order blocks.</summary>
    private static void PrepareMcu(
        PixelBuffer pixels,
        int mcuX,
        int mcuY,
        int mcuSize,
        JpegEncoderOptions options,
        ReadOnlySpan<float> luminanceReciprocals,
        ReadOnlySpan<float> chrominanceReciprocals,
        Span<int> destination,
        EncodingHuffmanTable acLuminance,
        EncodingHuffmanTable acChrominance,
        Span<ulong> preparedAcWords,
        Span<byte> preparedAcWordCounts,
        Span<byte> preparedAcLastBits)
    {
        Span<int> block = stackalloc int[64];
        if (mcuSize == 16)
        {
            Span<int> luminanceMcu = stackalloc int[256];
            Span<int> blueChromaMcu = stackalloc int[64];
            Span<int> redChromaMcu = stackalloc int[64];
            Load420Mcu(pixels, mcuX, mcuY, options, luminanceMcu, blueChromaMcu, redChromaMcu);
            for (int outputBlock = 0; outputBlock < 4; outputBlock++)
            {
                ForwardDctQuantize(luminanceMcu.Slice(outputBlock * 64, 64), luminanceReciprocals, destination.Slice(outputBlock * 64, 64));
            }

            ForwardDctQuantize(blueChromaMcu, chrominanceReciprocals, destination.Slice(4 * 64, 64));
            ForwardDctQuantize(redChromaMcu, chrominanceReciprocals, destination.Slice(5 * 64, 64));
            for (int outputBlock = 0; outputBlock < 6; outputBlock++)
            {
                PrepareAcBits(
                    destination.Slice(outputBlock * 64, 64),
                    outputBlock < 4 ? acLuminance : acChrominance,
                    preparedAcWords.Slice(outputBlock * MaximumPreparedAcWords, MaximumPreparedAcWords),
                    out preparedAcWordCounts[outputBlock],
                    out preparedAcLastBits[outputBlock]);
            }

            return;
        }

        LoadComponentBlock(pixels, mcuX, mcuY, 0, 1, options, block);
        ForwardDctQuantize(block, luminanceReciprocals, destination[..64]);
        LoadComponentBlock(pixels, mcuX, mcuY, 1, 1, options, block);
        ForwardDctQuantize(block, chrominanceReciprocals, destination.Slice(64, 64));
        LoadComponentBlock(pixels, mcuX, mcuY, 2, 1, options, block);
        ForwardDctQuantize(block, chrominanceReciprocals, destination.Slice(128, 64));
        for (int outputBlock = 0; outputBlock < 3; outputBlock++)
        {
            PrepareAcBits(
                destination.Slice(outputBlock * 64, 64),
                outputBlock == 0 ? acLuminance : acChrominance,
                preparedAcWords.Slice(outputBlock * MaximumPreparedAcWords, MaximumPreparedAcWords),
                out preparedAcWordCounts[outputBlock],
                out preparedAcLastBits[outputBlock]);
        }
    }

    /// <summary>Converts one 16x16 RGBA MCU to four Y blocks and averaged 4:2:0 chroma planes in one pass.</summary>
    private static void Load420Mcu(
        PixelBuffer pixels,
        int startX,
        int startY,
        JpegEncoderOptions options,
        Span<int> luminance,
        Span<int> blueChroma,
        Span<int> redChroma)
    {
        if (startX + 16 <= pixels.Width && startY + 16 <= pixels.Height)
        {
            if (pixels.BytesPerPixel == 3)
            {
                Load420Rgb24Interior(pixels, startX, startY, luminance, blueChroma, redChroma);
            }
            else
            {
                Load420Rgba32Interior(pixels, startX, startY, options, luminance, blueChroma, redChroma);
            }

            return;
        }

        Span<int> blueSums = stackalloc int[64];
        Span<int> redSums = stackalloc int[64];
        blueSums.Clear();
        redSums.Clear();
        Span<byte> data = pixels.Span;
        int bytesPerPixel = pixels.BytesPerPixel;
        for (int y = 0; y < 16; y++)
        {
            int sourceY = Math.Min(startY + y, pixels.Height - 1);
            for (int x = 0; x < 16; x++)
            {
                int sourceX = Math.Min(startX + x, pixels.Width - 1);
                int offset = (sourceY * pixels.Width + sourceX) * bytesPerPixel;
                int alpha = bytesPerPixel == 4 ? data[offset + 3] : 255;
                int red = Flatten(data[offset], alpha, options.BackgroundRed);
                int green = Flatten(data[offset + 1], alpha, options.BackgroundGreen);
                int blue = Flatten(data[offset + 2], alpha, options.BackgroundBlue);
                int luminanceBlock = ((y >> 3) * 2 + (x >> 3)) * 64;
                int luminanceIndex = luminanceBlock + ((y & 7) * 8) + (x & 7);
                luminance[luminanceIndex] = ((19595 * red + 38470 * green + 7471 * blue + 32768) >> 16) - 128;
                int chromaIndex = (y >> 1) * 8 + (x >> 1);
                blueSums[chromaIndex] += ((-11059 * red - 21709 * green + 32768 * blue + 32768) >> 16) + 128;
                redSums[chromaIndex] += ((32768 * red - 27439 * green - 5329 * blue + 32768) >> 16) + 128;
            }
        }

        for (int i = 0; i < 64; i++)
        {
            blueChroma[i] = ((blueSums[i] + 2) >> 2) - 128;
            redChroma[i] = ((redSums[i] + 2) >> 2) - 128;
        }
    }

    /// <summary>Converts one complete opaque RGB24 MCU without edge or alpha branches.</summary>
    private static void Load420Rgb24Interior(
        PixelBuffer pixels,
        int startX,
        int startY,
        Span<int> luminance,
        Span<int> blueChroma,
        Span<int> redChroma)
    {
        Span<byte> data = pixels.Span;
        int sourceStride = pixels.Width * 3;
        for (int chromaY = 0; chromaY < 8; chromaY++)
        for (int chromaX = 0; chromaX < 8; chromaX++)
        {
            int blueTotal = 0;
            int redTotal = 0;
            int localY = chromaY * 2;
            int localX = chromaX * 2;
            for (int dy = 0; dy < 2; dy++)
            {
                int offset = (startY + localY + dy) * sourceStride + (startX + localX) * 3;
                for (int dx = 0; dx < 2; dx++, offset += 3)
                {
                    int red = data[offset];
                    int green = data[offset + 1];
                    int blue = data[offset + 2];
                    int x = localX + dx;
                    int y = localY + dy;
                    int luminanceIndex = (((y >> 3) * 2 + (x >> 3)) * 64) + ((y & 7) * 8) + (x & 7);
                    luminance[luminanceIndex] = ((19595 * red + 38470 * green + 7471 * blue + 32768) >> 16) - 128;
                    blueTotal += ((-11059 * red - 21709 * green + 32768 * blue + 32768) >> 16) + 128;
                    redTotal += ((32768 * red - 27439 * green - 5329 * blue + 32768) >> 16) + 128;
                }
            }

            int chromaIndex = chromaY * 8 + chromaX;
            blueChroma[chromaIndex] = ((blueTotal + 2) >> 2) - 128;
            redChroma[chromaIndex] = ((redTotal + 2) >> 2) - 128;
        }
    }

    /// <summary>Converts one complete RGBA32 MCU while flattening alpha onto the configured background.</summary>
    private static void Load420Rgba32Interior(
        PixelBuffer pixels,
        int startX,
        int startY,
        JpegEncoderOptions options,
        Span<int> luminance,
        Span<int> blueChroma,
        Span<int> redChroma)
    {
        Span<byte> data = pixels.Span;
        int sourceStride = pixels.Width * 4;
        for (int chromaY = 0; chromaY < 8; chromaY++)
        for (int chromaX = 0; chromaX < 8; chromaX++)
        {
            int blueTotal = 0;
            int redTotal = 0;
            int localY = chromaY * 2;
            int localX = chromaX * 2;
            for (int dy = 0; dy < 2; dy++)
            {
                int offset = (startY + localY + dy) * sourceStride + (startX + localX) * 4;
                for (int dx = 0; dx < 2; dx++, offset += 4)
                {
                    int alpha = data[offset + 3];
                    int red = Flatten(data[offset], alpha, options.BackgroundRed);
                    int green = Flatten(data[offset + 1], alpha, options.BackgroundGreen);
                    int blue = Flatten(data[offset + 2], alpha, options.BackgroundBlue);
                    int x = localX + dx;
                    int y = localY + dy;
                    int luminanceIndex = (((y >> 3) * 2 + (x >> 3)) * 64) + ((y & 7) * 8) + (x & 7);
                    luminance[luminanceIndex] = ((19595 * red + 38470 * green + 7471 * blue + 32768) >> 16) - 128;
                    blueTotal += ((-11059 * red - 21709 * green + 32768 * blue + 32768) >> 16) + 128;
                    redTotal += ((32768 * red - 27439 * green - 5329 * blue + 32768) >> 16) + 128;
                }
            }

            int chromaIndex = chromaY * 8 + chromaX;
            blueChroma[chromaIndex] = ((blueTotal + 2) >> 2) - 128;
            redChroma[chromaIndex] = ((redTotal + 2) >> 2) - 128;
        }
    }

    /// <summary>Loads one level-shifted Y, Cb, or Cr block with optional 2x2 chroma averaging.</summary>
    private static void LoadComponentBlock(PixelBuffer pixels, int startX, int startY, int component, int samplingStep, JpegEncoderOptions options, Span<int> block)
    {
        Span<byte> data = pixels.Span;
        int bytesPerPixel = pixels.BytesPerPixel;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int total = 0;
                for (int sy = 0; sy < samplingStep; sy++)
                {
                    int sourceY = Math.Min(startY + y * samplingStep + sy, pixels.Height - 1);
                    for (int sx = 0; sx < samplingStep; sx++)
                    {
                        int sourceX = Math.Min(startX + x * samplingStep + sx, pixels.Width - 1);
                        int offset = (sourceY * pixels.Width + sourceX) * bytesPerPixel;
                        int alpha = bytesPerPixel == 4 ? data[offset + 3] : 255;
                        int red = Flatten(data[offset], alpha, options.BackgroundRed);
                        int green = Flatten(data[offset + 1], alpha, options.BackgroundGreen);
                        int blue = Flatten(data[offset + 2], alpha, options.BackgroundBlue);
                        total += component switch
                        {
                            0 => ((19595 * red + 38470 * green + 7471 * blue + 32768) >> 16),
                            1 => ((-11059 * red - 21709 * green + 32768 * blue + 32768) >> 16) + 128,
                            _ => ((32768 * red - 27439 * green - 5329 * blue + 32768) >> 16) + 128,
                        };
                    }
                }

                int divisor = samplingStep * samplingStep;
                block[y * 8 + x] = ((total + divisor / 2) / divisor) - 128;
            }
        }
    }

    /// <summary>Flattens one straight-alpha channel onto the configured JPEG background.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Flatten(int foreground, int alpha, int background) => alpha == 255
        ? foreground
        : (foreground * alpha + background * (255 - alpha) + 127) / 255;

    /// <summary>Runs a fixed-point separable orthonormal DCT and quantizes in natural order.</summary>
    private static void ForwardDctQuantize(ReadOnlySpan<int> source, ReadOnlySpan<float> quantizationReciprocals, Span<int> destination)
    {
        const int Pass1Bits = 2;
        const int ConstBits = 13;
        const int Fix0298631336 = 2446;
        const int Fix0390180644 = 3196;
        const int Fix0541196100 = 4433;
        const int Fix0765366865 = 6270;
        const int Fix0899976223 = 7373;
        const int Fix1175875602 = 9633;
        const int Fix1501321110 = 12299;
        const int Fix1847759065 = 15137;
        const int Fix1961570560 = 16069;
        const int Fix2053119869 = 16819;
        const int Fix2562915447 = 20995;
        const int Fix3072710261 = 25172;

        Span<int> workspace = stackalloc int[64];
        for (int y = 0; y < 8; y++)
        {
            int offset = y * 8;
            long tmp0 = source[offset] + source[offset + 7];
            long tmp7 = source[offset] - source[offset + 7];
            long tmp1 = source[offset + 1] + source[offset + 6];
            long tmp6 = source[offset + 1] - source[offset + 6];
            long tmp2 = source[offset + 2] + source[offset + 5];
            long tmp5 = source[offset + 2] - source[offset + 5];
            long tmp3 = source[offset + 3] + source[offset + 4];
            long tmp4 = source[offset + 3] - source[offset + 4];

            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;
            workspace[offset] = (int)((tmp10 + tmp11) << Pass1Bits);
            workspace[offset + 4] = (int)((tmp10 - tmp11) << Pass1Bits);
            long z1 = (tmp12 + tmp13) * Fix0541196100;
            workspace[offset + 2] = Descale(z1 + tmp13 * Fix0765366865, ConstBits - Pass1Bits);
            workspace[offset + 6] = Descale(z1 - tmp12 * Fix1847759065, ConstBits - Pass1Bits);

            z1 = tmp4 + tmp7;
            long z2 = tmp5 + tmp6;
            long z3 = tmp4 + tmp6;
            long z4 = tmp5 + tmp7;
            long z5 = (z3 + z4) * Fix1175875602;
            tmp4 *= Fix0298631336;
            tmp5 *= Fix2053119869;
            tmp6 *= Fix3072710261;
            tmp7 *= Fix1501321110;
            z1 *= -Fix0899976223;
            z2 *= -Fix2562915447;
            z3 = z3 * -Fix1961570560 + z5;
            z4 = z4 * -Fix0390180644 + z5;
            workspace[offset + 7] = Descale(tmp4 + z1 + z3, ConstBits - Pass1Bits);
            workspace[offset + 5] = Descale(tmp5 + z2 + z4, ConstBits - Pass1Bits);
            workspace[offset + 3] = Descale(tmp6 + z2 + z3, ConstBits - Pass1Bits);
            workspace[offset + 1] = Descale(tmp7 + z1 + z4, ConstBits - Pass1Bits);
        }

        for (int x = 0; x < 8; x++)
        {
            long tmp0 = workspace[x] + workspace[56 + x];
            long tmp7 = workspace[x] - workspace[56 + x];
            long tmp1 = workspace[8 + x] + workspace[48 + x];
            long tmp6 = workspace[8 + x] - workspace[48 + x];
            long tmp2 = workspace[16 + x] + workspace[40 + x];
            long tmp5 = workspace[16 + x] - workspace[40 + x];
            long tmp3 = workspace[24 + x] + workspace[32 + x];
            long tmp4 = workspace[24 + x] - workspace[32 + x];

            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;
            StoreDctCoefficient(destination, quantizationReciprocals, x, Descale(tmp10 + tmp11, Pass1Bits));
            StoreDctCoefficient(destination, quantizationReciprocals, 32 + x, Descale(tmp10 - tmp11, Pass1Bits));
            long z1 = (tmp12 + tmp13) * Fix0541196100;
            StoreDctCoefficient(destination, quantizationReciprocals, 16 + x, Descale(z1 + tmp13 * Fix0765366865, ConstBits + Pass1Bits));
            StoreDctCoefficient(destination, quantizationReciprocals, 48 + x, Descale(z1 - tmp12 * Fix1847759065, ConstBits + Pass1Bits));

            z1 = tmp4 + tmp7;
            long z2 = tmp5 + tmp6;
            long z3 = tmp4 + tmp6;
            long z4 = tmp5 + tmp7;
            long z5 = (z3 + z4) * Fix1175875602;
            tmp4 *= Fix0298631336;
            tmp5 *= Fix2053119869;
            tmp6 *= Fix3072710261;
            tmp7 *= Fix1501321110;
            z1 *= -Fix0899976223;
            z2 *= -Fix2562915447;
            z3 = z3 * -Fix1961570560 + z5;
            z4 = z4 * -Fix0390180644 + z5;
            StoreDctCoefficient(destination, quantizationReciprocals, 56 + x, Descale(tmp4 + z1 + z3, ConstBits + Pass1Bits));
            StoreDctCoefficient(destination, quantizationReciprocals, 40 + x, Descale(tmp5 + z2 + z4, ConstBits + Pass1Bits));
            StoreDctCoefficient(destination, quantizationReciprocals, 24 + x, Descale(tmp6 + z2 + z3, ConstBits + Pass1Bits));
            StoreDctCoefficient(destination, quantizationReciprocals, 8 + x, Descale(tmp7 + z1 + z4, ConstBits + Pass1Bits));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Descale(long value, int bits)
    {
        long half = 1L << (bits - 1);
        return value >= 0 ? (int)((value + half) >> bits) : -(int)(((-value) + half) >> bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreDctCoefficient(Span<int> destination, ReadOnlySpan<float> quantizationReciprocals, int index, int scaledCoefficient)
    {
        float scaled = scaledCoefficient * quantizationReciprocals[index];
        int coefficient = (int)(scaled + (scaledCoefficient >= 0 ? 0.5f : -0.5f));
        destination[index] = index == 0
            ? Math.Clamp(coefficient, -1024, 1023)
            : Math.Clamp(coefficient, -1023, 1023);
    }

    private static float[] CreateQuantizationReciprocals(ReadOnlySpan<byte> quantization)
    {
        float[] reciprocals = new float[64];
        for (int i = 0; i < reciprocals.Length; i++)
        {
            reciprocals[i] = 1f / (quantization[i] * 8f);
        }

        return reciprocals;
    }

    /// <summary>Entropy-encodes the predictor-dependent DC value of one quantized block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeDc(ref JpegBitWriter writer, scoped ReadOnlySpan<int> block, ref int previousDc, EncodingHuffmanTable dcTable)
    {
        int difference = block[0] - previousDc;
        previousDc = block[0];
        int size = MagnitudeBits(difference);
        dcTable.Write(ref writer, size, difference, size);
    }

    /// <summary>Prepares predictor-independent AC bits into bounded 56-bit words.</summary>
    private static void PrepareAcBits(
        scoped ReadOnlySpan<int> block,
        EncodingHuffmanTable table,
        Span<ulong> words,
        out byte wordCount,
        out byte lastWordBits)
    {
        ulong accumulator = 0;
        int accumulatorBits = 0;
        int outputWord = 0;
        int last = 63;
        while (last > 0 && block[ZigZag[last]] == 0)
        {
            last--;
        }

        int zeroRun = 0;
        for (int i = 1; i <= last; i++)
        {
            int value = block[ZigZag[i]];
            if (value == 0)
            {
                zeroRun++;
                continue;
            }

            while (zeroRun >= 16)
            {
                table.Get(0xF0, out int zrlCode, out int zrlCodeBits);
                AppendPreparedBits(words, ref outputWord, ref accumulator, ref accumulatorBits, (uint)zrlCode, zrlCodeBits);
                zeroRun -= 16;
            }

            int size = MagnitudeBits(value);
            table.Get((zeroRun << 4) | size, out int code, out int codeBits);
            uint amplitude = (uint)(value < 0 ? value + ((1 << size) - 1) : value);
            uint combined = ((uint)code << size) | amplitude;
            AppendPreparedBits(words, ref outputWord, ref accumulator, ref accumulatorBits, combined, codeBits + size);
            zeroRun = 0;
        }

        if (last < 63)
        {
            table.Get(0, out int eobCode, out int eobCodeBits);
            AppendPreparedBits(words, ref outputWord, ref accumulator, ref accumulatorBits, (uint)eobCode, eobCodeBits);
        }

        if (accumulatorBits != 0)
        {
            words[outputWord++] = accumulator;
        }

        wordCount = checked((byte)outputWord);
        lastWordBits = checked((byte)accumulatorBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendPreparedBits(
        Span<ulong> words,
        ref int outputWord,
        ref ulong accumulator,
        ref int accumulatorBits,
        uint value,
        int bits)
    {
        if (accumulatorBits + bits > PreparedAcWordBits)
        {
            int firstBits = PreparedAcWordBits - accumulatorBits;
            accumulator = (accumulator << firstBits) | (value >> (bits - firstBits));
            words[outputWord++] = accumulator;
            bits -= firstBits;
            value &= (1u << bits) - 1;
            accumulator = value;
            accumulatorBits = bits;
            return;
        }

        accumulator = (accumulator << bits) | value;
        accumulatorBits += bits;
    }

    /// <summary>Appends prepared AC words to the single ordered byte-stuffing writer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePreparedAc(
        ref JpegBitWriter writer,
        ulong[] words,
        byte[] wordCounts,
        byte[] lastWordBits,
        int block)
    {
        int count = wordCounts[block];
        int offset = block * MaximumPreparedAcWords;
        for (int word = 0; word < count; word++)
        {
            int bits = word == count - 1 ? lastWordBits[block] : PreparedAcWordBits;
            writer.Write(words[offset + word], bits);
        }
    }

    /// <summary>Counts the category bits needed by a coefficient.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MagnitudeBits(int value)
    {
        uint magnitude = (uint)Math.Abs(value);
        return magnitude == 0 ? 0 : BitOperations.Log2(magnitude) + 1;
    }

    /// <summary>Scales a reference quantization table according to JPEG quality convention.</summary>
    private static byte[] ScaleQuantization(ReadOnlySpan<byte> source, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        byte[] result = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            result[i] = (byte)Math.Clamp((source[i] * scale + 50) / 100, 1, 255);
        }

        return result;
    }

    /// <summary>Writes the JFIF APP0 segment.</summary>
    private static void WriteApp0(Stream destination)
    {
        ReadOnlySpan<byte> payload = [0x4A,0x46,0x49,0x46,0,1,1,0,0,1,0,1,0,0];
        WriteSegment(destination, 0xE0, payload);
    }

    /// <summary>Writes luminance and chrominance DQT tables.</summary>
    private static void WriteQuantization(Stream destination, ReadOnlySpan<byte> luminance, ReadOnlySpan<byte> chrominance)
    {
        Span<byte> payload = stackalloc byte[130];
        payload[0] = 0;
        payload[65] = 1;
        for (int i = 0; i < 64; i++)
        {
            payload[1 + i] = luminance[ZigZag[i]];
            payload[66 + i] = chrominance[ZigZag[i]];
        }

        WriteSegment(destination, 0xDB, payload);
    }

    /// <summary>Writes an SOF0 frame with three YCbCr components.</summary>
    private static void WriteFrame(Stream destination, int width, int height, JpegChromaSubsampling subsampling)
    {
        Span<byte> payload = stackalloc byte[15];
        payload[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(payload[1..], (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(payload[3..], (ushort)width);
        payload[5] = 3;
        payload[6] = 1;
        payload[7] = subsampling == JpegChromaSubsampling.Yuv420 ? (byte)0x22 : (byte)0x11;
        payload[8] = 0;
        payload[9] = 2;
        payload[10] = 0x11;
        payload[11] = 1;
        payload[12] = 3;
        payload[13] = 0x11;
        payload[14] = 1;
        WriteSegment(destination, 0xC0, payload);
    }

    /// <summary>Writes one DHT table.</summary>
    private static void WriteHuffman(Stream destination, int tableClass, int id, ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
    {
        byte[] payload = new byte[17 + values.Length];
        payload[0] = (byte)((tableClass << 4) | id);
        counts.CopyTo(payload.AsSpan(1));
        values.CopyTo(payload.AsSpan(17));
        WriteSegment(destination, 0xC4, payload);
    }

    /// <summary>Writes the single interleaved baseline scan header.</summary>
    private static void WriteScanHeader(Stream destination)
    {
        ReadOnlySpan<byte> payload = [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0];
        WriteSegment(destination, 0xDA, payload);
    }

    /// <summary>Writes a length-delimited JPEG segment.</summary>
    private static void WriteSegment(Stream destination, byte marker, ReadOnlySpan<byte> payload)
    {
        WriteMarker(destination, marker);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)(payload.Length + 2)));
        destination.Write(length);
        destination.Write(payload);
    }

    /// <summary>Writes a marker without a length field.</summary>
    private static void WriteMarker(Stream destination, byte marker)
    {
        destination.WriteByte(0xFF);
        destination.WriteByte(marker);
    }

    /// <summary>Maps symbols to canonical Huffman code words.</summary>
    private sealed class EncodingHuffmanTable
    {
        private readonly uint[] entries = new uint[256];

        /// <summary>Builds a symbol-to-code map from JPEG DHT data.</summary>
        public EncodingHuffmanTable(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
        {
            int code = 0;
            int index = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < counts[length - 1]; i++)
                {
                    int symbol = values[index++];
                    entries[symbol] = ((uint)code++ << 5) | (uint)length;
                }

                code <<= 1;
            }
        }

        /// <summary>Writes one known symbol.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ref JpegBitWriter writer, int symbol)
        {
            uint entry = entries[symbol];
            int length = (int)(entry & 31);
            if (length == 0)
            {
                ThrowHelper.InvalidData("A JPEG coefficient cannot be represented by the baseline Huffman table.");
            }

            writer.Write((int)(entry >> 5), length);
        }

        /// <summary>Writes a Huffman symbol and its signed amplitude in one reservoir operation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ref JpegBitWriter writer, int symbol, int value, int amplitudeBits)
        {
            uint entry = entries[symbol];
            int length = (int)(entry & 31);
            if (length == 0)
            {
                ThrowHelper.InvalidData("A JPEG coefficient cannot be represented by the baseline Huffman table.");
            }

            int amplitude = amplitudeBits == 0
                ? 0
                : value < 0 ? value + ((1 << amplitudeBits) - 1) : value;
            int combined = ((int)(entry >> 5) << amplitudeBits) | amplitude;
            writer.Write(combined, length + amplitudeBits);
        }

        /// <summary>Gets one canonical code without writing it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Get(int symbol, out int code, out int length)
        {
            uint entry = entries[symbol];
            length = (int)(entry & 31);
            if (length == 0)
            {
                ThrowHelper.InvalidData("A JPEG coefficient cannot be represented by the baseline Huffman table.");
            }

            code = (int)(entry >> 5);
        }
    }

    /// <summary>Writes MSB-first entropy bits with mandatory 0xFF byte stuffing.</summary>
    private ref struct JpegBitWriter
    {
        private readonly Stream destination;
        private readonly byte[] outputBuffer;
        private int outputCount;
        private ulong bits;
        private int bitCount;

        /// <summary>Initializes a bit writer.</summary>
        public JpegBitWriter(Stream destination)
        {
            this.destination = destination;
            outputBuffer = new byte[32768];
            outputCount = 0;
            bits = 0;
            bitCount = 0;
        }

        /// <summary>Writes the low <paramref name="count"/> bits MSB first.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int value, int count) => Write((uint)value, count);

        /// <summary>Writes the low <paramref name="count"/> bits MSB first.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ulong value, int count)
        {
            if (outputCount > outputBuffer.Length - 16)
            {
                FlushBuffer();
            }

            ulong mask = (1UL << count) - 1;
            bits = (bits << count) | (value & mask);
            bitCount += count;
            while (bitCount >= 8)
            {
                int shift = bitCount - 8;
                byte output = (byte)(bits >> shift);
                outputBuffer[outputCount++] = output;
                if (output == 0xFF)
                {
                    outputBuffer[outputCount++] = 0;
                }

                bitCount -= 8;
            }
        }

        /// <summary>Pads the final entropy byte with one bits.</summary>
        public void Flush()
        {
            if (bitCount != 0)
            {
                int padding = 8 - bitCount;
                Write((1 << padding) - 1, padding);
            }

            FlushBuffer();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void FlushBuffer()
        {
            if (outputCount == 0)
            {
                return;
            }

            destination.Write(outputBuffer.AsSpan(0, outputCount));
            outputCount = 0;
        }
    }
}
