using System.Runtime.CompilerServices;

namespace Tiesky.Image2D.Codecs.Jpeg;

/// <summary>Performs the integer 8x8 inverse discrete cosine transform.</summary>
internal static class JpegIdct
{
    private const int BasisBits = 14;
    private static readonly int[] HalfBasis = CreateReducedBasis(2);
    private static readonly int[] QuarterBasis = CreateReducedBasis(4);
    private static readonly int[] EighthBasis = CreateReducedBasis(8);
    private const int W1 = 2841;
    private const int W2 = 2676;
    private const int W3 = 2408;
    private const int W5 = 1609;
    private const int W6 = 1108;
    private const int W7 = 565;

    /// <summary>Dequantizes and transforms one block into an eight-bit destination plane.</summary>
    public static void Transform(scoped ReadOnlySpan<int> coefficients, scoped ReadOnlySpan<ushort> quantization, scoped Span<byte> destination, int stride, int destinationX, int destinationY)
    {
        if (HasOnlyDc(coefficients, 8))
        {
            int dc = coefficients[0] * quantization[0];
            byte sample = (byte)Math.Clamp((((dc << 11) + 8192) >> 14) + 128, 0, 255);
            for (int y = 0; y < 8; y++)
            {
                destination.Slice((destinationY + y) * stride + destinationX, 8).Fill(sample);
            }

            return;
        }

        Span<int> block = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            block[i] = coefficients[i] * quantization[i];
        }

        for (int row = 0; row < 8; row++)
        {
            TransformRow(block.Slice(row * 8, 8));
        }

