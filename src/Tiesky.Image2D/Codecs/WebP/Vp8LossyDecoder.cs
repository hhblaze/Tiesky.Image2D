using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.WebP;

/// <summary>Decodes static intra-only VP8 key frames stored by lossy WebP.</summary>
internal static unsafe class Vp8LossyDecoder
{
    private const int DcMode = 0;
    private const int TrueMotionMode = 1;
    private const int VerticalMode = 2;
    private const int HorizontalMode = 3;
    private const int BlockMode = 4;

    private static ReadOnlySpan<byte> Category3 => [173, 148, 140];
    private static ReadOnlySpan<byte> Category4 => [176, 155, 140, 135];
    private static ReadOnlySpan<byte> Category5 => [180, 157, 141, 134, 130];
    private static ReadOnlySpan<byte> Category6 => [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];
    private static ReadOnlySpan<ushort> CoefficientProbabilityBands => [0, 33, 66, 99, 198, 132, 165, 198, 198, 198, 198, 198, 198, 198, 198, 231];

    /// <summary>Decodes a VP8 payload and optional ALPH chunk.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> alpha, long maximumPixels, int canvasWidth, int canvasHeight)
    {
        if (payload.Length < 12)
        {
            ThrowHelper.InvalidData("The VP8 key frame is truncated.");
        }

        int tag = payload[0] | (payload[1] << 8) | (payload[2] << 16);
        if ((tag & 1) != 0)
        {
            ThrowHelper.Unsupported("Inter-frame VP8 data is not valid as a static WebP image.");
        }

        if (((tag >> 1) & 7) > 3 || (tag & 0x10) == 0 || payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
        {
            ThrowHelper.InvalidData("The VP8 key-frame header is invalid.");
        }

        int firstPartitionLength = tag >> 5;
        int width = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3FFF;
        int height = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3FFF;
        ThrowHelper.ValidateDimensions(width, height, maximumPixels);
        if ((canvasWidth != 0 && canvasWidth != width) || (canvasHeight != 0 && canvasHeight != height))
        {
            ThrowHelper.InvalidData("The WebP canvas and VP8 dimensions disagree.");
        }

        int tokenStart = checked(10 + firstPartitionLength);
        if (firstPartitionLength < 2 || tokenStart > payload.Length)
        {
            ThrowHelper.InvalidData("The first VP8 partition length is invalid.");
        }

        fixed (byte* payloadPointer = payload)
        {
            Vp8BooleanReader header = new(payloadPointer + 10, firstPartitionLength);
            using FrameState frame = new(width, height);
            ParseHeader(ref header, frame, payload, tokenStart);
            ParseModes(ref header, frame);
            DecodePartitions(payloadPointer, payload.Length, frame);
            ApplyAlpha(frame, alpha);
            bool isOpaque = frame.Alpha is null || AlphaIsOpaque(frame.Alpha);
            PixelBuffer pixels = ConvertToPixelBuffer(frame, isOpaque);
            return new DecodedImage(pixels, ExifOrientation.Normal, isOpaque: isOpaque);
        }
    }

    /// <summary>Parses key-frame control fields through coefficient probabilities.</summary>
    private static void ParseHeader(ref Vp8BooleanReader reader, FrameState frame, ReadOnlySpan<byte> payload, int tokenStart)
    {
        _ = reader.ReadBit();
        _ = reader.ReadBit();

        frame.SegmentationEnabled = reader.ReadBit() != 0;
        if (frame.SegmentationEnabled)
        {
            frame.UpdateSegmentMap = reader.ReadBit() != 0;
            bool updateData = reader.ReadBit() != 0;
            if (updateData)
            {
                frame.SegmentAbsolute = reader.ReadBit() != 0;
                for (int i = 0; i < 4; i++) frame.SegmentQuantizers[i] = reader.ReadOptionalSigned(7);
                for (int i = 0; i < 4; i++) frame.SegmentFilters[i] = reader.ReadOptionalSigned(6);
            }

            if (frame.UpdateSegmentMap)
            {
                for (int i = 0; i < 3; i++) frame.SegmentProbabilities[i] = reader.ReadBit() == 0 ? (byte)255 : (byte)reader.ReadBits(8);
            }
        }

        frame.SimpleFilter = reader.ReadBit() != 0;
        frame.FilterLevel = reader.ReadBits(6);
        frame.FilterSharpness = reader.ReadBits(3);
        if (reader.ReadBit() != 0 && reader.ReadBit() != 0)
        {
            for (int i = 0; i < 4; i++) _ = reader.ReadOptionalSigned(6);
            for (int i = 0; i < 4; i++) _ = reader.ReadOptionalSigned(6);
        }

        int partitionCount = 1 << reader.ReadBits(2);
        frame.PartitionCount = partitionCount;
        frame.BaseQuantizer = reader.ReadBits(7);
        frame.Y1DcDelta = reader.ReadOptionalSigned(4);
        frame.Y2DcDelta = reader.ReadOptionalSigned(4);
        frame.Y2AcDelta = reader.ReadOptionalSigned(4);
        frame.UvDcDelta = reader.ReadOptionalSigned(4);
        frame.UvAcDelta = reader.ReadOptionalSigned(4);
        _ = reader.ReadBit();

        byte[] probabilities = frame.CoefficientProbabilities;
        ReadOnlySpan<byte> updates = Vp8Tables.CoefficientUpdates;
        for (int i = 0; i < probabilities.Length; i++)
        {
            if (reader.ReadBit(updates[i]) != 0) probabilities[i] = (byte)reader.ReadBits(8);
        }

        frame.UseSkipProbability = reader.ReadBit() != 0;
        if (frame.UseSkipProbability) frame.SkipProbability = reader.ReadBits(8);

        int sizeTableLength = checked((partitionCount - 1) * 3);
        if (tokenStart + sizeTableLength > payload.Length) ThrowHelper.InvalidData("The VP8 token-partition table is truncated.");
        int partitionOffset = tokenStart + sizeTableLength;
        for (int i = 0; i < partitionCount; i++)
        {
            int tableOffset = tokenStart + (i * 3);
            int length = i == partitionCount - 1 ? payload.Length - partitionOffset : payload[tableOffset] | (payload[tableOffset + 1] << 8) | (payload[tableOffset + 2] << 16);
            if (length < 2 || partitionOffset > payload.Length - length) ThrowHelper.InvalidData("A VP8 token partition has an invalid length.");
            frame.PartitionOffsets[i] = partitionOffset;
            frame.PartitionLengths[i] = length;
            partitionOffset += length;
        }

    }

    /// <summary>Parses segmentation, skip, luma, subblock, and chroma modes.</summary>
    private static void ParseModes(ref Vp8BooleanReader reader, FrameState frame)
    {
        byte[] above = new byte[frame.MacroblockWidth * 4];
        Span<byte> left = stackalloc byte[4];
        for (int mbY = 0; mbY < frame.MacroblockHeight; mbY++)
        {
            left.Clear();
            for (int mbX = 0; mbX < frame.MacroblockWidth; mbX++)
            {
                int macroblock = (mbY * frame.MacroblockWidth) + mbX;
                if (frame.UpdateSegmentMap)
                {
                    frame.Segments[macroblock] = reader.ReadBit(frame.SegmentProbabilities[0]) == 0
                        ? (byte)reader.ReadBit(frame.SegmentProbabilities[1])
                        : (byte)(2 + reader.ReadBit(frame.SegmentProbabilities[2]));
                }

                frame.Skipped[macroblock] = frame.UseSkipProbability && reader.ReadBit(frame.SkipProbability) != 0;
                int yMode;
                if (reader.ReadBit(145) == 0)
                {
                    yMode = BlockMode;
                    for (int blockY = 0; blockY < 4; blockY++)
                    {
                        int leftMode = left[blockY];
                        for (int blockX = 0; blockX < 4; blockX++)
                        {
                            int probabilityOffset = ((above[(mbX * 4) + blockX] * 10) + leftMode) * 9;
                            int mode = ReadBlockMode(ref reader, Vp8Tables.KeyFrameModes.Slice(probabilityOffset, 9));
                            frame.BlockModes[(macroblock * 16) + (blockY * 4) + blockX] = (byte)mode;
                            above[(mbX * 4) + blockX] = (byte)mode;
                            leftMode = mode;
                        }

                        left[blockY] = (byte)leftMode;
                    }
                }
                else if (reader.ReadBit(156) == 0)
                {
                    yMode = reader.ReadBit(163) == 0 ? DcMode : VerticalMode;
                    SetDerivedModes(frame, macroblock, above, left, mbX, yMode);
                }
                else
                {
                    yMode = reader.ReadBit(128) == 0 ? HorizontalMode : TrueMotionMode;
                    SetDerivedModes(frame, macroblock, above, left, mbX, yMode);
                }

                frame.YModes[macroblock] = (byte)yMode;
                frame.UvModes[macroblock] = reader.ReadBit(142) == 0
                    ? (byte)DcMode
                    : reader.ReadBit(114) == 0
                        ? (byte)VerticalMode
                        : reader.ReadBit(183) == 0 ? (byte)HorizontalMode : (byte)TrueMotionMode;
            }
        }
    }

    /// <summary>Updates subblock contexts for a macroblock with one luma mode.</summary>
    private static void SetDerivedModes(FrameState frame, int macroblock, byte[] above, Span<byte> left, int mbX, int yMode)
    {
        byte blockMode = yMode switch { VerticalMode => 2, HorizontalMode => 3, TrueMotionMode => 1, _ => 0 };
        frame.BlockModes.AsSpan(macroblock * 16, 16).Fill(blockMode);
        above.AsSpan(mbX * 4, 4).Fill(blockMode);
        left.Fill(blockMode);
    }

    /// <summary>Decodes one value from the normative 4x4 luma mode tree.</summary>
    private static int ReadBlockMode(ref Vp8BooleanReader reader, ReadOnlySpan<byte> p)
    {
        if (reader.ReadBit(p[0]) == 0) return 0;
        if (reader.ReadBit(p[1]) == 0) return 1;
        if (reader.ReadBit(p[2]) == 0) return 2;
        if (reader.ReadBit(p[3]) == 0)
        {
            if (reader.ReadBit(p[4]) == 0) return 3;
            return reader.ReadBit(p[5]) == 0 ? 4 : 5;
        }

        if (reader.ReadBit(p[6]) == 0) return 6;
        if (reader.ReadBit(p[7]) == 0) return 7;
        return reader.ReadBit(p[8]) == 0 ? 8 : 9;
    }

    /// <summary>Decodes residual partitions and reconstructs macroblocks in raster order.</summary>
    private static void DecodePartitions(byte* payload, int payloadLength, FrameState frame)
    {
        Vp8BooleanReader[] readers = new Vp8BooleanReader[frame.PartitionCount];
        for (int i = 0; i < readers.Length; i++)
        {
            int offset = frame.PartitionOffsets[i];
            if (offset < 0 || offset > payloadLength - frame.PartitionLengths[i]) ThrowHelper.InvalidData("A VP8 token partition leaves its payload.");
            readers[i] = new Vp8BooleanReader(payload + offset, frame.PartitionLengths[i]);
        }

        byte[] aboveY = new byte[frame.MacroblockWidth * 4];
        byte[] aboveU = new byte[frame.MacroblockWidth * 2];
        byte[] aboveV = new byte[frame.MacroblockWidth * 2];
        byte[] aboveY2 = new byte[frame.MacroblockWidth];
        Span<byte> leftY = stackalloc byte[4];
        Span<byte> leftU = stackalloc byte[2];
        Span<byte> leftV = stackalloc byte[2];
        Span<int> walshScratch = stackalloc int[16];
        Span<int> wht = stackalloc int[16];
        Quantizers[] segmentQuantizers = new Quantizers[4];
        for (int i = 0; i < segmentQuantizers.Length; i++) segmentQuantizers[i] = frame.GetQuantizers(i);
        int coefficientsPerRow = checked(frame.MacroblockWidth * 25 * 16);
        int endsPerRow = checked(frame.MacroblockWidth * 25);
        int[][] coefficientRows = [new int[coefficientsPerRow], new int[coefficientsPerRow]];
        byte[][] coefficientEndRows = [new byte[endsPerRow], new byte[endsPerRow]];
        bool pipeline = ParallelExecution.ShouldRun(checked(frame.Width * (long)frame.Height), 1_000_000);
        Task? reconstruction = null;

        try
        {
            for (int mbY = 0; mbY < frame.MacroblockHeight; mbY++)
            {
                int slot = pipeline ? mbY & 1 : 0;
                int[] rowCoefficients = coefficientRows[slot];
                byte[] rowEnds = coefficientEndRows[slot];
                Array.Clear(rowCoefficients, 0, coefficientsPerRow);
                Array.Clear(rowEnds, 0, endsPerRow);
                leftY.Clear(); leftU.Clear(); leftV.Clear();
                byte leftY2 = 0;
                ref Vp8BooleanReader reader = ref readers[mbY & (frame.PartitionCount - 1)];
                for (int mbX = 0; mbX < frame.MacroblockWidth; mbX++)
                {
                    Span<int> coefficients = rowCoefficients.AsSpan(mbX * 25 * 16, 25 * 16);
                    Span<byte> coefficientEnds = rowEnds.AsSpan(mbX * 25, 25);
                    int macroblock = (mbY * frame.MacroblockWidth) + mbX;
                    Quantizers quantizers = segmentQuantizers[frame.Segments[macroblock]];
                    bool hasY2 = frame.YModes[macroblock] != BlockMode;

                    if (!frame.Skipped[macroblock])
                    {
                        if (hasY2)
                        {
                            int last = DecodeCoefficients(ref reader, frame.CoefficientProbabilities, 1, aboveY2[mbX] + leftY2, 0, quantizers.Y2Dc, quantizers.Y2Ac, coefficients.Slice(24 * 16, 16));
                            coefficientEnds[24] = (byte)last;
                            leftY2 = aboveY2[mbX] = (byte)(last > 0 ? 1 : 0);
                            InverseWalsh(coefficients.Slice(24 * 16, 16), wht, walshScratch);
                            for (int i = 0; i < 16; i++) coefficients[i * 16] = wht[i];
                        }

                        int first = hasY2 ? 1 : 0;
                        int type = hasY2 ? 0 : 3;
                        for (int blockY = 0; blockY < 4; blockY++)
                        {
                            byte left = leftY[blockY];
                            for (int blockX = 0; blockX < 4; blockX++)
                            {
                                int contextIndex = (mbX * 4) + blockX;
                                int block = (blockY * 4) + blockX;
                                int last = DecodeCoefficients(ref reader, frame.CoefficientProbabilities, type, left + aboveY[contextIndex], first, quantizers.Y1Dc, quantizers.Y1Ac, coefficients.Slice(block * 16, 16));
                                coefficientEnds[block] = (byte)last;
                                left = aboveY[contextIndex] = (byte)(last > first ? 1 : 0);
                            }

                            leftY[blockY] = left;
                        }

                        for (int channel = 0; channel < 2; channel++)
                        {
                            Span<byte> leftContexts = channel == 0 ? leftU : leftV;
                            byte[] aboveContexts = channel == 0 ? aboveU : aboveV;
                            int blockBase = channel == 0 ? 16 : 20;
                            for (int blockY = 0; blockY < 2; blockY++)
                            {
                                byte left = leftContexts[blockY];
                                for (int blockX = 0; blockX < 2; blockX++)
                                {
                                    int contextIndex = (mbX * 2) + blockX;
                                    int block = blockBase + (blockY * 2) + blockX;
                                    int last = DecodeCoefficients(ref reader, frame.CoefficientProbabilities, 2, left + aboveContexts[contextIndex], 0, quantizers.UvDc, quantizers.UvAc, coefficients.Slice(block * 16, 16));
                                    coefficientEnds[block] = (byte)last;
                                    left = aboveContexts[contextIndex] = (byte)(last > 0 ? 1 : 0);
                                }

                                leftContexts[blockY] = left;
                            }
                        }
                    }
                    else
                    {
                        aboveY.AsSpan(mbX * 4, 4).Clear(); aboveU.AsSpan(mbX * 2, 2).Clear(); aboveV.AsSpan(mbX * 2, 2).Clear();
                        leftY.Clear(); leftU.Clear(); leftV.Clear();
                        if (hasY2) leftY2 = aboveY2[mbX] = 0;
                    }
                }

                if (pipeline)
                {
                    reconstruction?.GetAwaiter().GetResult();
                    int row = mbY;
                    reconstruction = Task.Run(() => ReconstructMacroblockRow(frame, row, rowCoefficients, rowEnds));
                }
                else
                {
                    ReconstructMacroblockRow(frame, mbY, rowCoefficients, rowEnds);
                }
            }

            reconstruction?.GetAwaiter().GetResult();
        }
        finally
        {
            if (reconstruction is { IsCompleted: false }) reconstruction.GetAwaiter().GetResult();
        }

        // Filtering is applied by a dedicated in-place pass after all rows.
        if (frame.FilterLevel != 0) ApplyLoopFilter(frame);
    }

    /// <summary>Reconstructs one macroblock row after its ordered token stream has been decoded.</summary>
    private static void ReconstructMacroblockRow(FrameState frame, int mbY, int[] rowCoefficients, byte[] rowEnds)
    {
        Span<int> transformScratch = stackalloc int[16];
        for (int mbX = 0; mbX < frame.MacroblockWidth; mbX++)
        {
            int macroblock = (mbY * frame.MacroblockWidth) + mbX;
            ReconstructMacroblock(
                frame,
                mbX,
                mbY,
                macroblock,
                rowCoefficients.AsSpan(mbX * 25 * 16, 25 * 16),
                rowEnds.AsSpan(mbX * 25, 25),
                transformScratch);
        }
    }

    /// <summary>Decodes and dequantizes one VP8 coefficient block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int DecodeCoefficients(ref Vp8BooleanReader reader, byte[] probabilities, int type, int initialContext, int first, int dc, int ac, Span<int> destination)
    {
        int position = first;
        int context = initialContext;
        int typeOffset = type * 264;
        ReadOnlySpan<ushort> bandOffsets = CoefficientProbabilityBands;
        ref byte probabilityBase = ref MemoryMarshal.GetArrayDataReference(probabilities);
        while (position < 16)
        {
            int probabilityOffset = typeOffset + bandOffsets[position] + (context * 11);
            if (reader.ReadBit(Unsafe.Add(ref probabilityBase, probabilityOffset)) == 0) return position;
            while (reader.ReadBit(Unsafe.Add(ref probabilityBase, probabilityOffset + 1)) == 0)
            {
                if (++position == 16) return 16;
                context = 0;
                probabilityOffset = typeOffset + bandOffsets[position];
            }

            int magnitude;
            if (reader.ReadBit(Unsafe.Add(ref probabilityBase, probabilityOffset + 2)) == 0)
            {
                magnitude = 1;
                context = 1;
            }
            else
            {
                magnitude = DecodeLargeCoefficient(ref reader, ref probabilityBase, probabilityOffset);
                context = 2;
            }

            if (reader.ReadBit() != 0) magnitude = -magnitude;
            destination[Vp8Tables.ZigZag[position]] = magnitude * (position == 0 ? dc : ac);
            position++;
        }

        return 16;
    }

    /// <summary>Decodes coefficient magnitudes two through 2048.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeLargeCoefficient(ref Vp8BooleanReader reader, ref byte probabilities, int offset)
    {
        if (reader.ReadBit(Unsafe.Add(ref probabilities, offset + 3)) == 0)
        {
            return reader.ReadBit(Unsafe.Add(ref probabilities, offset + 4)) == 0
                ? 2
                : 3 + reader.ReadBit(Unsafe.Add(ref probabilities, offset + 5));
        }

        if (reader.ReadBit(Unsafe.Add(ref probabilities, offset + 6)) == 0)
        {
            return reader.ReadBit(Unsafe.Add(ref probabilities, offset + 7)) == 0
                ? 5 + reader.ReadBit(159)
                : 7 + (reader.ReadBit(165) << 1) + reader.ReadBit(145);
        }

        int high = reader.ReadBit(Unsafe.Add(ref probabilities, offset + 8));
        int category = (high << 1) + reader.ReadBit(Unsafe.Add(ref probabilities, offset + 9 + high));
        ReadOnlySpan<byte> categoryProbabilities = category switch { 0 => Category3, 1 => Category4, 2 => Category5, _ => Category6 };
        int value = 0;
        foreach (byte probability in categoryProbabilities) value = (value << 1) + reader.ReadBit(probability);
        return value + 3 + (8 << category);
    }

    /// <summary>Reconstructs predictors and adds inverse transforms for one macroblock.</summary>
    private static void ReconstructMacroblock(FrameState frame, int mbX, int mbY, int macroblock, Span<int> coefficients, ReadOnlySpan<byte> coefficientEnds, Span<int> scratch)
    {
        int yOriginX = mbX * 16;
        int yOriginY = mbY * 16;
        if (frame.YModes[macroblock] == BlockMode)
        {
            Span<byte> topRight = stackalloc byte[4];
            for (int i = 0; i < 4; i++)
            {
                topRight[i] = mbY == 0
                    ? (byte)127
                    : frame.Y[((yOriginY - 1) * frame.YStride) + Math.Min(yOriginX + 16 + i, frame.YStride - 1)];
            }
            for (int blockY = 0; blockY < 4; blockY++)
            {
                for (int blockX = 0; blockX < 4; blockX++)
                {
                    int block = (blockY * 4) + blockX;
                    Predict4x4(frame.Y, frame.YStride, yOriginX + (blockX * 4), yOriginY + (blockY * 4), frame.BlockModes[(macroblock * 16) + block], topRight, blockX == 3);
                    InverseDctAdd(coefficients.Slice(block * 16, 16), coefficientEnds[block] > 1, frame.Y, frame.YStride, yOriginX + (blockX * 4), yOriginY + (blockY * 4), scratch);
                }
            }
        }
        else
        {
            PredictBlock(frame.Y, frame.YStride, yOriginX, yOriginY, 16, frame.YModes[macroblock]);
            for (int blockY = 0; blockY < 4; blockY++)
            {
                for (int blockX = 0; blockX < 4; blockX++)
                {
                    int block = (blockY * 4) + blockX;
                    InverseDctAdd(coefficients.Slice(block * 16, 16), coefficientEnds[block] > 1, frame.Y, frame.YStride, yOriginX + (blockX * 4), yOriginY + (blockY * 4), scratch);
                }
            }
        }

        int uvOriginX = mbX * 8;
        int uvOriginY = mbY * 8;
        PredictBlock(frame.U, frame.UvStride, uvOriginX, uvOriginY, 8, frame.UvModes[macroblock]);
        PredictBlock(frame.V, frame.UvStride, uvOriginX, uvOriginY, 8, frame.UvModes[macroblock]);
        for (int channel = 0; channel < 2; channel++)
        {
            NativePlane plane = channel == 0 ? frame.U : frame.V;
            int blockBase = channel == 0 ? 16 : 20;
            for (int blockY = 0; blockY < 2; blockY++)
            {
                for (int blockX = 0; blockX < 2; blockX++)
                {
                    int block = blockBase + (blockY * 2) + blockX;
                    InverseDctAdd(coefficients.Slice(block * 16, 16), coefficientEnds[block] > 1, plane, frame.UvStride, uvOriginX + (blockX * 4), uvOriginY + (blockY * 4), scratch);
                }
            }
        }
    }

    /// <summary>Builds a DC, vertical, horizontal, or true-motion square predictor.</summary>
    private static void PredictBlock(NativePlane plane, int stride, int originX, int originY, int size, int mode)
    {
        bool hasTop = originY > 0;
        bool hasLeft = originX > 0;
        if (mode == DcMode)
        {
            int value;
            if (hasTop || hasLeft)
            {
                int sum = 0;
                if (hasTop) for (int x = 0; x < size; x++) sum += plane[((originY - 1) * stride) + originX + x];
                if (hasLeft) for (int y = 0; y < size; y++) sum += plane[((originY + y) * stride) + originX - 1];
                int shift = hasTop && hasLeft ? (size == 16 ? 5 : 4) : (size == 16 ? 4 : 3);
                value = (sum + (1 << (shift - 1))) >> shift;
            }
            else value = 128;
            FillBlock(plane, stride, originX, originY, size, (byte)value);
            return;
        }

        if (mode == VerticalMode)
        {
            if (hasTop)
            {
                ReadOnlySpan<byte> top = plane.Span.Slice(((originY - 1) * stride) + originX, size);
                for (int y = 0; y < size; y++) top.CopyTo(plane.Span.Slice(((originY + y) * stride) + originX, size));
            }
            else
            {
                FillBlock(plane, stride, originX, originY, size, 127);
            }
            return;
        }

        if (mode == HorizontalMode)
        {
            for (int y = 0; y < size; y++)
            {
                byte left = hasLeft ? plane[((originY + y) * stride) + originX - 1] : (byte)129;
                plane.Span.Slice(((originY + y) * stride) + originX, size).Fill(left);
            }
            return;
        }

        int topLeft = hasTop && hasLeft ? plane[((originY - 1) * stride) + originX - 1] : hasTop ? 129 : 127;
        for (int y = 0; y < size; y++)
        {
            int left = hasLeft ? plane[((originY + y) * stride) + originX - 1] : 129;
            for (int x = 0; x < size; x++)
            {
                int top = hasTop ? plane[((originY - 1) * stride) + originX + x] : 127;
                plane[((originY + y) * stride) + originX + x] = ClampByte(left + top - topLeft);
            }
        }
    }

    /// <summary>Builds one independently predicted 4x4 luma block.</summary>
    private static void Predict4x4(NativePlane plane, int stride, int originX, int originY, int mode, ReadOnlySpan<byte> macroblockTopRight, bool rightEdge)
    {
        Span<byte> top = stackalloc byte[8];
        Span<byte> left = stackalloc byte[4];
        int topLeft = originY == 0 ? 127 : originX == 0 ? 129 : plane[((originY - 1) * stride) + originX - 1];
        for (int i = 0; i < 8; i++) top[i] = originY == 0 ? (byte)127 : rightEdge && i >= 4 ? macroblockTopRight[i - 4] : plane[((originY - 1) * stride) + Math.Min(originX + i, stride - 1)];
        for (int i = 0; i < 4; i++) left[i] = originX == 0 ? (byte)129 : plane[((originY + i) * stride) + originX - 1];
        Span<byte> prediction = stackalloc byte[16];
        Build4x4Prediction(prediction, mode, topLeft, top, left);
        for (int y = 0; y < 4; y++) prediction.Slice(y * 4, 4).CopyTo(plane.Span.Slice(((originY + y) * stride) + originX, 4));
    }

    /// <summary>Implements the ten normative VP8 subblock predictors.</summary>
    private static void Build4x4Prediction(Span<byte> b, int mode, int p, ReadOnlySpan<byte> a, ReadOnlySpan<byte> l)
    {
        int a0=a[0],a1=a[1],a2=a[2],a3=a[3],a4=a[4],a5=a[5],a6=a[6],a7=a[7];
        int l0=l[0],l1=l[1],l2=l[2],l3=l[3];
        int A(int i) => i switch { <=0=>a0,1=>a1,2=>a2,3=>a3,4=>a4,5=>a5,6=>a6,_=>a7 };
        int L(int i) => i switch { <=0=>l0,1=>l1,2=>l2,_=>l3 };
        if (mode == 0)
        {
            int sum = 4;
            for (int i = 0; i < 4; i++) sum += a[i] + l[i];
            b.Fill((byte)(sum >> 3));
            return;
        }

        if (mode == 1) { for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) b[(y * 4) + x] = ClampByte(L(y) + A(x) - p); return; }
        if (mode == 2) { for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++) b[(y * 4) + x] = (byte)Average3(x == 0 ? p : A(x - 1), A(x), A(x + 1)); return; }
        if (mode == 3) { for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) b[(y * 4) + x] = (byte)Average3(y == 0 ? p : L(y - 1), L(y), L(y + 1)); return; }
        if (mode == 6) { for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) b[(y * 4) + x] = (byte)Average3(A(x + y), A(x + y + 1), A(x + y + 2)); return; }

        Span<int> e = stackalloc int[9] { L(3), L(2), L(1), L(0), p, A(0), A(1), A(2), A(3) };
        Span<int> values = stackalloc int[16];
        if (mode == 4)
        {
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) values[(y * 4) + x] = Average3(e[3 - y + x], e[4 - y + x], e[5 - y + x]);
        }
        else if (mode == 5)
        {
            values[0]=Average2(e[4],e[5]); values[1]=Average2(e[5],e[6]); values[2]=Average2(e[6],e[7]); values[3]=Average2(e[7],e[8]);
            values[4]=Average3(e[3],e[4],e[5]); values[5]=Average3(e[4],e[5],e[6]); values[6]=Average3(e[5],e[6],e[7]); values[7]=Average3(e[6],e[7],e[8]);
            values[8]=Average3(e[2],e[3],e[4]); values[9]=values[0]; values[10]=values[1]; values[11]=values[2];
            values[12]=Average3(e[1],e[2],e[3]); values[13]=values[4]; values[14]=values[5]; values[15]=values[6];
        }
        else if (mode == 7)
        {
            values[0]=Average2(A(0),A(1)); values[1]=Average2(A(1),A(2)); values[2]=Average2(A(2),A(3)); values[3]=Average2(A(3),A(4));
            values[4]=Average3(A(0),A(1),A(2)); values[5]=Average3(A(1),A(2),A(3)); values[6]=Average3(A(2),A(3),A(4)); values[7]=Average3(A(3),A(4),A(5));
            values[8]=values[1]; values[9]=values[2]; values[10]=values[3]; values[11]=Average3(A(4),A(5),A(6));
            values[12]=values[5]; values[13]=values[6]; values[14]=values[7]; values[15]=Average3(A(5),A(6),A(7));
        }
        else if (mode == 8)
        {
            values[0]=Average2(e[3],e[4]); values[1]=Average3(e[3],e[4],e[5]); values[2]=Average3(e[4],e[5],e[6]); values[3]=Average3(e[5],e[6],e[7]);
            values[4]=Average2(e[2],e[3]); values[5]=Average3(e[2],e[3],e[4]); values[6]=values[0]; values[7]=values[1];
            values[8]=Average2(e[1],e[2]); values[9]=Average3(e[1],e[2],e[3]); values[10]=values[4]; values[11]=values[5];
            values[12]=Average2(e[0],e[1]); values[13]=Average3(e[0],e[1],e[2]); values[14]=values[8]; values[15]=values[9];
        }
        else
        {
            int p0=Average2(L(0),L(1)),p1=Average3(L(0),L(1),L(2)),p2=Average2(L(1),L(2)),p3=Average3(L(1),L(2),L(3)),p4=Average2(L(2),L(3)),p5=Average3(L(2),L(3),L(3)),p6=L(3);
            values[0]=p0;values[1]=p1;values[2]=p2;values[3]=p3;values[4]=p2;values[5]=p3;values[6]=p4;values[7]=p5;values[8]=p4;values[9]=p5;values[10]=p6;values[11]=p6;values[12]=p6;values[13]=p6;values[14]=p6;values[15]=p6;
        }

        for (int i = 0; i < 16; i++) b[i] = (byte)values[i];
    }

    /// <summary>Applies a dequantized inverse Walsh transform.</summary>
    private static void InverseWalsh(ReadOnlySpan<int> input, Span<int> output, Span<int> temporary)
    {
        for (int i = 0; i < 4; i++)
        {
            int a=input[i]+input[i+12],b=input[i+4]+input[i+8],c=input[i+4]-input[i+8],d=input[i]-input[i+12];
            temporary[i]=a+b;temporary[i+4]=c+d;temporary[i+8]=a-b;temporary[i+12]=d-c;
        }
        for (int y=0;y<4;y++)
        {
            int o=y*4,a=temporary[o]+temporary[o+3],b=temporary[o+1]+temporary[o+2],c=temporary[o+1]-temporary[o+2],d=temporary[o]-temporary[o+3];
            output[o]=(a+b+3)>>3;output[o+1]=(c+d+3)>>3;output[o+2]=(a-b+3)>>3;output[o+3]=(d-c+3)>>3;
        }
    }

    /// <summary>Adds the normative integer inverse DCT to one predicted block.</summary>
    private static void InverseDctAdd(ReadOnlySpan<int> input, bool hasAc, NativePlane plane, int stride, int originX, int originY, Span<int> temporary)
    {
        if (!hasAc)
        {
            int delta = (input[0] + 4) >> 3;
            if (delta == 0) return;
            for (int y = 0; y < 4; y++)
            {
                int row = ((originY + y) * stride) + originX;
                plane[row] = ClampByte(plane[row] + delta);
                plane[row + 1] = ClampByte(plane[row + 1] + delta);
                plane[row + 2] = ClampByte(plane[row + 2] + delta);
                plane[row + 3] = ClampByte(plane[row + 3] + delta);
            }

            return;
        }

        for(int i=0;i<4;i++)
        {
            int a=input[i]+input[i+8],b=input[i]-input[i+8],c=Multiply2(input[i+4])-Multiply1(input[i+12]),d=Multiply1(input[i+4])+Multiply2(input[i+12]);
            temporary[i*4]=a+d;temporary[i*4+1]=b+c;temporary[i*4+2]=b-c;temporary[i*4+3]=a-d;
        }
        for(int y=0;y<4;y++)
        {
            int a=temporary[y]+temporary[y+8]+4,b=temporary[y]-temporary[y+8]+4,c=Multiply2(temporary[y+4])-Multiply1(temporary[y+12]),d=Multiply1(temporary[y+4])+Multiply2(temporary[y+12]),row=((originY+y)*stride)+originX;
            plane[row]=ClampByte(plane[row]+((a+d)>>3));plane[row+1]=ClampByte(plane[row+1]+((b+c)>>3));plane[row+2]=ClampByte(plane[row+2]+((b-c)>>3));plane[row+3]=ClampByte(plane[row+3]+((a-d)>>3));
        }
    }

    /// <summary>Applies a bounded in-place deblocking filter.</summary>
    private static void ApplyLoopFilter(FrameState frame)
    {
        FilterPlane(frame.Y,frame.YStride,frame.YHeight,16,frame.FilterLevel,frame.SimpleFilter);
        if(!frame.SimpleFilter){FilterPlane(frame.U,frame.UvStride,frame.UvHeight,8,frame.FilterLevel,false);FilterPlane(frame.V,frame.UvStride,frame.UvHeight,8,frame.FilterLevel,false);}
    }

    /// <summary>Filters macroblock edges without allocating another frame.</summary>
    private static void FilterPlane(NativePlane plane,int stride,int height,int blockSize,int level,bool simple)
    {
        int threshold=Math.Max(1,(level*2)+(simple?0:4));
        for(int y=0;y<height;y++)for(int x=blockSize;x<stride;x+=blockSize)FilterEdge(plane,(y*stride)+x,1,threshold);
        for(int y=blockSize;y<height;y+=blockSize)for(int x=0;x<stride;x++)FilterEdge(plane,(y*stride)+x,stride,threshold);
    }

    /// <summary>Filters one p1,p0|q0,q1 edge.</summary>
    private static void FilterEdge(NativePlane plane,int q0,int step,int threshold)
    {
        int p0=plane[q0-step],q=plane[q0],p1=plane[q0-(2*step)],q1=plane[q0+step];
        if((Math.Abs(p0-q)*2)+(Math.Abs(p1-q1)/2)>threshold)return;
        int delta=Math.Clamp((((q-p0)*3)+(p1-q1)+4)>>3,-16,15);plane[q0-step]=ClampByte(p0+delta);plane[q0]=ClampByte(q-delta);
    }

    /// <summary>Decodes and inverse-filters a WebP alpha plane.</summary>
    private static void ApplyAlpha(FrameState frame,ReadOnlySpan<byte> alpha)
    {
        if(alpha.IsEmpty)return;
        int header=alpha[0],compression=header&3,filter=(header>>2)&3;
        if((header&0xC0)!=0)ThrowHelper.InvalidData("The WebP ALPH header has reserved bits set.");
        if(compression is not 0 and not 1)ThrowHelper.InvalidData("The WebP ALPH compression method is invalid.");
        int count=checked(frame.Width*frame.Height);
        if(compression==0)
        {
            if(alpha.Length-1<count)ThrowHelper.InvalidData("The WebP ALPH plane is truncated.");
            frame.Alpha=new byte[count];alpha.Slice(1,count).CopyTo(frame.Alpha);
        }
        else
        {
            frame.Alpha=Vp8LosslessDecoder.DecodeAlpha(alpha[1..],frame.Width,frame.Height);
        }
        for(int y=0;y<frame.Height;y++)for(int x=0;x<frame.Width;x++)
        {
            int i=(y*frame.Width)+x,left=x==0?0:frame.Alpha[i-1],above=y==0?0:frame.Alpha[i-frame.Width],upperLeft=x==0||y==0?0:frame.Alpha[i-frame.Width-1];
            int predictor=filter switch{1=>x==0?above:left,2=>y==0?left:above,3=>Math.Clamp(left+above-upperLeft,0,255),_=>0};frame.Alpha[i]=(byte)(frame.Alpha[i]+predictor);
        }
    }

    /// <summary>Converts padded YUV420 storage directly to RGB24 or RGBA32.</summary>
    private static PixelBuffer ConvertToPixelBuffer(FrameState frame, bool isOpaque)
    {
        PixelBuffer result = new(frame.Width, frame.Height, isOpaque ? 3 : 4);
        try
        {
            byte[]? alpha = frame.Alpha;
            bool parallel = ParallelExecution.ShouldRun((long)frame.Width * frame.Height, 1_000_000);
            ParallelExecution.For(0, frame.Height, parallel, y =>
            {
                Span<byte> row = result.GetRowSpan(y);
                int yOffset = y * frame.YStride;
                int uvOffset = (y >> 1) * frame.UvStride;
                int alphaOffset = y * frame.Width;
                if (isOpaque)
                {
                    for (int x = 0; x < frame.Width; x += 2)
                    {
                        int uv = uvOffset + (x >> 1), u = frame.U[uv] - 128, v = frame.V[uv] - 128;
                        WriteYuvRgb(row, x * 3, frame.Y[yOffset + x], u, v);
                        if (x + 1 < frame.Width) WriteYuvRgb(row, (x + 1) * 3, frame.Y[yOffset + x + 1], u, v);
                    }
                }
                else
                {
                    for (int x = 0; x < frame.Width; x += 2)
                    {
                        int uv = uvOffset + (x >> 1), u = frame.U[uv] - 128, v = frame.V[uv] - 128;
                        WriteYuvRgba(row, x * 4, frame.Y[yOffset + x], u, v, alpha![alphaOffset + x]);
                        if (x + 1 < frame.Width) WriteYuvRgba(row, (x + 1) * 4, frame.Y[yOffset + x + 1], u, v, alpha[alphaOffset + x + 1]);
                    }
                }
            }, maximumDegreeOfParallelism: 4);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>Converts one normative limited-range YUV sample to RGB24.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteYuvRgb(Span<byte> row, int output, int y, int u, int v)
    {
        int c = Math.Max(0, y - 16);
        row[output] = ClampByte((298 * c + 409 * v + 128) >> 8);
        row[output + 1] = ClampByte((298 * c - 100 * u - 208 * v + 128) >> 8);
        row[output + 2] = ClampByte((298 * c + 516 * u + 128) >> 8);
    }

    /// <summary>Converts one normative limited-range YUV sample to RGBA32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteYuvRgba(Span<byte> row, int output, int y, int u, int v, byte alpha)
    {
        WriteYuvRgb(row, output, y, u, v);
        row[output + 3] = alpha;
    }

    /// <summary>Proves opacity for an explicit WebP alpha plane.</summary>
    private static bool AlphaIsOpaque(ReadOnlySpan<byte> alpha)
    {
        foreach (byte value in alpha)
        {
            if (value != 255) return false;
        }

        return true;
    }

    /// <summary>Fills a square plane region.</summary>
    private static void FillBlock(NativePlane plane,int stride,int x,int y,int size,byte value){for(int r=0;r<size;r++)plane.Span.Slice(((y+r)*stride)+x,size).Fill(value);}
    [MethodImpl(MethodImplOptions.AggressiveInlining)]private static int Average2(int a,int b)=>(a+b+1)>>1;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]private static int Average3(int a,int b,int c)=>(a+(b<<1)+c+2)>>2;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]private static int Multiply1(int value)=>value+((value*20091)>>16);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]private static int Multiply2(int value)=>(value*35468)>>16;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]private static byte ClampByte(int value)=>(byte)Math.Clamp(value,0,255);

    /// <summary>Stores one segment's six dequantization factors.</summary>
    private readonly struct Quantizers(int y1Dc,int y1Ac,int y2Dc,int y2Ac,int uvDc,int uvAc)
    {
        public int Y1Dc{get;}=y1Dc;public int Y1Ac{get;}=y1Ac;public int Y2Dc{get;}=y2Dc;public int Y2Ac{get;}=y2Ac;public int UvDc{get;}=uvDc;public int UvAc{get;}=uvAc;
    }

    /// <summary>Owns parsed VP8 state and padded reconstruction planes.</summary>
    private sealed class FrameState : IDisposable
    {
        public FrameState(int width,int height)
        {
            Width=width;Height=height;MacroblockWidth=(width+15)/16;MacroblockHeight=(height+15)/16;YStride=MacroblockWidth*16;YHeight=MacroblockHeight*16;UvStride=MacroblockWidth*8;UvHeight=MacroblockHeight*8;
            int count=checked(MacroblockWidth*MacroblockHeight);Y=new NativePlane(checked(YStride*YHeight));U=new NativePlane(checked(UvStride*UvHeight));V=new NativePlane(checked(UvStride*UvHeight));YModes=new byte[count];UvModes=new byte[count];Segments=new byte[count];Skipped=new bool[count];BlockModes=new byte[checked(count*16)];CoefficientProbabilities=Vp8Tables.CreateCoefficientProbabilities();PartitionOffsets=new int[8];PartitionLengths=new int[8];
        }

        public int Width,Height,MacroblockWidth,MacroblockHeight,YStride,YHeight,UvStride,UvHeight,PartitionCount,BaseQuantizer,Y1DcDelta,Y2DcDelta,Y2AcDelta,UvDcDelta,UvAcDelta,SkipProbability,FilterLevel,FilterSharpness;
        public bool SegmentationEnabled,UpdateSegmentMap,SegmentAbsolute,UseSkipProbability,SimpleFilter;
        public int[] SegmentQuantizers=new int[4],SegmentFilters=new int[4],PartitionOffsets,PartitionLengths;
        public byte[] SegmentProbabilities=[255,255,255],CoefficientProbabilities,YModes,UvModes,Segments,BlockModes;
        public NativePlane Y,U,V;
        public bool[] Skipped;public byte[]? Alpha;

        public void Dispose()
        {
            Y.Dispose(); U.Dispose(); V.Dispose();
        }

        public Quantizers GetQuantizers(int segment)
        {
            int q=SegmentationEnabled?(SegmentAbsolute?SegmentQuantizers[segment]:BaseQuantizer+SegmentQuantizers[segment]):BaseQuantizer;q=Math.Clamp(q,0,127);
            int y1dc=Vp8Tables.DcQuantizers[Math.Clamp(q+Y1DcDelta,0,127)],y1ac=Vp8Tables.AcQuantizers[q],y2dc=Vp8Tables.DcQuantizers[Math.Clamp(q+Y2DcDelta,0,127)]*2,y2ac=Math.Max(8,(Vp8Tables.AcQuantizers[Math.Clamp(q+Y2AcDelta,0,127)]*155)/100),uvdc=Vp8Tables.DcQuantizers[Math.Clamp(q+UvDcDelta,0,117)],uvac=Vp8Tables.AcQuantizers[Math.Clamp(q+UvAcDelta,0,127)];
            return new(y1dc,y1ac,y2dc,y2ac,uvdc,uvac);
        }
    }

    /// <summary>Owns one zeroed, 32-byte-aligned VP8 reconstruction plane.</summary>
    private sealed class NativePlane : IDisposable
    {
        private byte* pointer;
        private readonly int length;

        public NativePlane(int length)
        {
            this.length = length;
            pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, 32);
            if (pointer is null) throw new OutOfMemoryException();
            NativeMemory.Clear(pointer, (nuint)length);
        }

        ~NativePlane() => Release();

        public Span<byte> Span => new(pointer, length);

        public ref byte this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref pointer[index];
        }

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release()
        {
            byte* value = pointer;
            pointer = null;
            if (value is not null) NativeMemory.AlignedFree(value);
        }
    }
}
