using System.Numerics;
using System.Runtime.CompilerServices;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Processing;

/// <summary>Executes fused orientation, rotation, crop, and separable resize.</summary>
internal static class TransformProcessor
{
    /// <summary>Returns source pixels for an identity transform or an owned transformed buffer.</summary>
    public static PixelBuffer Apply(DecodedImage source, ImageRotation rotation, ResizeOptions? resize)
    {
        if (!Enum.IsDefined(rotation))
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "The rotation value is invalid.");
        }

        CoordinateTransform coordinates = new(source.Pixels.Width, source.Pixels.Height, source.Orientation, rotation);
        CoordinateTransform logicalCoordinates = new(source.SourceWidth, source.SourceHeight, source.Orientation, rotation);
        ResizePlan plan = ResizePlan.Create(logicalCoordinates.Width, logicalCoordinates.Height, resize);
        if (source.Orientation == ExifOrientation.Normal && rotation == ImageRotation.None &&
            plan.Width == source.Pixels.Width && plan.Height == source.Pixels.Height &&
            source.SourceWidth == source.Pixels.Width && source.SourceHeight == source.Pixels.Height &&
            plan.SourceX == 0 && plan.SourceY == 0 && plan.SourceWidth == source.Pixels.Width && plan.SourceHeight == source.Pixels.Height)
        {
            return source.Pixels;
        }

        if (resize is null || (plan.Width == coordinates.Width && plan.Height == coordinates.Height && plan.SourceWidth == logicalCoordinates.Width && plan.SourceHeight == logicalCoordinates.Height))
        {
            if (source.Orientation == ExifOrientation.Normal && rotation != ImageRotation.None)
            {
                return CopyRotation(source.Pixels, rotation);
            }

            return CopyOriented(source.Pixels, coordinates);
        }

        double horizontalScale = coordinates.Width / (double)logicalCoordinates.Width;
        double verticalScale = coordinates.Height / (double)logicalCoordinates.Height;
        KernelMap horizontal = KernelMap.Create(coordinates.Width, plan.Width, plan.SourceX * horizontalScale, plan.SourceWidth * horizontalScale, plan.Filter);
        KernelMap vertical = KernelMap.Create(coordinates.Height, plan.Height, plan.SourceY * verticalScale, plan.SourceHeight * verticalScale, plan.Filter);

        // Proven-opaque RGB24 stays compact through both passes. Choosing the smaller
        // intermediate shape is especially important when one dimension collapses sharply.
        long horizontalFirstPixels = (long)plan.Width * coordinates.Height;
        long verticalFirstPixels = (long)coordinates.Width * plan.Height;
        ImageRotation? fastRotation = source.Orientation == ExifOrientation.Normal ? rotation : null;
        bool quarterTurn = fastRotation is ImageRotation.Clockwise90 or ImageRotation.CounterClockwise90;
        bool horizontalFirst = horizontalFirstPixels < verticalFirstPixels || (horizontalFirstPixels == verticalFirstPixels && !quarterTurn);
        return horizontalFirst
            ? ResizeHorizontalFirst(source.Pixels, coordinates, plan, horizontal, vertical, source.IsOpaque, fastRotation)
            : ResizeVerticalFirst(source.Pixels, coordinates, plan, horizontal, vertical, source.IsOpaque, fastRotation);
    }

    /// <summary>Applies a later ordered phase to already oriented pixels.</summary>
    public static PixelBuffer Apply(PixelBuffer source, bool isOpaque, ImageRotation rotation, ResizeOptions? resize)
    {
        if (!Enum.IsDefined(rotation))
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "The rotation value is invalid.");
        }

        CoordinateTransform coordinates = new(source.Width, source.Height, ExifOrientation.Normal, rotation);
        ResizePlan plan = ResizePlan.Create(coordinates.Width, coordinates.Height, resize);
        if (rotation == ImageRotation.None &&
            plan.Width == source.Width && plan.Height == source.Height &&
            plan.SourceX == 0 && plan.SourceY == 0 && plan.SourceWidth == source.Width && plan.SourceHeight == source.Height)
        {
            return source;
        }

        if (resize is null || (plan.Width == coordinates.Width && plan.Height == coordinates.Height &&
            plan.SourceWidth == coordinates.Width && plan.SourceHeight == coordinates.Height))
        {
            return rotation == ImageRotation.None ? source : CopyRotation(source, rotation);
        }

        KernelMap horizontal = KernelMap.Create(coordinates.Width, plan.Width, plan.SourceX, plan.SourceWidth, plan.Filter);
        KernelMap vertical = KernelMap.Create(coordinates.Height, plan.Height, plan.SourceY, plan.SourceHeight, plan.Filter);
        long horizontalFirstPixels = (long)plan.Width * coordinates.Height;
        long verticalFirstPixels = (long)coordinates.Width * plan.Height;
        bool quarterTurn = rotation is ImageRotation.Clockwise90 or ImageRotation.CounterClockwise90;
        bool horizontalFirst = horizontalFirstPixels < verticalFirstPixels || (horizontalFirstPixels == verticalFirstPixels && !quarterTurn);
        return horizontalFirst
            ? ResizeHorizontalFirst(source, coordinates, plan, horizontal, vertical, isOpaque, rotation)
            : ResizeVerticalFirst(source, coordinates, plan, horizontal, vertical, isOpaque, rotation);
    }

    /// <summary>Copies a user quarter/half turn through cache-sized, independently schedulable tiles.</summary>
    private static PixelBuffer CopyRotation(PixelBuffer source, ImageRotation rotation)
    {
        bool quarterTurn = rotation is ImageRotation.Clockwise90 or ImageRotation.CounterClockwise90;
        PixelBuffer destination = new(
            quarterTurn ? source.Height : source.Width,
            quarterTurn ? source.Width : source.Height,
            source.BytesPerPixel);
        const int TileSize = 32;
        int tileRows = (destination.Height + TileSize - 1) / TileSize;
        bool parallel = ParallelExecution.ShouldRun((long)source.Width * source.Height, 1_000_000);
        try
        {
            ParallelExecution.For(0, tileRows, parallel, tileY =>
            {
                ReadOnlySpan<byte> sourceBytes = source.Span;
                int firstY = tileY * TileSize;
                int endY = Math.Min(firstY + TileSize, destination.Height);
                for (int tileX = 0; tileX < destination.Width; tileX += TileSize)
                {
                    int endX = Math.Min(tileX + TileSize, destination.Width);
                    for (int y = firstY; y < endY; y++)
                    {
                        Span<byte> target = destination.GetRowSpan(y);
                        for (int x = tileX; x < endX; x++)
                        {
                            int sourceX;
                            int sourceY;
                            if (rotation == ImageRotation.Clockwise90)
                            {
                                sourceX = y;
                                sourceY = source.Height - 1 - x;
                            }
                            else if (rotation == ImageRotation.CounterClockwise90)
                            {
                                sourceX = source.Width - 1 - y;
                                sourceY = x;
                            }
                            else
                            {
                                sourceX = source.Width - 1 - x;
                                sourceY = source.Height - 1 - y;
                            }

                            int input = (sourceY * source.Width + sourceX) * source.BytesPerPixel;
                            int output = x * source.BytesPerPixel;
                            if (source.BytesPerPixel == 4)
                            {
                                Unsafe.WriteUnaligned(
                                    ref target[output],
                                    Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in sourceBytes[input])));
                            }
                            else
                            {
                                target[output] = sourceBytes[input];
                                target[output + 1] = sourceBytes[input + 1];
                                target[output + 2] = sourceBytes[input + 2];
                            }
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

    /// <summary>Copies a discrete orientation without constructing a rotated full-size precursor.</summary>
    private static PixelBuffer CopyOriented(PixelBuffer source, CoordinateTransform coordinates)
    {
        int bytesPerPixel = source.BytesPerPixel;
        PixelBuffer destination = new(coordinates.Width, coordinates.Height, bytesPerPixel);
        try
        {
            Span<byte> sourceBytes = source.Span;
            for (int y = 0; y < destination.Height; y++)
            {
                Span<byte> target = destination.GetRowSpan(y);
                coordinates.Map(0, y, out int firstX, out int firstY);
                coordinates.Map(destination.Width - 1, y, out int lastX, out int lastY);
                if (firstY == lastY && lastX - firstX == destination.Width - 1)
                {
                    SimdPrimitives.Copy(source.GetRowSpan(firstY).Slice(firstX * bytesPerPixel, target.Length), target);
                    continue;
                }

                if (firstY == lastY && firstX - lastX == destination.Width - 1)
                {
                    ReadOnlySpan<byte> sourceRow = source.GetRowSpan(firstY).Slice(lastX * bytesPerPixel, target.Length);
                    if (bytesPerPixel == 4)
                    {
                        SimdPrimitives.ReversePixels(sourceRow, target);
                    }
                    else
                    {
                        ReverseRgb24(sourceRow, target);
                    }
                    continue;
                }

                for (int x = 0; x < destination.Width; x++)
                {
                    coordinates.Map(x, y, out int sourceX, out int sourceY);
                    int sourceOffset = checked((sourceY * source.Width + sourceX) * bytesPerPixel);
                    sourceBytes.Slice(sourceOffset, bytesPerPixel).CopyTo(target[(x * bytesPerPixel)..]);
                }
            }

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>Reverses one tightly packed RGB24 row without widening it to RGBA.</summary>
    private static void ReverseRgb24(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int pixels = source.Length / 3;
        for (int x = 0; x < pixels; x++)
        {
            int input = (pixels - 1 - x) * 3;
            int output = x * 3;
            destination[output] = source[input];
            destination[output + 1] = source[input + 1];
            destination[output + 2] = source[input + 2];
        }
    }

    /// <summary>Resizes logical rows first, retaining only target-width intermediate rows.</summary>
    private static PixelBuffer ResizeHorizontalFirst(PixelBuffer source, CoordinateTransform coordinates, ResizePlan plan, KernelMap horizontal, KernelMap vertical, bool opaque, ImageRotation? fastRotation)
    {
        int outputBytesPerPixel = opaque && source.BytesPerPixel == 3 ? 3 : 4;
        const int intermediateBytesPerPixel = 4;
        using PixelBuffer intermediate = new(plan.Width, coordinates.Height, intermediateBytesPerPixel);
        PixelBuffer destination = new(plan.Width, plan.Height, outputBytesPerPixel);
        try
        {
            long estimatedOperations = checked((long)coordinates.Height * horizontal.Weights.Length + (long)plan.Width * vertical.Weights.Length);
            bool parallel = ParallelExecution.ShouldRun(estimatedOperations, 4_000_000);
            ParallelExecution.For(0, coordinates.Height, parallel, y =>
            {
                Span<byte> target = intermediate.GetRowSpan(y);
                if (fastRotation == ImageRotation.None)
                {
                    ReadOnlySpan<byte> sourceRow = source.GetRowSpan(y);
                    for (int x = 0; x < plan.Width; x++)
                    {
                        AccumulateHorizontalRow(sourceRow, horizontal, x, target, x * intermediateBytesPerPixel, opaque, source.BytesPerPixel, intermediateBytesPerPixel);
                    }
                }
                else
                {
                    for (int x = 0; x < plan.Width; x++)
                    {
                        AccumulateOrientedHorizontal(source, coordinates, horizontal, x, y, target, x * intermediateBytesPerPixel, opaque, intermediateBytesPerPixel);
                    }
                }
            });

            ParallelExecution.For(0, plan.Height, parallel, y =>
            {
                ReadOnlySpan<byte> intermediateBytes = intermediate.Span;
                Span<byte> target = destination.GetRowSpan(y);
                for (int x = 0; x < plan.Width; x++)
                {
                    AccumulateVerticalBytes(intermediateBytes, intermediate.Width, intermediateBytesPerPixel, vertical, x, y, target, x * outputBytesPerPixel, opaque, outputBytesPerPixel);
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

    /// <summary>Resizes logical columns first, retaining only target-height intermediate columns.</summary>
    private static PixelBuffer ResizeVerticalFirst(PixelBuffer source, CoordinateTransform coordinates, ResizePlan plan, KernelMap horizontal, KernelMap vertical, bool opaque, ImageRotation? fastRotation)
    {
        int outputBytesPerPixel = opaque && source.BytesPerPixel == 3 ? 3 : 4;
        const int intermediateBytesPerPixel = 4;
        using PixelBuffer intermediate = new(coordinates.Width, plan.Height, intermediateBytesPerPixel);
        PixelBuffer destination = new(plan.Width, plan.Height, outputBytesPerPixel);
        try
        {
            long estimatedOperations = checked((long)coordinates.Width * vertical.Weights.Length + (long)plan.Height * horizontal.Weights.Length);
            bool parallel = ParallelExecution.ShouldRun(estimatedOperations, 4_000_000);
            ParallelExecution.For(0, plan.Height, parallel, y =>
            {
                ReadOnlySpan<byte> sourceBytes = source.Span;
                Span<byte> target = intermediate.GetRowSpan(y);
                for (int x = 0; x < coordinates.Width; x++)
                {
                    if (fastRotation == ImageRotation.None)
                    {
                        AccumulateVerticalBytes(sourceBytes, source.Width, source.BytesPerPixel, vertical, x, y, target, x * intermediateBytesPerPixel, opaque, intermediateBytesPerPixel);
                    }
                    else if (fastRotation is ImageRotation.Clockwise90 or ImageRotation.CounterClockwise90)
                    {
                        AccumulateQuarterTurnVertical(sourceBytes, source.Width, source.Height, source.BytesPerPixel, vertical, x, y, target, x * intermediateBytesPerPixel, opaque, fastRotation == ImageRotation.Clockwise90, intermediateBytesPerPixel);
                    }
                    else
                    {
                        AccumulateOrientedVertical(source, coordinates, vertical, x, y, target, x * intermediateBytesPerPixel, opaque, intermediateBytesPerPixel);
                    }
                }
            });

            ParallelExecution.For(0, plan.Height, parallel, y =>
            {
                Span<byte> target = destination.GetRowSpan(y);
                ReadOnlySpan<byte> sourceRow = intermediate.GetRowSpan(y);
                for (int x = 0; x < plan.Width; x++)
                {
                    AccumulateHorizontalRow(sourceRow, horizontal, x, target, x * outputBytesPerPixel, opaque, intermediateBytesPerPixel, outputBytesPerPixel);
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

    /// <summary>Filters one logical source row through the fused coordinate mapping.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateOrientedHorizontal(PixelBuffer source, CoordinateTransform coordinates, KernelMap map, int destinationX, int y, Span<byte> target, int output, bool opaque, int outputBytesPerPixel)
    {
        int start = map.Offsets[destinationX];
        int end = map.Offsets[destinationX + 1];
        Span<byte> sourceBytes = source.Span;
        if (opaque)
        {
            Vector4 color = default;
            for (int i = start; i < end; i++)
            {
                coordinates.Map(map.Indices[i], y, out int rawX, out int rawY);
                int input = (rawY * source.Width + rawX) * source.BytesPerPixel;
                color += LoadColor(sourceBytes, input) * map.Weights[i];
            }

            StoreOpaque(target, output, color, outputBytesPerPixel);
            return;
        }

        float red = 0, green = 0, blue = 0, alpha = 0;
        for (int i = start; i < end; i++)
        {
            coordinates.Map(map.Indices[i], y, out int rawX, out int rawY);
            int input = (rawY * source.Width + rawX) * source.BytesPerPixel;
            Accumulate(sourceBytes, input, map.Weights[i], ref red, ref green, ref blue, ref alpha);
        }

        Store(target, output, red, green, blue, alpha);
    }

    /// <summary>Filters one logical source column through the fused coordinate mapping.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateOrientedVertical(PixelBuffer source, CoordinateTransform coordinates, KernelMap map, int x, int destinationY, Span<byte> target, int output, bool opaque, int outputBytesPerPixel)
    {
        int start = map.Offsets[destinationY];
        int end = map.Offsets[destinationY + 1];
        Span<byte> sourceBytes = source.Span;
        if (opaque)
        {
            Vector4 color = default;
            for (int i = start; i < end; i++)
            {
                coordinates.Map(x, map.Indices[i], out int rawX, out int rawY);
                int input = (rawY * source.Width + rawX) * source.BytesPerPixel;
                color += LoadColor(sourceBytes, input) * map.Weights[i];
            }

            StoreOpaque(target, output, color, outputBytesPerPixel);
            return;
        }

        float red = 0, green = 0, blue = 0, alpha = 0;
        for (int i = start; i < end; i++)
        {
            coordinates.Map(x, map.Indices[i], out int rawX, out int rawY);
            int input = (rawY * source.Width + rawX) * source.BytesPerPixel;
            Accumulate(sourceBytes, input, map.Weights[i], ref red, ref green, ref blue, ref alpha);
        }

        Store(target, output, red, green, blue, alpha);
    }

    /// <summary>Filters one ordinary intermediate row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateHorizontalRow(ReadOnlySpan<byte> row, KernelMap map, int destinationX, Span<byte> target, int output, bool opaque, int bytesPerPixel = 4, int outputBytesPerPixel = 4)
    {
        int start = map.Offsets[destinationX];
        int end = map.Offsets[destinationX + 1];
        if (opaque)
        {
            Vector4 color = default;
            for (int i = start; i < end; i++)
            {
                color += LoadColor(row, map.Indices[i] * bytesPerPixel) * map.Weights[i];
            }

            StoreOpaque(target, output, color, outputBytesPerPixel);
            return;
        }

        float red = 0, green = 0, blue = 0, alpha = 0;
        for (int i = start; i < end; i++)
        {
            Accumulate(row, map.Indices[i] * bytesPerPixel, map.Weights[i], ref red, ref green, ref blue, ref alpha);
        }

        Store(target, output, red, green, blue, alpha);
    }

    /// <summary>Filters one ordinary intermediate column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateVerticalBytes(ReadOnlySpan<byte> sourceBytes, int sourceWidth, int bytesPerPixel, KernelMap map, int x, int destinationY, Span<byte> target, int output, bool opaque, int outputBytesPerPixel)
    {
        int start = map.Offsets[destinationY];
        int end = map.Offsets[destinationY + 1];
        if (opaque)
        {
            Vector4 color = default;
            for (int i = start; i < end; i++)
            {
                int input = (map.Indices[i] * sourceWidth + x) * bytesPerPixel;
                color += LoadColor(sourceBytes, input) * map.Weights[i];
            }

            StoreOpaque(target, output, color, outputBytesPerPixel);
            return;
        }

        float red = 0, green = 0, blue = 0, alpha = 0;
        for (int i = start; i < end; i++)
        {
            int input = (map.Indices[i] * sourceWidth + x) * bytesPerPixel;
            Accumulate(sourceBytes, input, map.Weights[i], ref red, ref green, ref blue, ref alpha);
        }

        Store(target, output, red, green, blue, alpha);
    }

    /// <summary>Filters a quarter-turn logical column using contiguous raw-row samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateQuarterTurnVertical(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int bytesPerPixel,
        KernelMap map,
        int logicalX,
        int destinationY,
        Span<byte> target,
        int output,
        bool opaque,
        bool clockwise,
        int outputBytesPerPixel)
    {
        int start = map.Offsets[destinationY];
        int end = map.Offsets[destinationY + 1];
        int rawY = clockwise ? sourceHeight - 1 - logicalX : logicalX;
        int rowOffset = rawY * sourceWidth * bytesPerPixel;
        if (opaque)
        {
            Vector4 color = default;
            for (int i = start; i < end; i++)
            {
                int rawX = clockwise ? map.Indices[i] : sourceWidth - 1 - map.Indices[i];
                color += LoadColor(source, rowOffset + rawX * bytesPerPixel) * map.Weights[i];
            }

            StoreOpaque(target, output, color, outputBytesPerPixel);
            return;
        }

        float red = 0, green = 0, blue = 0, alpha = 0;
        for (int i = start; i < end; i++)
        {
            int rawX = clockwise ? map.Indices[i] : sourceWidth - 1 - map.Indices[i];
            Accumulate(source, rowOffset + rawX * bytesPerPixel, map.Weights[i], ref red, ref green, ref blue, ref alpha);
        }

        Store(target, output, red, green, blue, alpha);
    }

    /// <summary>Accumulates premultiplied channels so transparent colors cannot create resize halos.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accumulate(ReadOnlySpan<byte> source, int input, float weight, ref float red, ref float green, ref float blue, ref float alpha)
    {
        float normalizedAlpha = source[input + 3] * (1f / 255f);
        float weightedAlpha = normalizedAlpha * weight;
        red += source[input] * weightedAlpha;
        green += source[input + 1] * weightedAlpha;
        blue += source[input + 2] * weightedAlpha;
        alpha += source[input + 3] * weight;
    }

    /// <summary>Loads one opaque RGBA sample into a SIMD-friendly color vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 LoadColor(ReadOnlySpan<byte> source, int input) =>
        new(source[input], source[input + 1], source[input + 2], 0);

    /// <summary>Unpremultiplies, clamps, and stores one filtered pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Span<byte> target, int output, float red, float green, float blue, float alpha)
    {
        float normalizedAlpha = alpha * (1f / 255f);
        if (normalizedAlpha > 1e-6f)
        {
            float inverse = 1f / normalizedAlpha;
            target[output] = ClampToByte(red * inverse);
            target[output + 1] = ClampToByte(green * inverse);
            target[output + 2] = ClampToByte(blue * inverse);
        }
        else
        {
            target[output] = 0;
            target[output + 1] = 0;
            target[output + 2] = 0;
        }

        target[output + 3] = ClampToByte(alpha);
    }

    /// <summary>Stores a filtered pixel whose alpha is known to remain fully opaque.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreOpaque(Span<byte> target, int output, Vector4 color, int bytesPerPixel)
    {
        target[output] = ClampToByte(color.X);
        target[output + 1] = ClampToByte(color.Y);
        target[output + 2] = ClampToByte(color.Z);
        if (bytesPerPixel == 4) target[output + 3] = 255;
    }

    /// <summary>Rounds a floating-point channel to the byte domain.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

}