        for (int column = 0; column < 8; column++)
        {
            TransformColumn(block, column, destination, stride, destinationX, destinationY);
        }
    }

    /// <summary>Applies the horizontal half of the scaled integer IDCT.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransformRow(Span<int> row)
    {
        if ((row[1] | row[2] | row[3] | row[4] | row[5] | row[6] | row[7]) == 0)
        {
            int dc = row[0] << 3;
            row.Fill(dc);
            return;
        }

        int x0 = (row[0] << 11) + 128;
        int x1 = row[4] << 11;
        int x2 = row[6];
        int x3 = row[2];
        int x4 = row[1];
        int x5 = row[7];
        int x6 = row[5];
        int x7 = row[3];
        int x8 = W7 * (x4 + x5);
        x4 = x8 + (W1 - W7) * x4;
        x5 = x8 - (W1 + W7) * x5;
        x8 = W3 * (x6 + x7);
        x6 = x8 - (W3 - W5) * x6;
        x7 = x8 - (W3 + W5) * x7;
        x8 = x0 + x1;
        x0 -= x1;
        x1 = W6 * (x3 + x2);
        x2 = x1 - (W2 + W6) * x2;
        x3 = x1 + (W2 - W6) * x3;
        x1 = x4 + x6;
        x4 -= x6;
        x6 = x5 + x7;
        x5 -= x7;
        x7 = x8 + x3;
        x8 -= x3;
        x3 = x0 + x2;
        x0 -= x2;
        x2 = (181 * (x4 + x5) + 128) >> 8;
        x4 = (181 * (x4 - x5) + 128) >> 8;
        row[0] = (x7 + x1) >> 8;
        row[1] = (x3 + x2) >> 8;
        row[2] = (x0 + x4) >> 8;
        row[3] = (x8 + x6) >> 8;
        row[4] = (x8 - x6) >> 8;
        row[5] = (x0 - x4) >> 8;
        row[6] = (x3 - x2) >> 8;
        row[7] = (x7 - x1) >> 8;
    }

    /// <summary>Applies the vertical half and stores clamped samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransformColumn(Span<int> block, int column, Span<byte> destination, int stride, int destinationX, int destinationY)
    {
        if ((block[8 + column] | block[16 + column] | block[24 + column] | block[32 + column] |
             block[40 + column] | block[48 + column] | block[56 + column]) == 0)
        {
            int value = (((block[column] << 8) + 8192) >> 14);
            for (int y = 0; y < 8; y++)
            {
                Store(destination, destinationX + column, destinationY + y, stride, value);
            }

            return;
        }

        int x0 = (block[column] << 8) + 8192;
        int x1 = block[32 + column] << 8;
        int x2 = block[48 + column];
        int x3 = block[16 + column];
        int x4 = block[8 + column];
        int x5 = block[56 + column];
        int x6 = block[40 + column];
        int x7 = block[24 + column];
        int x8 = W7 * (x4 + x5) + 4;
        x4 = (x8 + (W1 - W7) * x4) >> 3;
        x5 = (x8 - (W1 + W7) * x5) >> 3;
        x8 = W3 * (x6 + x7) + 4;
        x6 = (x8 - (W3 - W5) * x6) >> 3;
        x7 = (x8 - (W3 + W5) * x7) >> 3;
        x8 = x0 + x1;
        x0 -= x1;
        x1 = W6 * (x3 + x2) + 4;
        x2 = (x1 - (W2 + W6) * x2) >> 3;
        x3 = (x1 + (W2 - W6) * x3) >> 3;
        x1 = x4 + x6;
        x4 -= x6;
        x6 = x5 + x7;
        x5 -= x7;
        x7 = x8 + x3;
        x8 -= x3;
        x3 = x0 + x2;
        x0 -= x2;
        x2 = (181 * (x4 + x5) + 128) >> 8;
        x4 = (181 * (x4 - x5) + 128) >> 8;

        Store(destination, destinationX + column, destinationY, stride, (x7 + x1) >> 14);
        Store(destination, destinationX + column, destinationY + 1, stride, (x3 + x2) >> 14);
        Store(destination, destinationX + column, destinationY + 2, stride, (x0 + x4) >> 14);
        Store(destination, destinationX + column, destinationY + 3, stride, (x8 + x6) >> 14);
        Store(destination, destinationX + column, destinationY + 4, stride, (x8 - x6) >> 14);
        Store(destination, destinationX + column, destinationY + 5, stride, (x0 - x4) >> 14);
        Store(destination, destinationX + column, destinationY + 6, stride, (x3 - x2) >> 14);
        Store(destination, destinationX + column, destinationY + 7, stride, (x7 - x1) >> 14);
    }

    /// <summary>Stores a level-shifted IDCT sample.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Span<byte> destination, int x, int y, int stride, int value) => destination[y * stride + x] = (byte)Math.Clamp(value + 128, 0, 255);

    /// <summary>Transforms a block into a power-of-two box-reduced destination.</summary>
    public static void TransformScaled(scoped ReadOnlySpan<int> coefficients, scoped ReadOnlySpan<ushort> quantization, scoped Span<byte> destination, int stride, int destinationX, int destinationY, int reduction)
    {
        if (reduction == 1)
        {
            Transform(coefficients, quantization, destination, stride, destinationX, destinationY);
            return;
        }

        int outputSize = 8 / reduction;
        ReadOnlySpan<int> basis = reduction switch
        {
            2 => HalfBasis,
            4 => QuarterBasis,
            8 => EighthBasis,
            _ => throw new ArgumentOutOfRangeException(nameof(reduction)),
        };
        if (HasOnlyDc(coefficients, outputSize))
        {
            long total = (long)coefficients[0] * quantization[0] * basis[0] * basis[0];
            const long Half = 1L << ((BasisBits * 2) - 1);
            int value = total >= 0
                ? (int)((total + Half) >> (BasisBits * 2))
                : -(int)(((-total) + Half) >> (BasisBits * 2));
            byte sample = (byte)Math.Clamp(value + 128, 0, 255);
            for (int y = 0; y < outputSize; y++)
            {
                destination.Slice((destinationY + y) * stride + destinationX, outputSize).Fill(sample);
            }

            return;
        }

        // Frequencies above the reduced block's Nyquist limit are deliberately discarded.
        // This avoids aliasing and changes the scaled transform to two outputSize-cubed passes.
        Span<long> horizontal = stackalloc long[16];
        horizontal = horizontal[..(outputSize * outputSize)];
        for (int v = 0; v < outputSize; v++)
        for (int x = 0; x < outputSize; x++)
        {
            long total = 0;
            for (int u = 0; u < outputSize; u++)
            {
                total += (long)coefficients[v * 8 + u] * quantization[v * 8 + u] * basis[x * 8 + u];
            }

            horizontal[v * outputSize + x] = total;
        }

        for (int y = 0; y < outputSize; y++)
        {
            for (int x = 0; x < outputSize; x++)
            {
                long total = 0;
                for (int v = 0; v < outputSize; v++)
                {
                    total += horizontal[v * outputSize + x] * basis[y * 8 + v];
                }

                const long Half = 1L << ((BasisBits * 2) - 1);
                int sample = total >= 0
                    ? (int)((total + Half) >> (BasisBits * 2))
                    : -(int)(((-total) + Half) >> (BasisBits * 2));
                destination[(destinationY + y) * stride + destinationX + x] = (byte)Math.Clamp(sample + 128, 0, 255);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasOnlyDc(ReadOnlySpan<int> coefficients, int frequencyCount)
    {
        for (int vertical = 0; vertical < frequencyCount; vertical++)
        for (int horizontal = 0; horizontal < frequencyCount; horizontal++)
        {
            if ((vertical != 0 || horizontal != 0) && coefficients[vertical * 8 + horizontal] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds a fixed-point basis that averages the covered full-resolution samples.</summary>
    private static int[] CreateReducedBasis(int reduction)
    {
        int outputSize = 8 / reduction;
        int[] basis = new int[outputSize * 8];
        for (int output = 0; output < outputSize; output++)
        for (int frequency = 0; frequency < 8; frequency++)
        {
            double average = 0;
            for (int sample = 0; sample < reduction; sample++)
            {
                int coordinate = output * reduction + sample;
                average += Math.Cos(((2 * coordinate + 1) * frequency * Math.PI) / 16);
            }

            average /= reduction;
            double normalization = frequency == 0 ? 1 / Math.Sqrt(2) : 1;
            basis[output * 8 + frequency] = (int)Math.Round(0.5 * normalization * average * (1 << BasisBits));
        }

        return basis;
    }
}
