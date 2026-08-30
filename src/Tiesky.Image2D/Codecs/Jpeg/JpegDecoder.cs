using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Tiesky.Image2D.Internal;
using Tiesky.Image2D.Processing;

namespace Tiesky.Image2D.Codecs.Jpeg;

/// <summary>Decodes eight-bit Huffman baseline and progressive JPEG images.</summary>
internal static unsafe class JpegDecoder
{
    private static readonly Vector128<byte> RgbRedLow = Vector128.Create(
        (byte)0, 0x80, 0x80, 1, 0x80, 0x80, 2, 0x80, 0x80, 3, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbGreenLow = Vector128.Create(
        (byte)0x80, 0, 0x80, 0x80, 1, 0x80, 0x80, 2, 0x80, 0x80, 3, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbBlueLow = Vector128.Create(
        (byte)0x80, 0x80, 0, 0x80, 0x80, 1, 0x80, 0x80, 2, 0x80, 0x80, 3, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbRedHigh = Vector128.Create(
        (byte)4, 0x80, 0x80, 5, 0x80, 0x80, 6, 0x80, 0x80, 7, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbGreenHigh = Vector128.Create(
        (byte)0x80, 4, 0x80, 0x80, 5, 0x80, 0x80, 6, 0x80, 0x80, 7, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbBlueHigh = Vector128.Create(
        (byte)0x80, 0x80, 4, 0x80, 0x80, 5, 0x80, 0x80, 6, 0x80, 0x80, 7, 0x80, 0x80, 0x80, 0x80);

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

    /// <summary>Decodes one JPEG byte sequence.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> data, long maximumPixels, DecodeRequest request)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            ThrowHelper.InvalidData("The JPEG start marker is invalid.");
        }

        ushort[][] quantization = new ushort[4][];
        JpegHuffmanTable?[] dcTables = new JpegHuffmanTable?[4];
        JpegHuffmanTable?[] acTables = new JpegHuffmanTable?[4];
        JpegFrame? frame = null;
        ExifOrientation orientation = ExifOrientation.Normal;
        int adobeTransform = -1;
        int restartInterval = 0;
        int position = 2;
        bool sawEnd = false;

        try
        {
            while (position < data.Length)
            {
                int marker = ReadMarker(data, ref position);
                if (marker == 0xD9)
                {
                    sawEnd = true;
                    break;
                }

                if (marker is >= 0xD0 and <= 0xD7 or 0x01 or 0xD8)
                {
                    ThrowHelper.InvalidData("A standalone JPEG marker appears outside entropy data.");
                }

                BinaryPrimitivesEx.Ensure(data, position, 2);
                int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                if (segmentLength < 2)
                {
                    ThrowHelper.InvalidData("A JPEG segment length is invalid.");
                }

                BinaryPrimitivesEx.Ensure(data, position + 2, segmentLength - 2);
                ReadOnlySpan<byte> payload = data.Slice(position + 2, segmentLength - 2);
                position += segmentLength;

                switch (marker)
                {
                    case 0xC0:
                    case 0xC2:
                        if (frame is not null)
                        {
                            ThrowHelper.InvalidData("A JPEG stream contains multiple frame headers.");
                        }

                        frame = ParseFrame(payload, marker == 0xC2, maximumPixels, request, orientation);
                        break;
                    case 0xC4:
                        ParseHuffmanTables(payload, dcTables, acTables);
                        break;
                    case 0xDB:
                        ParseQuantizationTables(payload, quantization);
                        break;
                    case 0xDD:
                        if (payload.Length != 2)
                        {
                            ThrowHelper.InvalidData("The JPEG restart interval is invalid.");
                        }

                        restartInterval = BinaryPrimitives.ReadUInt16BigEndian(payload);
                        break;
                    case 0xDA:
                        if (frame is null)
                        {
                            ThrowHelper.InvalidData("A JPEG scan precedes its frame header.");
                        }

                        JpegScan scan = ParseScan(payload, frame);
                        DecodeScan(data, ref position, frame, scan, quantization, dcTables, acTables, restartInterval);
                        break;
                    case 0xE1:
                        orientation = ExifOrientationReader.Read(payload);
                        break;
                    case 0xEE:
                        if (payload.Length >= 12 && payload[..5].SequenceEqual("Adobe"u8))
                        {
                            adobeTransform = payload[11];
                        }

                        break;
                    case 0xC1:
                    case 0xC3:
                    case 0xC5:
                    case 0xC6:
                    case 0xC7:
                    case 0xC9:
                    case 0xCA:
                    case 0xCB:
                    case 0xCD:
                    case 0xCE:
                    case 0xCF:
                        ThrowHelper.Unsupported("Only eight-bit Huffman baseline and progressive JPEG frames are supported.");
                        break;
                    default:
                        // APPn and COM payloads are metadata and are intentionally discarded.
                        break;
                }
            }

            if (!sawEnd || frame is null || frame.ScanCount == 0)
            {
                ThrowHelper.InvalidData("The JPEG stream is incomplete.");
            }

            if (frame.Progressive)
            {
                FinishProgressive(frame, quantization);
            }

            PixelBuffer pixels = ConvertToRgba(frame, adobeTransform);
            return new DecodedImage(pixels, orientation, frame.OriginalWidth, frame.OriginalHeight, isOpaque: true);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    /// <summary>Reads a marker prefix while tolerating legal 0xFF fill bytes.</summary>
    private static int ReadMarker(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length || data[position++] != 0xFF)
        {
            ThrowHelper.InvalidData("A JPEG marker prefix is missing.");
        }

        while (position < data.Length && data[position] == 0xFF)
        {
            position++;
        }

        if (position >= data.Length || data[position] == 0)
        {
            ThrowHelper.InvalidData("A JPEG marker is invalid.");
        }

        return data[position++];
    }

    /// <summary>Parses and allocates one SOF0 or SOF2 frame.</summary>
    private static JpegFrame ParseFrame(ReadOnlySpan<byte> payload, bool progressive, long maximumPixels, DecodeRequest request, ExifOrientation orientation)
    {
        if (payload.Length < 9 || payload[0] != 8)
        {
            ThrowHelper.Unsupported("Only eight-bit JPEG sample precision is supported.");
        }

        int height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
        int componentCount = payload[5];
        if (componentCount is not (1 or 3 or 4) || payload.Length != 6 + componentCount * 3)
        {
            ThrowHelper.Unsupported("JPEG images must contain one, three, or four components.");
        }

        ThrowHelper.ValidateDimensions(width, height, maximumPixels);
        int reduction = CalculateReduction(width, height, progressive, componentCount, request, orientation);
        JpegFrame frame = new(width, height, progressive, componentCount, reduction);
        try
        {
            for (int i = 0; i < componentCount; i++)
            {
                int offset = 6 + i * 3;
                int horizontal = payload[offset + 1] >> 4;
                int vertical = payload[offset + 1] & 15;
                int quantizationId = payload[offset + 2];
                if (horizontal is < 1 or > 4 || vertical is < 1 or > 4 || quantizationId > 3)
                {
                    ThrowHelper.Unsupported("JPEG sampling factors or quantization selector are unsupported.");
                }

                frame.Components[i] = new JpegComponent(payload[offset], horizontal, vertical, quantizationId);
            }

            frame.Initialize();
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    /// <summary>Selects the largest native reduction that leaves a 25% sampling margin.</summary>
    private static int CalculateReduction(int width, int height, bool progressive, int componentCount, DecodeRequest request, ExifOrientation orientation)
    {
        if (progressive || componentCount == 4 || request.Resize is null)
        {
            return 1;
        }

        CoordinateTransform coordinates = new(width, height, orientation, request.Rotation);
        ResizePlan plan = ResizePlan.Create(coordinates.Width, coordinates.Height, request.Resize);
        foreach (int reduction in new[] { 8, 4, 2 })
        {
            if (plan.SourceWidth / reduction >= plan.Width * 1.25 && plan.SourceHeight / reduction >= plan.Height * 1.25)
            {
                return reduction;
            }
        }

        return 1;
    }

    /// <summary>Parses one or more DQT tables and converts zig-zag order to natural order.</summary>
    private static void ParseQuantizationTables(ReadOnlySpan<byte> payload, ushort[][] tables)
    {
        int offset = 0;
        while (offset < payload.Length)
        {
            int selector = payload[offset++];
            int precision = selector >> 4;
            int id = selector & 15;
            if (precision > 1 || id > 3)
            {
                ThrowHelper.InvalidData("A JPEG quantization table selector is invalid.");
            }

            int bytes = precision == 0 ? 64 : 128;
            BinaryPrimitivesEx.Ensure(payload, offset, bytes);
            ushort[] table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                ushort value = precision == 0 ? payload[offset + i] : BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + i * 2, 2));
                if (value == 0)
                {
                    ThrowHelper.InvalidData("JPEG quantization values must be nonzero.");
                }

                table[ZigZag[i]] = value;
            }

            tables[id] = table;
            offset += bytes;
        }
    }

    /// <summary>Parses one or more canonical DHT tables.</summary>
    private static void ParseHuffmanTables(ReadOnlySpan<byte> payload, JpegHuffmanTable?[] dcTables, JpegHuffmanTable?[] acTables)
    {
        int offset = 0;
        while (offset < payload.Length)
        {
            BinaryPrimitivesEx.Ensure(payload, offset, 17);
            int selector = payload[offset++];
            int tableClass = selector >> 4;
            int id = selector & 15;
            if (tableClass > 1 || id > 3)
            {
                ThrowHelper.InvalidData("A JPEG Huffman table selector is invalid.");
            }

            ReadOnlySpan<byte> counts = payload.Slice(offset, 16);
            offset += 16;
            int symbolCount = 0;
            foreach (byte count in counts)
            {
                symbolCount += count;
            }

            if (symbolCount == 0 || symbolCount > 256)
            {
                ThrowHelper.InvalidData("A JPEG Huffman table has an invalid symbol count.");
            }

            BinaryPrimitivesEx.Ensure(payload, offset, symbolCount);
            JpegHuffmanTable table = new(counts, payload.Slice(offset, symbolCount));
            (tableClass == 0 ? dcTables : acTables)[id] = table;
            offset += symbolCount;
        }
    }

    /// <summary>Parses one SOS header and binds entropy tables to frame components.</summary>
    private static JpegScan ParseScan(ReadOnlySpan<byte> payload, JpegFrame frame)
    {
        if (payload.Length < 6)
        {
            ThrowHelper.InvalidData("The JPEG scan header is too short.");
        }

        int count = payload[0];
        if (count < 1 || count > frame.Components.Length || payload.Length != 1 + count * 2 + 3)
        {
            ThrowHelper.InvalidData("The JPEG scan component count is invalid.");
        }

        JpegComponent[] components = new JpegComponent[count];
        for (int i = 0; i < count; i++)
        {
            int offset = 1 + i * 2;
            JpegComponent component = frame.FindComponent(payload[offset]);
            component.DcTableId = payload[offset + 1] >> 4;
            component.AcTableId = payload[offset + 1] & 15;
            if (component.DcTableId > 3 || component.AcTableId > 3 || Array.IndexOf(components, component, 0, i) >= 0)
            {
                ThrowHelper.InvalidData("A JPEG scan component selector is invalid.");
            }

            components[i] = component;
        }

        int spectralStart = payload[1 + count * 2];
        int spectralEnd = payload[2 + count * 2];
        int approximation = payload[3 + count * 2];
        int high = approximation >> 4;
        int low = approximation & 15;
        if (!frame.Progressive && (spectralStart != 0 || spectralEnd != 63 || high != 0 || low != 0))
        {
            ThrowHelper.InvalidData("A baseline JPEG scan has progressive parameters.");
        }

        if (frame.Progressive && (spectralStart > spectralEnd || spectralEnd > 63 || high > 13 || low > 13 || (spectralStart == 0 && spectralEnd != 0) || (spectralStart != 0 && count != 1)))
        {
            ThrowHelper.InvalidData("The progressive JPEG scan parameters are invalid.");
        }

        return new JpegScan(components, spectralStart, spectralEnd, high, low);
    }

    /// <summary>Decodes all MCUs in one sequential or progressive scan.</summary>
    private static void DecodeScan(
        ReadOnlySpan<byte> data,
        ref int position,
        JpegFrame frame,
        JpegScan scan,
        ushort[][] quantization,
        JpegHuffmanTable?[] dcTables,
        JpegHuffmanTable?[] acTables,
        int restartInterval)
    {
        if (!frame.Progressive)
        {
            DecodeBaselineScan(data, ref position, frame, scan, quantization, dcTables, acTables, restartInterval);
            return;
        }

        JpegEntropyReader reader = new(data, position);
        int[] predictors = new int[frame.Components.Length];
        int eobRun = 0;
        int restartIndex = 0;
        int unitCount = scan.Components.Length == 1
            ? scan.Components[0].VisibleBlocksWide * scan.Components[0].VisibleBlocksHigh
            : frame.McuColumns * frame.McuRows;

        for (int unit = 0; unit < unitCount; unit++)
        {
            if (scan.Components.Length == 1)
            {
                JpegComponent component = scan.Components[0];
                int blockX = unit % component.VisibleBlocksWide;
                int blockY = unit / component.VisibleBlocksWide;
                DecodeBlock(ref reader, frame, scan, component, blockX, blockY, predictors, ref eobRun, quantization, dcTables, acTables);
            }
            else
            {
                int mcuX = unit % frame.McuColumns;
                int mcuY = unit / frame.McuColumns;
                foreach (JpegComponent component in scan.Components)
                {
                    for (int vertical = 0; vertical < component.VerticalSampling; vertical++)
                    {
                        for (int horizontal = 0; horizontal < component.HorizontalSampling; horizontal++)
                        {
                            int blockX = mcuX * component.HorizontalSampling + horizontal;
                            int blockY = mcuY * component.VerticalSampling + vertical;
                            DecodeBlock(ref reader, frame, scan, component, blockX, blockY, predictors, ref eobRun, quantization, dcTables, acTables);
                        }
                    }
                }
            }

            if (restartInterval != 0 && (unit + 1) % restartInterval == 0 && unit + 1 < unitCount)
            {
                reader.ConsumeRestart(restartIndex);
                restartIndex = (restartIndex + 1) & 7;
                Array.Clear(predictors);
                eobRun = 0;
            }
        }

        position = reader.FinishScan();
        frame.ScanCount++;
    }

    /// <summary>
    /// Entropy-decodes baseline coefficients in bounded ordered batches, then transforms
    /// the independent blocks in parallel. The entropy reader and DC predictors never
    /// cross a worker boundary.
    /// </summary>
    private static void DecodeBaselineScan(
        ReadOnlySpan<byte> data,
        ref int position,
        JpegFrame frame,
        JpegScan scan,
        ushort[][] quantization,
        JpegHuffmanTable?[] dcTables,
        JpegHuffmanTable?[] acTables,
        int restartInterval)
    {
        const int MaximumCoefficientBytes = 1 * 1024 * 1024;
        JpegEntropyReader reader = new(data, position);
        int[] predictors = new int[frame.Components.Length];
        int[] frameComponentIndices = new int[scan.Components.Length];
        JpegHuffmanTable[] boundDcTables = new JpegHuffmanTable[scan.Components.Length];
        JpegHuffmanTable[] boundAcTables = new JpegHuffmanTable[scan.Components.Length];
        ushort[][] boundQuantization = new ushort[scan.Components.Length][];
        for (int componentIndex = 0; componentIndex < scan.Components.Length; componentIndex++)
        {
            JpegComponent component = scan.Components[componentIndex];
            frameComponentIndices[componentIndex] = Array.IndexOf(frame.Components, component);
            boundDcTables[componentIndex] = dcTables[component.DcTableId] ??
                throw new Image2DException(ImageErrorCode.InvalidData, "A referenced JPEG DC Huffman table is missing.");
            boundAcTables[componentIndex] = acTables[component.AcTableId] ??
                throw new Image2DException(ImageErrorCode.InvalidData, "A referenced JPEG AC Huffman table is missing.");
            boundQuantization[componentIndex] = quantization[component.QuantizationId] ??
                throw new Image2DException(ImageErrorCode.InvalidData, "A referenced JPEG quantization table is missing.");
        }

        int restartIndex = 0;
        bool nonInterleaved = scan.Components.Length == 1;
        int unitCount = nonInterleaved
            ? scan.Components[0].VisibleBlocksWide * scan.Components[0].VisibleBlocksHigh
            : frame.McuColumns * frame.McuRows;
        int blocksPerUnit = nonInterleaved
            ? 1
            : scan.Components.Sum(component => component.HorizontalSampling * component.VerticalSampling);
        int coefficientsPerUnit = checked(blocksPerUnit * 64);
        int unitsPerBatch = Math.Max(1, Math.Min(unitCount, MaximumCoefficientBytes / checked(coefficientsPerUnit * sizeof(int))));
        bool parallel = ParallelExecution.ShouldRun((long)frame.Width * frame.Height * blocksPerUnit, 2_000_000);
        int bufferLength = checked(unitsPerBatch * coefficientsPerUnit);
        int[][] coefficientBuffers = parallel
            ? [ArrayPool<int>.Shared.Rent(bufferLength), ArrayPool<int>.Shared.Rent(bufferLength)]
            : [ArrayPool<int>.Shared.Rent(bufferLength)];
        Task pendingTransform = Task.CompletedTask;

        try
        {
            int batchNumber = 0;
            for (int batchStart = 0; batchStart < unitCount; batchStart += unitsPerBatch, batchNumber++)
            {
                int[] coefficients = coefficientBuffers[batchNumber % coefficientBuffers.Length];
                int batchCount = Math.Min(unitsPerBatch, unitCount - batchStart);
                Array.Clear(coefficients, 0, batchCount * coefficientsPerUnit);
                for (int localUnit = 0; localUnit < batchCount; localUnit++)
                {
                    int unit = batchStart + localUnit;
                    int coefficientOffset = localUnit * coefficientsPerUnit;
                    if (nonInterleaved)
                    {
                        JpegComponent component = scan.Components[0];
                        DecodeBaselineCoefficients(
                            ref reader,
                            coefficients.AsSpan(coefficientOffset, 64),
                            frameComponentIndices[0],
                            predictors,
                            boundDcTables[0],
                            boundAcTables[0]);
                    }
                    else
                    {
                        int block = 0;
                        for (int componentIndex = 0; componentIndex < scan.Components.Length; componentIndex++)
                        {
                            JpegComponent component = scan.Components[componentIndex];
                            int componentBlocks = component.HorizontalSampling * component.VerticalSampling;
                            for (int componentBlock = 0; componentBlock < componentBlocks; componentBlock++)
                            {
                                DecodeBaselineCoefficients(
                                    ref reader,
                                    coefficients.AsSpan(coefficientOffset + block * 64, 64),
                                    frameComponentIndices[componentIndex],
                                    predictors,
                                    boundDcTables[componentIndex],
                                    boundAcTables[componentIndex]);
                                block++;
                            }
                        }
                    }

                    if (restartInterval != 0 && (unit + 1) % restartInterval == 0 && unit + 1 < unitCount)
                    {
                        reader.ConsumeRestart(restartIndex);
                        restartIndex = (restartIndex + 1) & 7;
                        Array.Clear(predictors);
                    }
                }

                pendingTransform.GetAwaiter().GetResult();
                if (parallel && batchCount > 1)
                {
                    int capturedBatchStart = batchStart;
                    int capturedBatchCount = batchCount;
                    int[] capturedCoefficients = coefficients;
                    pendingTransform = Task.Run(() => ParallelExecution.For(0, capturedBatchCount, parallel: true, localUnit =>
                        TransformBaselineUnit(frame, scan, boundQuantization, capturedCoefficients, coefficientsPerUnit, capturedBatchStart + localUnit, localUnit)));
                }
                else
                {
                    ParallelExecution.For(0, batchCount, parallel: false, localUnit =>
                        TransformBaselineUnit(frame, scan, boundQuantization, coefficients, coefficientsPerUnit, batchStart + localUnit, localUnit));
                }
            }

            position = reader.FinishScan();
            pendingTransform.GetAwaiter().GetResult();
            frame.ScanCount++;
        }
        finally
        {
            if (!pendingTransform.IsCompleted)
            {
                pendingTransform.GetAwaiter().GetResult();
            }

            foreach (int[] buffer in coefficientBuffers)
            {
                ArrayPool<int>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Decodes one baseline block without performing its independent IDCT.</summary>
    private static void DecodeBaselineCoefficients(
        ref JpegEntropyReader reader,
        Span<int> block,
        int componentIndex,
        int[] predictors,
        JpegHuffmanTable dc,
        JpegHuffmanTable ac)
    {
        int category = dc.DecodeWithAmplitude(ref reader, 11, out int dcAmplitude);
        if (category > 11)
        {
            ThrowHelper.InvalidData("A JPEG DC coefficient category is invalid.");
        }

        predictors[componentIndex] += Extend(dcAmplitude, category);
        block[0] = predictors[componentIndex];
        DecodeSequentialAc(ref reader, ac, block);
    }

    /// <summary>Transforms every block belonging to one baseline scan unit.</summary>
    private static void TransformBaselineUnit(
        JpegFrame frame,
        JpegScan scan,
        ushort[][] boundQuantization,
        int[] coefficients,
        int coefficientsPerUnit,
        int unit,
        int localUnit)
    {
        int coefficientOffset = localUnit * coefficientsPerUnit;
        if (scan.Components.Length == 1)
        {
            JpegComponent component = scan.Components[0];
            int blockX = unit % component.VisibleBlocksWide;
            int blockY = unit / component.VisibleBlocksWide;
            TransformBaselineBlock(component, boundQuantization[0], coefficients.AsSpan(coefficientOffset, 64), blockX, blockY, frame.Reduction);
            return;
        }

        int mcuX = unit % frame.McuColumns;
        int mcuY = unit / frame.McuColumns;
        int block = 0;
        for (int componentIndex = 0; componentIndex < scan.Components.Length; componentIndex++)
        {
            JpegComponent component = scan.Components[componentIndex];
            for (int vertical = 0; vertical < component.VerticalSampling; vertical++)
            for (int horizontal = 0; horizontal < component.HorizontalSampling; horizontal++)
            {
                int blockX = mcuX * component.HorizontalSampling + horizontal;
                int blockY = mcuY * component.VerticalSampling + vertical;
                TransformBaselineBlock(component, boundQuantization[componentIndex], coefficients.AsSpan(coefficientOffset + block * 64, 64), blockX, blockY, frame.Reduction);
                block++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransformBaselineBlock(
        JpegComponent component,
        ReadOnlySpan<ushort> quantization,
        ReadOnlySpan<int> coefficients,
        int blockX,
        int blockY,
        int reduction)
    {
        JpegIdct.TransformScaled(coefficients, quantization, component.Plane, component.PlaneWidth, blockX * component.BlockSize, blockY * component.BlockSize, reduction);
    }

    /// <summary>Decodes one block according to scan progression state.</summary>
    private static void DecodeBlock(
        ref JpegEntropyReader reader,
        JpegFrame frame,
        JpegScan scan,
        JpegComponent component,
        int blockX,
        int blockY,
        int[] predictors,
        ref int eobRun,
        ushort[][] quantization,
        JpegHuffmanTable?[] dcTables,
        JpegHuffmanTable?[] acTables)
    {
        JpegHuffmanTable? dc = null;
        JpegHuffmanTable? ac = null;
        if (!frame.Progressive || (scan.SpectralStart == 0 && scan.HighApproximation == 0))
        {
            dc = dcTables[component.DcTableId] ?? throw new Image2DException(ImageErrorCode.InvalidData, "A referenced JPEG DC Huffman table is missing.");
        }

        if (!frame.Progressive || scan.SpectralStart != 0)
        {
            ac = acTables[component.AcTableId] ?? throw new Image2DException(ImageErrorCode.InvalidData, "A referenced JPEG AC Huffman table is missing.");
        }
        int componentIndex = Array.IndexOf(frame.Components, component);
        if (!frame.Progressive)
        {
            Span<int> block = stackalloc int[64];
            int category = dc!.Decode(ref reader);
            if (category > 11)
            {
                ThrowHelper.InvalidData("A JPEG DC coefficient category is invalid.");
            }

            predictors[componentIndex] += Receive(ref reader, category);
            block[0] = predictors[componentIndex];
            DecodeSequentialAc(ref reader, ac!, block);
            ushort[] table = quantization[component.QuantizationId] ?? throw new Image2DException(ImageErrorCode.InvalidData, "A referenced JPEG quantization table is missing.");
            JpegIdct.TransformScaled(block, table, component.Plane, component.PlaneWidth, blockX * component.BlockSize, blockY * component.BlockSize, frame.Reduction);
            return;
        }

        Span<int> coefficients = component.GetCoefficientBlock(blockX, blockY);
        if (scan.SpectralStart == 0)
        {
            if (scan.HighApproximation == 0)
            {
                int category = dc!.Decode(ref reader);
                if (category > 11)
                {
                    ThrowHelper.InvalidData("A progressive JPEG DC category is invalid.");
                }

                predictors[componentIndex] += Receive(ref reader, category);
                coefficients[0] = predictors[componentIndex] << scan.LowApproximation;
            }
            else
            {
                coefficients[0] |= reader.ReadBit() << scan.LowApproximation;
            }
        }
        else if (scan.HighApproximation == 0)
        {
            DecodeProgressiveAcFirst(ref reader, ac!, coefficients, scan.SpectralStart, scan.SpectralEnd, scan.LowApproximation, ref eobRun);
        }
        else
        {
            DecodeProgressiveAcRefine(ref reader, ac!, coefficients, scan.SpectralStart, scan.SpectralEnd, scan.LowApproximation, ref eobRun);
        }
    }

    /// <summary>Decodes baseline run-length AC coefficients.</summary>
    private static void DecodeSequentialAc(ref JpegEntropyReader reader, JpegHuffmanTable table, scoped Span<int> block)
    {
        int coefficient = 1;
        while (coefficient < 64)
        {
            int value = table.DecodeWithAmplitude(ref reader, 10, out int amplitude);
            int run = value >> 4;
            int size = value & 15;
            if (size == 0)
            {
                if (run == 0)
                {
                    return;
                }

                if (run != 15)
                {
                    ThrowHelper.InvalidData("A JPEG AC run is invalid.");
                }

                coefficient += 16;
                continue;
            }

            coefficient += run;
            if (coefficient >= 64 || size > 10)
            {
                ThrowHelper.InvalidData("A JPEG AC coefficient is out of range.");
            }

            block[ZigZag[coefficient++]] = Extend(amplitude, size);
        }
    }

    /// <summary>Decodes the first transmission of a progressive AC band.</summary>
    private static void DecodeProgressiveAcFirst(ref JpegEntropyReader reader, JpegHuffmanTable table, Span<int> block, int start, int end, int low, ref int eobRun)
    {
        if (eobRun != 0)
        {
            eobRun--;
            return;
        }

        int coefficient = start;
        while (coefficient <= end)
        {
            int value = table.Decode(ref reader);
            int run = value >> 4;
            int size = value & 15;
            if (size == 0)
            {
                if (run == 15)
                {
                    coefficient += 16;
                    continue;
                }

                eobRun = (1 << run) + reader.ReadBits(run) - 1;
                return;
            }

            coefficient += run;
            if (coefficient > end)
            {
                ThrowHelper.InvalidData("A progressive JPEG AC coefficient is out of range.");
            }

            block[ZigZag[coefficient++]] = Receive(ref reader, size) << low;
        }
    }

    /// <summary>Decodes one refinement transmission of a progressive AC band.</summary>
    private static void DecodeProgressiveAcRefine(ref JpegEntropyReader reader, JpegHuffmanTable table, Span<int> block, int start, int end, int low, ref int eobRun)
    {
        int positiveBit = 1 << low;
        int negativeBit = -positiveBit;
        int coefficient = start;

        if (eobRun == 0)
        {
            while (coefficient <= end)
            {
                int value = table.Decode(ref reader);
                int run = value >> 4;
                int size = value & 15;
                int newCoefficient = 0;
                if (size == 0)
                {
                    if (run != 15)
                    {
                        eobRun = (1 << run) + reader.ReadBits(run);
                        break;
                    }

                    run = 16;
                }
                else
                {
                    if (size != 1)
                    {
                        ThrowHelper.InvalidData("A progressive JPEG refinement symbol is invalid.");
                    }

                    newCoefficient = reader.ReadBit() != 0 ? positiveBit : negativeBit;
                }

                while (coefficient <= end)
                {
                    int natural = ZigZag[coefficient];
                    int current = block[natural];
                    if (current != 0)
                    {
                        RefineExisting(ref reader, block, natural, positiveBit);
                    }
                    else if (run == 0)
                    {
                        break;
                    }
                    else
                    {
                        run--;
                    }

                    coefficient++;
                }

                if (newCoefficient != 0)
                {
                    if (coefficient > end)
                    {
                        ThrowHelper.InvalidData("A progressive JPEG refinement coefficient is out of range.");
                    }

                    block[ZigZag[coefficient++]] = newCoefficient;
                }
            }
        }

        if (eobRun > 0)
        {
            while (coefficient <= end)
            {
                int natural = ZigZag[coefficient++];
                if (block[natural] != 0)
                {
                    RefineExisting(ref reader, block, natural, positiveBit);
                }
            }

            eobRun--;
        }
    }

    /// <summary>Adds one previously absent approximation bit to a nonzero coefficient.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RefineExisting(ref JpegEntropyReader reader, Span<int> block, int index, int bit)
    {
        int value = block[index];
        if (reader.ReadBit() != 0 && (Math.Abs(value) & bit) == 0)
        {
            block[index] = value > 0 ? value + bit : value - bit;
        }
    }

    /// <summary>Extends a JPEG sign-magnitude category to a signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Receive(ref JpegEntropyReader reader, int size)
    {
        if (size == 0)
        {
            return 0;
        }

        int value = reader.ReadBits(size);
        return Extend(value, size);
    }

    /// <summary>Extends already-read JPEG sign-magnitude bits to a signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Extend(int value, int size)
    {
        if (size == 0)
        {
            return 0;
        }

        int threshold = 1 << (size - 1);
        return value < threshold ? value - ((1 << size) - 1) : value;
    }

    /// <summary>Transforms all coefficient buffers after the final progressive scan.</summary>
    private static void FinishProgressive(JpegFrame frame, ushort[][] quantization)
    {
        foreach (JpegComponent component in frame.Components)
        {
            ushort[] table = quantization[component.QuantizationId] ?? throw new Image2DException(ImageErrorCode.InvalidData, "A referenced JPEG quantization table is missing.");
            bool parallel = ParallelExecution.ShouldRun((long)component.BlocksWide * component.BlocksHigh * 64, 1_000_000);
            ParallelExecution.For(0, component.BlocksHigh, parallel, blockY =>
            {
                for (int blockX = 0; blockX < component.BlocksWide; blockX++)
                {
                    JpegIdct.Transform(component.GetCoefficientBlock(blockX, blockY), table, component.Plane, component.PlaneWidth, blockX * 8, blockY * 8);
                }
            });
        }
    }

    /// <summary>Upsamples frame components and converts their declared color transform to RGBA.</summary>
    private static PixelBuffer ConvertToRgba(JpegFrame frame, int adobeTransform)
    {
        PixelBuffer destination = new(frame.Width, frame.Height, 3);
        try
        {
            bool rgb = frame.Components.Length == 3 && (adobeTransform == 0 ||
                (frame.Components[0].Id == (byte)'R' && frame.Components[1].Id == (byte)'G' && frame.Components[2].Id == (byte)'B'));
            bool ycck = frame.Components.Length == 4 && adobeTransform == 2;
            if (!rgb && frame.Components.Length == 3 &&
                frame.Components[0].HorizontalSampling == frame.MaximumHorizontalSampling &&
                frame.Components[0].VerticalSampling == frame.MaximumVerticalSampling &&
                frame.Components[1].HorizontalSampling == frame.Components[2].HorizontalSampling &&
                frame.Components[1].VerticalSampling == frame.Components[2].VerticalSampling)
            {
                ConvertThreeComponentYcbcr(frame, destination);
                return destination;
            }

            JpegComponentSampler firstSampler = new(frame.Components[0], frame.Width, frame.Height, frame.MaximumHorizontalSampling, frame.MaximumVerticalSampling);
            JpegComponentSampler? secondSampler = frame.Components.Length > 1
                ? new(frame.Components[1], frame.Width, frame.Height, frame.MaximumHorizontalSampling, frame.MaximumVerticalSampling)
                : null;
            JpegComponentSampler? thirdSampler = frame.Components.Length > 2
                ? new(frame.Components[2], frame.Width, frame.Height, frame.MaximumHorizontalSampling, frame.MaximumVerticalSampling)
                : null;
            JpegComponentSampler? fourthSampler = frame.Components.Length > 3
                ? new(frame.Components[3], frame.Width, frame.Height, frame.MaximumHorizontalSampling, frame.MaximumVerticalSampling)
                : null;

            bool parallel = ParallelExecution.ShouldRun((long)frame.Width * frame.Height, 1_000_000);
            ParallelExecution.For(0, frame.Height, parallel, y =>
            {
                Span<byte> converted = stackalloc byte[3];
                Span<byte> row = destination.GetRowSpan(y);
                for (int x = 0; x < frame.Width; x++)
                {
                    int output = x * 3;
                    int first = firstSampler.Sample(x, y);
                    if (frame.Components.Length == 1)
                    {
                        row[output] = (byte)first;
                        row[output + 1] = (byte)first;
                        row[output + 2] = (byte)first;
                    }
                    else if (frame.Components.Length == 3)
                    {
                        int second = secondSampler!.Sample(x, y);
                        int third = thirdSampler!.Sample(x, y);
                        if (rgb)
                        {
                            row[output] = (byte)first;
                            row[output + 1] = (byte)second;
                            row[output + 2] = (byte)third;
                        }
                        else
                        {
                            ConvertYcbcr(first, second, third, row, output);
                        }
                    }
                    else
                    {
                        int second = secondSampler!.Sample(x, y);
                        int third = thirdSampler!.Sample(x, y);
                        int key = fourthSampler!.Sample(x, y);
                        if (ycck)
                        {
                            ConvertYcbcr(first, second, third, converted, 0);
                            row[output] = Multiply(converted[0], key);
                            row[output + 1] = Multiply(converted[1], key);
                            row[output + 2] = Multiply(converted[2], key);
                        }
                        else
                        {
                            // Adobe CMYK JPEG convention stores inverted components, making
                            // multiplication by the inverted key equivalent to subtractive compositing.
                            row[output] = Multiply(first, key);
                            row[output + 1] = Multiply(second, key);
                            row[output + 2] = Multiply(third, key);
                        }
                    }

                }
            });

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>Converts the ubiquitous full-resolution Y plus equally-subsampled Cb/Cr layout.</summary>
    private static void ConvertThreeComponentYcbcr(JpegFrame frame, PixelBuffer destination)
    {
        JpegComponent luminance = frame.Components[0];
        JpegComponent blueChroma = frame.Components[1];
        JpegComponent redChroma = frame.Components[2];
        if (blueChroma.HorizontalSampling == frame.MaximumHorizontalSampling &&
            blueChroma.VerticalSampling == frame.MaximumVerticalSampling)
        {
            ConvertThreeComponentYcbcr444(frame, destination, luminance, blueChroma, redChroma);
            return;
        }

        if (blueChroma.HorizontalSampling * 2 == frame.MaximumHorizontalSampling &&
            (blueChroma.VerticalSampling == frame.MaximumVerticalSampling ||
             blueChroma.VerticalSampling * 2 == frame.MaximumVerticalSampling))
        {
            ConvertThreeComponentYcbcrQuarterPhase(frame, destination, luminance, blueChroma, redChroma);
            return;
        }

        SamplingAxis horizontal = new(frame.Width, blueChroma.HorizontalSampling, frame.MaximumHorizontalSampling, blueChroma.PlaneWidth);
        SamplingAxis vertical = new(frame.Height, blueChroma.VerticalSampling, frame.MaximumVerticalSampling, blueChroma.PlaneHeight);
        int yStride = luminance.PlaneWidth;
        int chromaStride = blueChroma.PlaneWidth;

        bool parallel = ParallelExecution.ShouldRun((long)frame.Width * frame.Height, 1_000_000);
        ParallelExecution.For(0, frame.Height, parallel, y =>
        {
            Span<byte> yPlane = luminance.Plane;
            Span<byte> cbPlane = blueChroma.Plane;
            Span<byte> crPlane = redChroma.Plane;
            Span<byte> row = destination.GetRowSpan(y);
            int y0 = vertical.First[y];
            int y1 = vertical.Second[y];
            int fy = vertical.Fraction[y];
            int luminanceOffset = y * yStride;
            for (int x = 0; x < frame.Width; x++)
            {
                int x0 = horizontal.First[x];
                int x1 = horizontal.Second[x];
                int fx = horizontal.Fraction[x];
                int cb = SampleBilinear(cbPlane, chromaStride, x0, x1, y0, y1, fx, fy);
                int cr = SampleBilinear(crPlane, chromaStride, x0, x1, y0, y1, fx, fy);
                int output = x * 3;
                ConvertYcbcr(yPlane[luminanceOffset + x], cb, cr, row, output);
            }
        });
    }

    /// <summary>Converts a full-resolution 4:4:4 YCbCr layout without sampling maps.</summary>
    private static void ConvertThreeComponentYcbcr444(
        JpegFrame frame,
        PixelBuffer destination,
        JpegComponent luminance,
        JpegComponent blueChroma,
        JpegComponent redChroma)
    {
        bool parallel = ParallelExecution.ShouldRun((long)frame.Width * frame.Height, 1_000_000);
        ParallelExecution.For(0, frame.Height, parallel, y =>
        {
            Span<byte> yPlane = luminance.Plane;
            Span<byte> cbPlane = blueChroma.Plane;
            Span<byte> crPlane = redChroma.Plane;
            Span<byte> row = destination.GetRowSpan(y);
            int yOffset = y * luminance.PlaneWidth;
            int chromaOffset = y * blueChroma.PlaneWidth;
            for (int x = 0; x < frame.Width; x++)
            {
                ConvertYcbcr(yPlane[yOffset + x], cbPlane[chromaOffset + x], crPlane[chromaOffset + x], row, x * 3);
            }
        });
    }

    /// <summary>
    /// Converts 4:2:0 or 4:2:2 using the JPEG centered quarter phases directly.
    /// This is algebraically identical to the generic Q16 bilinear sampler.
    /// </summary>
    private static void ConvertThreeComponentYcbcrQuarterPhase(
        JpegFrame frame,
        PixelBuffer destination,
        JpegComponent luminance,
        JpegComponent blueChroma,
        JpegComponent redChroma)
    {
        bool verticallySubsampled = blueChroma.VerticalSampling * 2 == frame.MaximumVerticalSampling;
        bool parallel = ParallelExecution.ShouldRun((long)frame.Width * frame.Height, 1_000_000);
        ParallelExecution.For(0, frame.Height, parallel, y =>
        {
            Span<byte> yPlane = luminance.Plane;
            Span<byte> cbPlane = blueChroma.Plane;
            Span<byte> crPlane = redChroma.Plane;
            Span<byte> row = destination.GetRowSpan(y);
            int chromaY0;
            int chromaY1;
            int verticalQuarter;
            if (!verticallySubsampled)
            {
                chromaY0 = y;
                chromaY1 = y;
                verticalQuarter = 0;
            }
            else if (y == 0)
            {
                chromaY0 = 0;
                chromaY1 = 0;
                verticalQuarter = 0;
            }
            else
            {
                chromaY0 = (y - 1) >> 1;
                chromaY1 = chromaY0 + 1;
                verticalQuarter = (y & 1) == 0 ? 3 : 1;
            }

            int lastChromaY = blueChroma.PlaneHeight - 1;
            if (chromaY0 >= lastChromaY)
            {
                chromaY0 = lastChromaY;
                chromaY1 = lastChromaY;
                verticalQuarter = 0;
            }
            else if (chromaY1 > lastChromaY)
            {
                chromaY1 = lastChromaY;
            }

            int yOffset = y * luminance.PlaneWidth;
            int topOffset = chromaY0 * blueChroma.PlaneWidth;
            int bottomOffset = chromaY1 * blueChroma.PlaneWidth;
            bool vectorizedColor = frame.Width is >= 8 and <= 8192 && Avx2.IsSupported && Ssse3.IsSupported &&
                SimdPrimitives.ForcedMode is SimdMode.Automatic or SimdMode.Avx2;
            if (vectorizedColor)
            {
                Span<short> cbSamples = stackalloc short[frame.Width];
                Span<short> crSamples = stackalloc short[frame.Width];
                FillQuarterPhaseChromaRows(
                    cbPlane,
                    crPlane,
                    topOffset,
                    bottomOffset,
                    blueChroma.PlaneWidth - 1,
                    verticalQuarter,
                    cbSamples,
                    crSamples);
                ConvertYcbcrRowAvx2(yPlane.Slice(yOffset, frame.Width), cbSamples, crSamples, row);
                return;
            }

            int cb = SampleQuarterPhase(cbPlane, topOffset, bottomOffset, 0, 0, 0, verticalQuarter);
            int cr = SampleQuarterPhase(crPlane, topOffset, bottomOffset, 0, 0, 0, verticalQuarter);
            ConvertYcbcr(yPlane[yOffset], cb, cr, row, 0);

            int outputX = 1;
            int chromaX = 0;
            int lastChromaX = blueChroma.PlaneWidth - 1;
            while (outputX < frame.Width)
            {
                int firstChromaX = Math.Min(chromaX, lastChromaX);
                int secondChromaX = Math.Min(chromaX + 1, lastChromaX);
                int horizontalQuarter = firstChromaX == secondChromaX ? 0 : 1;
                cb = SampleQuarterPhase(cbPlane, topOffset, bottomOffset, firstChromaX, secondChromaX, horizontalQuarter, verticalQuarter);
                cr = SampleQuarterPhase(crPlane, topOffset, bottomOffset, firstChromaX, secondChromaX, horizontalQuarter, verticalQuarter);
                ConvertYcbcr(yPlane[yOffset + outputX], cb, cr, row, outputX * 3);
                outputX++;
                if (outputX < frame.Width)
                {
                    horizontalQuarter = firstChromaX == secondChromaX ? 0 : 3;
                    cb = SampleQuarterPhase(cbPlane, topOffset, bottomOffset, firstChromaX, secondChromaX, horizontalQuarter, verticalQuarter);
                    cr = SampleQuarterPhase(crPlane, topOffset, bottomOffset, firstChromaX, secondChromaX, horizontalQuarter, verticalQuarter);
                    ConvertYcbcr(yPlane[yOffset + outputX], cb, cr, row, outputX * 3);
                    outputX++;
                }

                chromaX++;
            }
        });
    }

    /// <summary>
    /// Reconstructs both centered half-width chroma rows in one traversal. Sharing the
    /// coordinate work keeps the exact quarter-phase arithmetic while reducing the hot
    /// 4:2:0/4:2:2 conversion loop's branches and index calculations.
    /// </summary>
    private static void FillQuarterPhaseChromaRows(
        ReadOnlySpan<byte> bluePlane,
        ReadOnlySpan<byte> redPlane,
        int topOffset,
        int bottomOffset,
        int lastChromaX,
        int verticalQuarter,
        Span<short> blueDestination,
        Span<short> redDestination)
    {
        blueDestination[0] = (short)SampleQuarterPhase(bluePlane, topOffset, bottomOffset, 0, 0, 0, verticalQuarter);
        redDestination[0] = (short)SampleQuarterPhase(redPlane, topOffset, bottomOffset, 0, 0, 0, verticalQuarter);
        int fullInteriorPairs = Math.Min((blueDestination.Length - 1) >> 1, lastChromaX);
        ref byte blueReference = ref MemoryMarshal.GetReference(bluePlane);
        ref byte redReference = ref MemoryMarshal.GetReference(redPlane);
        int chromaX = 0;
        for (; chromaX < fullInteriorPairs; chromaX++)
        {
            InterpolateQuarterPhasePair(ref blueReference, topOffset, bottomOffset, chromaX, verticalQuarter, out short blueFirst, out short blueSecond);
            InterpolateQuarterPhasePair(ref redReference, topOffset, bottomOffset, chromaX, verticalQuarter, out short redFirst, out short redSecond);
            int output = (chromaX << 1) + 1;
            blueDestination[output] = blueFirst;
            blueDestination[output + 1] = blueSecond;
            redDestination[output] = redFirst;
            redDestination[output + 1] = redSecond;
        }

        int outputX = (chromaX << 1) + 1;
        while (outputX < blueDestination.Length)
        {
            int first = Math.Min(chromaX, lastChromaX);
            int second = Math.Min(chromaX + 1, lastChromaX);
            int horizontalQuarter = first == second ? 0 : 1;
            blueDestination[outputX] = (short)SampleQuarterPhase(bluePlane, topOffset, bottomOffset, first, second, horizontalQuarter, verticalQuarter);
            redDestination[outputX] = (short)SampleQuarterPhase(redPlane, topOffset, bottomOffset, first, second, horizontalQuarter, verticalQuarter);
            outputX++;
            if (outputX < blueDestination.Length)
            {
                horizontalQuarter = first == second ? 0 : 3;
                blueDestination[outputX] = (short)SampleQuarterPhase(bluePlane, topOffset, bottomOffset, first, second, horizontalQuarter, verticalQuarter);
                redDestination[outputX] = (short)SampleQuarterPhase(redPlane, topOffset, bottomOffset, first, second, horizontalQuarter, verticalQuarter);
                outputX++;
            }

            chromaX++;
        }
    }

    /// <summary>Interpolates the 1/4 and 3/4 horizontal phases for one interior chroma pair.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterpolateQuarterPhasePair(
        ref byte plane,
        int topOffset,
        int bottomOffset,
        int chromaX,
        int verticalQuarter,
        out short first,
        out short second)
    {
        int topFirst = Unsafe.Add(ref plane, topOffset + chromaX);
        int topSecond = Unsafe.Add(ref plane, topOffset + chromaX + 1);
        int topQuarter = topFirst * 3 + topSecond;
        int topThreeQuarter = topFirst + topSecond * 3;
        if (verticalQuarter == 0)
        {
            first = (short)((topQuarter + 2) >> 2);
            second = (short)((topThreeQuarter + 2) >> 2);
            return;
        }

        int bottomFirst = Unsafe.Add(ref plane, bottomOffset + chromaX);
        int bottomSecond = Unsafe.Add(ref plane, bottomOffset + chromaX + 1);
        int bottomQuarter = bottomFirst * 3 + bottomSecond;
        int bottomThreeQuarter = bottomFirst + bottomSecond * 3;
        first = (short)((topQuarter * (4 - verticalQuarter) + bottomQuarter * verticalQuarter + 8) >> 4);
        second = (short)((topThreeQuarter * (4 - verticalQuarter) + bottomThreeQuarter * verticalQuarter + 8) >> 4);
    }

    /// <summary>Converts eight reconstructed YCbCr samples per AVX2 arithmetic iteration.</summary>
    private static void ConvertYcbcrRowAvx2(
        ReadOnlySpan<byte> luminance,
        ReadOnlySpan<short> blueChroma,
        ReadOnlySpan<short> redChroma,
        Span<byte> destination)
    {
        ref byte yReference = ref MemoryMarshal.GetReference(luminance);
        ref short cbReference = ref MemoryMarshal.GetReference(blueChroma);
        ref short crReference = ref MemoryMarshal.GetReference(redChroma);
        ref byte destinationReference = ref MemoryMarshal.GetReference(destination);
        Vector256<int> half = Vector256.Create(128);
        Vector256<int> rounding = Vector256.Create(32768);
        Vector256<int> zero = Vector256<int>.Zero;
        Vector256<int> maximum = Vector256.Create(255);
        int x = 0;
        for (; x <= luminance.Length - 8; x += 8)
        {
            ulong yBytes = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref yReference, x));
            Vector256<int> y = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(yBytes).AsByte());
            ref byte cbBytes = ref Unsafe.As<short, byte>(ref Unsafe.Add(ref cbReference, x));
            ref byte crBytes = ref Unsafe.As<short, byte>(ref Unsafe.Add(ref crReference, x));
            Vector256<int> cb = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref cbBytes)) - half;
            Vector256<int> cr = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<short>>(ref crBytes)) - half;
            Vector256<int> red = y + Avx2.ShiftRightArithmetic(Avx2.MultiplyLow(cr, Vector256.Create(91881)) + rounding, 16);
            Vector256<int> green = y - Avx2.ShiftRightArithmetic(
                Avx2.MultiplyLow(cb, Vector256.Create(22554)) + Avx2.MultiplyLow(cr, Vector256.Create(46802)) + rounding,
                16);
            Vector256<int> blue = y + Avx2.ShiftRightArithmetic(Avx2.MultiplyLow(cb, Vector256.Create(116130)) + rounding, 16);
            red = Avx2.Min(Avx2.Max(red, zero), maximum);
            green = Avx2.Min(Avx2.Max(green, zero), maximum);
            blue = Avx2.Min(Avx2.Max(blue, zero), maximum);
            Vector128<byte> redBytes = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(red.GetLower(), red.GetUpper()), Vector128<short>.Zero);
            Vector128<byte> greenBytes = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(green.GetLower(), green.GetUpper()), Vector128<short>.Zero);
            Vector128<byte> blueBytes = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(blue.GetLower(), blue.GetUpper()), Vector128<short>.Zero);
            Vector128<byte> first = Sse2.Or(
                Sse2.Or(Ssse3.Shuffle(redBytes, RgbRedLow), Ssse3.Shuffle(greenBytes, RgbGreenLow)),
                Ssse3.Shuffle(blueBytes, RgbBlueLow));
            Vector128<byte> second = Sse2.Or(
                Sse2.Or(Ssse3.Shuffle(redBytes, RgbRedHigh), Ssse3.Shuffle(greenBytes, RgbGreenHigh)),
                Ssse3.Shuffle(blueBytes, RgbBlueHigh));
            int output = x * 3;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destinationReference, output), first);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destinationReference, output + 12), second.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destinationReference, output + 20), second.AsUInt32().GetElement(2));
        }

        for (; x < luminance.Length; x++)
        {
            ConvertYcbcr(luminance[x], blueChroma[x], redChroma[x], destination, x * 3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SampleQuarterPhase(
        ReadOnlySpan<byte> plane,
        int topOffset,
        int bottomOffset,
        int first,
        int second,
        int horizontalQuarter,
        int verticalQuarter)
    {
        int top = plane[topOffset + first] * (4 - horizontalQuarter) + plane[topOffset + second] * horizontalQuarter;
        if (verticalQuarter == 0)
        {
            return (top + 2) >> 2;
        }

        int bottom = plane[bottomOffset + first] * (4 - horizontalQuarter) + plane[bottomOffset + second] * horizontalQuarter;
        return (top * (4 - verticalQuarter) + bottom * verticalQuarter + 8) >> 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SampleBilinear(ReadOnlySpan<byte> plane, int stride, int x0, int x1, int y0, int y1, int fx, int fy)
    {
        int first = plane[y0 * stride + x0];
        int top = (first << 16) + (plane[y0 * stride + x1] - first) * fx;
        if (fy == 0)
        {
            return (top + 32768) >> 16;
        }

        first = plane[y1 * stride + x0];
        int bottom = (first << 16) + (plane[y1 * stride + x1] - first) * fx;
        return (int)(((long)top * (65536 - fy) + (long)bottom * fy + (1L << 31)) >> 32);
    }

    /// <summary>Precomputes chroma reconstruction coordinates so the pixel loop uses only integer arithmetic.</summary>
    private sealed class JpegComponentSampler
    {
        private readonly JpegComponent component;
        private readonly SamplingAxis? horizontal;
        private readonly SamplingAxis? vertical;

        public JpegComponentSampler(JpegComponent component, int width, int height, int maximumHorizontal, int maximumVertical)
        {
            this.component = component;
            horizontal = component.HorizontalSampling == maximumHorizontal
                ? null
                : new SamplingAxis(width, component.HorizontalSampling, maximumHorizontal, component.PlaneWidth);
            vertical = component.VerticalSampling == maximumVertical
                ? null
                : new SamplingAxis(height, component.VerticalSampling, maximumVertical, component.PlaneHeight);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Sample(int x, int y)
        {
            int x0 = horizontal is null ? x : horizontal.First[x];
            int x1 = horizontal is null ? x : horizontal.Second[x];
            int y0 = vertical is null ? y : vertical.First[y];
            int y1 = vertical is null ? y : vertical.Second[y];
            int fx = horizontal is null ? 0 : horizontal.Fraction[x];
            int fy = vertical is null ? 0 : vertical.Fraction[y];
            Span<byte> plane = component.Plane;
            int stride = component.PlaneWidth;

            int top = (plane[y0 * stride + x0] << 16) +
                (plane[y0 * stride + x1] - plane[y0 * stride + x0]) * fx;
            if (fy == 0)
            {
                return (top + 32768) >> 16;
            }

            int bottom = (plane[y1 * stride + x0] << 16) +
                (plane[y1 * stride + x1] - plane[y1 * stride + x0]) * fx;
            return (int)(((long)top * (65536 - fy) + (long)bottom * fy + (1L << 31)) >> 32);
        }
    }

    /// <summary>Maps destination coordinates to two source samples and one Q16 fraction.</summary>
    private sealed class SamplingAxis
    {
        public SamplingAxis(int destinationLength, int sampling, int maximumSampling, int sourceLength)
        {
            First = new int[destinationLength];
            Second = new int[destinationLength];
            Fraction = new ushort[destinationLength];
            int last = sourceLength - 1;
            for (int coordinate = 0; coordinate < destinationLength; coordinate++)
            {
                long source = ((((long)(2 * coordinate + 1) * sampling) << 15) / maximumSampling) - 32768;
                int first = (int)(source >> 16);
                int fraction = (int)(source - ((long)first << 16));
                if (first < 0)
                {
                    first = 0;
                    fraction = 0;
                }
                else if (first >= last)
                {
                    first = last;
                    fraction = 0;
                }

                First[coordinate] = first;
                Second[coordinate] = fraction == 0 ? first : first + 1;
                Fraction[coordinate] = (ushort)fraction;
            }
        }

        public int[] First { get; }
        public int[] Second { get; }
        public ushort[] Fraction { get; }
    }

    /// <summary>Converts full-range JPEG YCbCr to clamped RGB.</summary>
    private static void ConvertYcbcr(int y, int cb, int cr, Span<byte> destination, int offset)
    {
        cb -= 128;
        cr -= 128;
        destination[offset] = Clamp(y + ((91881 * cr + 32768) >> 16));
        destination[offset + 1] = Clamp(y - ((22554 * cb + 46802 * cr + 32768) >> 16));
        destination[offset + 2] = Clamp(y + ((116130 * cb + 32768) >> 16));
    }

    /// <summary>Multiplies two inverted CMYK channels with rounding.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Multiply(int value, int key) => (byte)((value * key + 127) / 255);

    /// <summary>Clamps an integer channel to a byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>Owns frame geometry and native component buffers.</summary>
    private sealed class JpegFrame : IDisposable
    {
        /// <summary>Initializes an unallocated frame.</summary>
        public JpegFrame(int width, int height, bool progressive, int componentCount, int reduction)
        {
            OriginalWidth = width;
            OriginalHeight = height;
            Reduction = reduction;
            Width = (width + reduction - 1) / reduction;
            Height = (height + reduction - 1) / reduction;
            Progressive = progressive;
            Components = new JpegComponent[componentCount];
        }

        public int Width { get; }
        public int Height { get; }
        public int OriginalWidth { get; }
        public int OriginalHeight { get; }
        public int Reduction { get; }
        public bool Progressive { get; }
        public JpegComponent[] Components { get; }
        public int MaximumHorizontalSampling { get; private set; }
        public int MaximumVerticalSampling { get; private set; }
        public int McuColumns { get; private set; }
        public int McuRows { get; private set; }
        public int ScanCount { get; set; }

        /// <summary>Computes MCU geometry and allocates padded planes.</summary>
        public void Initialize()
        {
            MaximumHorizontalSampling = Components.Max(component => component.HorizontalSampling);
            MaximumVerticalSampling = Components.Max(component => component.VerticalSampling);
            if (Components.Sum(component => component.HorizontalSampling * component.VerticalSampling) > 10)
            {
                ThrowHelper.Unsupported("The JPEG sampling-factor sum exceeds the supported limit.");
            }

            McuColumns = (OriginalWidth + MaximumHorizontalSampling * 8 - 1) / (MaximumHorizontalSampling * 8);
            McuRows = (OriginalHeight + MaximumVerticalSampling * 8 - 1) / (MaximumVerticalSampling * 8);
            HashSet<byte> ids = new();
            foreach (JpegComponent component in Components)
            {
                if (!ids.Add(component.Id))
                {
                    ThrowHelper.InvalidData("JPEG component identifiers must be unique.");
                }

                component.Allocate(OriginalWidth, OriginalHeight, MaximumHorizontalSampling, MaximumVerticalSampling, McuColumns, McuRows, Progressive, Reduction);
            }
        }

        /// <summary>Finds a component by its SOS identifier.</summary>
        public JpegComponent FindComponent(byte id)
        {
            foreach (JpegComponent component in Components)
            {
                if (component.Id == id)
                {
                    return component;
                }
            }

            ThrowHelper.InvalidData("A JPEG scan references an unknown component.");
            return null!;
        }

        /// <summary>Releases all native component buffers.</summary>
        public void Dispose()
        {
            foreach (JpegComponent? component in Components)
            {
                component?.Dispose();
            }
        }
    }

    /// <summary>Owns one padded sample plane and optional progressive coefficients.</summary>
    private sealed class JpegComponent : IDisposable
    {
        private byte* plane;
        private int* coefficients;
        private int planeLength;
        private int coefficientLength;

        /// <summary>Initializes component sampling metadata.</summary>
        public JpegComponent(byte id, int horizontalSampling, int verticalSampling, int quantizationId)
        {
            Id = id;
            HorizontalSampling = horizontalSampling;
            VerticalSampling = verticalSampling;
            QuantizationId = quantizationId;
        }

        public byte Id { get; }
        public int HorizontalSampling { get; }
        public int VerticalSampling { get; }
        public int QuantizationId { get; }
        public int DcTableId { get; set; }
        public int AcTableId { get; set; }
        public int BlocksWide { get; private set; }
        public int BlocksHigh { get; private set; }
        public int VisibleBlocksWide { get; private set; }
        public int VisibleBlocksHigh { get; private set; }
        public int BlockSize { get; private set; }
        public int PlaneWidth => BlocksWide * BlockSize;
        public int PlaneHeight => BlocksHigh * BlockSize;
        public Span<byte> Plane => new(plane, planeLength);

        /// <summary>Allocates native buffers after frame sampling maxima are known.</summary>
        public void Allocate(int width, int height, int maximumHorizontal, int maximumVertical, int mcuColumns, int mcuRows, bool progressive, int reduction)
        {
            BlockSize = 8 / reduction;
            BlocksWide = checked(mcuColumns * HorizontalSampling);
            BlocksHigh = checked(mcuRows * VerticalSampling);
            VisibleBlocksWide = checked((width * HorizontalSampling + maximumHorizontal * 8 - 1) / (maximumHorizontal * 8));
            VisibleBlocksHigh = checked((height * VerticalSampling + maximumVertical * 8 - 1) / (maximumVertical * 8));
            planeLength = checked(PlaneWidth * PlaneHeight);
            plane = (byte*)NativeMemory.AlignedAlloc((nuint)planeLength, 32);
            if (plane is null)
            {
                throw new OutOfMemoryException();
            }

            NativeMemory.Clear(plane, (nuint)planeLength);
            if (progressive)
            {
                coefficientLength = checked(BlocksWide * BlocksHigh * 64);
                coefficients = (int*)NativeMemory.AlignedAlloc(checked((nuint)coefficientLength * 4), 32);
                if (coefficients is null)
                {
                    throw new OutOfMemoryException();
                }

                NativeMemory.Clear(coefficients, checked((nuint)coefficientLength * 4));
            }
        }

        /// <summary>Gets one mutable natural-order progressive coefficient block.</summary>
        public Span<int> GetCoefficientBlock(int blockX, int blockY)
        {
            int offset = checked((blockY * BlocksWide + blockX) * 64);
            return new Span<int>(coefficients + offset, 64);
        }

        /// <summary>Releases plane and coefficient allocations.</summary>
        public void Dispose()
        {
            if (plane is not null)
            {
                NativeMemory.AlignedFree(plane);
                plane = null;
            }

            if (coefficients is not null)
            {
                NativeMemory.AlignedFree(coefficients);
                coefficients = null;
            }
        }
    }

    /// <summary>Describes one scan header.</summary>
    private sealed class JpegScan
    {
        /// <summary>Initializes scan parameters.</summary>
        public JpegScan(JpegComponent[] components, int spectralStart, int spectralEnd, int highApproximation, int lowApproximation)
        {
            Components = components;
            SpectralStart = spectralStart;
            SpectralEnd = spectralEnd;
            HighApproximation = highApproximation;
            LowApproximation = lowApproximation;
        }

        public JpegComponent[] Components { get; }
        public int SpectralStart { get; }
        public int SpectralEnd { get; }
        public int HighApproximation { get; }
        public int LowApproximation { get; }
    }
}
