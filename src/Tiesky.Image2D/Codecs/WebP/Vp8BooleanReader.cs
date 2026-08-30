using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.WebP;

/// <summary>Reads the binary arithmetic coding used by VP8 partitions.</summary>
internal unsafe struct Vp8BooleanReader
{
    private byte* cursor;
    private readonly byte* end;
    private ulong reservoir;
    private uint rangeMinusOne;
    private int reservoirPosition;

    /// <summary>Initializes a bounded decoder over one VP8 partition.</summary>
    public Vp8BooleanReader(byte* data, int length)
    {
        if (length < 2)
        {
            ThrowHelper.InvalidData("A VP8 boolean partition is truncated.");
        }

        cursor = data;
        end = data + length;
        reservoir = 0;
        rangeMinusOne = 254;
        reservoirPosition = -8;
        Refill();
    }

    /// <summary>Reads one probability-weighted bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit(int probability)
    {
        if (reservoirPosition < 0)
        {
            Refill();
        }

        uint currentRange = rangeMinusOne;
        int position = reservoirPosition;
        uint split = (currentRange * (uint)probability) >> 8;
        bool one = (reservoir >> position) > split;
        uint nextRange;
        if (one)
        {
            nextRange = currentRange - split;
            reservoir -= (ulong)(split + 1) << position;
        }
        else
        {
            nextRange = split + 1;
        }

        int shift = BitOperations.LeadingZeroCount(nextRange) - 24;
        rangeMinusOne = (nextRange << shift) - 1;
        reservoirPosition = position - shift;
        return one ? 1 : 0;
    }

    /// <summary>Reads an equiprobable bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit() => ReadBit(128);

    /// <summary>Reads an unsigned big-endian literal from the arithmetic stream.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int ReadBits(int count)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
        {
            result = (result << 1) | ReadBit();
        }

        return result;
    }

    /// <summary>Reads the optional signed delta representation used by frame headers.</summary>
    public int ReadOptionalSigned(int bits)
    {
        if (ReadBit() == 0)
        {
            return 0;
        }

        int magnitude = ReadBits(bits);
        return ReadBit() == 0 ? magnitude : -magnitude;
    }

    /// <summary>Refills 56 guarded bits at a time, with a strict byte tail.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill()
    {
        if (end - cursor >= 8)
        {
            ulong input = Unsafe.ReadUnaligned<ulong>(cursor);
            cursor += 7;
            ulong next = BinaryPrimitives.ReverseEndianness(input) >> 8;
            reservoir = next | (reservoir << 56);
            reservoirPosition += 56;
            return;
        }

        while (reservoirPosition < 0)
        {
            if (cursor >= end)
            {
                ThrowHelper.InvalidData("A VP8 boolean partition ends inside a symbol.");
            }

            reservoir = *cursor++ | (reservoir << 8);
            reservoirPosition += 8;
        }
    }
}
