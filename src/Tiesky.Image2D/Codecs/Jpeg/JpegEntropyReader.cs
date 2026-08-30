using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.Jpeg;

/// <summary>Reads byte-stuffed JPEG entropy bits without consuming the next marker.</summary>
internal ref struct JpegEntropyReader
{
    private readonly ref byte data;
    private readonly int length;
    private int position;
    private ulong bits;
    private int bitCount;

    /// <summary>Initializes an entropy reader at the first byte following SOS.</summary>
    public JpegEntropyReader(ReadOnlySpan<byte> data, int position)
    {
        this.data = ref MemoryMarshal.GetReference(data);
        length = data.Length;
        this.position = position;
        bits = 0;
        bitCount = 0;
    }

    /// <summary>Gets the current byte position.</summary>
    public readonly int Position => position;

    /// <summary>Reads one entropy bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit() => ReadBits(1);

    /// <summary>Reads up to sixteen entropy bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (!EnsureBits(count))
        {
            ThrowHelper.InvalidData("A JPEG marker occurred inside an entropy-coded block.");
        }

        int result = PeekBufferedBits(count);
        DropBits(count);
        return result;
    }

    /// <summary>Tries to expose bits without crossing an unstuffed scan marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeekBits(int count, out int value)
    {
        if (!EnsureBits(count))
        {
            value = 0;
            return false;
        }

        value = PeekBufferedBits(count);
        return true;
    }

    /// <summary>Drops bits previously returned by <see cref="TryPeekBits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DropBits(int count)
    {
        bitCount -= count;
    }

    /// <summary>Fills the reservoir, returning false when the terminating marker is reached.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnsureBits(int count)
    {
        while (bitCount < count)
        {
            if (position >= length)
            {
                ThrowHelper.UnexpectedEnd();
            }

            if (bitCount <= 31 && position <= length - sizeof(uint))
            {
                uint chunk = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref data, position)));
                uint inverted = chunk ^ uint.MaxValue;
                if (((inverted - 0x01010101u) & ~inverted & 0x80808080u) == 0)
                {
                    bits = (bits << 32) | chunk;
                    bitCount += 32;
                    position += sizeof(uint);
                    continue;
                }
            }

            byte value = Unsafe.Add(ref data, position++);
            if (value == 0xFF)
            {
                int markerPosition = position - 1;
                while (position < length && Unsafe.Add(ref data, position) == 0xFF)
                {
                    position++;
                }

                if (position >= length)
                {
                    ThrowHelper.UnexpectedEnd();
                }

                if (Unsafe.Add(ref data, position) != 0)
                {
                    position = markerPosition;
                    return false;
                }

                position++;
            }

            bits = (bits << 8) | value;
            bitCount += 8;
        }

        return true;
    }

    /// <summary>Reads already-buffered bits without advancing.</summary>
    private readonly int PeekBufferedBits(int count)
    {
        int shift = bitCount - count;
        return (int)((bits >> shift) & ((1UL << count) - 1));
    }

    /// <summary>Discards entropy padding and consumes the expected restart marker.</summary>
    public void ConsumeRestart(int expectedIndex)
    {
        bits = 0;
        bitCount = 0;
        if (position >= length || Unsafe.Add(ref data, position++) != 0xFF)
        {
            ThrowHelper.InvalidData("A JPEG restart marker is missing.");
        }

        while (position < length && Unsafe.Add(ref data, position) == 0xFF)
        {
            position++;
        }

        if (position >= length || Unsafe.Add(ref data, position++) != 0xD0 + expectedIndex)
        {
            ThrowHelper.InvalidData("A JPEG restart marker is out of sequence.");
        }
    }

    /// <summary>Returns the position of the marker terminating the current scan.</summary>
    public int FinishScan()
    {
        bits = 0;
        bitCount = 0;
        if (position >= length)
        {
            ThrowHelper.UnexpectedEnd();
        }

        // Encoders may leave fill bytes before the terminating marker, but an unstuffed
        // non-FF byte after all declared MCUs means the scan geometry was inconsistent.
        if (Unsafe.Add(ref data, position) != 0xFF)
        {
            ThrowHelper.InvalidData("JPEG entropy data extends beyond the declared scan.");
        }

        return position;
    }
}

/// <summary>Stores one canonical JPEG Huffman decoding table.</summary>
internal sealed class JpegHuffmanTable
{
    private const int LookaheadBits = 12;
    private readonly ushort[] lookahead = new ushort[1 << LookaheadBits];
    private readonly int[] minimumCode = new int[17];
    private readonly int[] maximumCode = new int[18];
    private readonly int[] valueOffset = new int[17];
    private readonly byte[] values;

    /// <summary>Builds canonical lookup bounds from sixteen code-length counts.</summary>
    public JpegHuffmanTable(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
    {
        values = symbols.ToArray();
        Array.Fill(maximumCode, -1);
        int code = 0;
        int symbolOffset = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = counts[length - 1];
            if (count != 0)
            {
                minimumCode[length] = code;
                maximumCode[length] = code + count - 1;
                valueOffset[length] = symbolOffset - code;
                if (length <= LookaheadBits)
                {
                    int repetitions = 1 << (LookaheadBits - length);
                    for (int i = 0; i < count; i++)
                    {
                        int packed = (length << 8) | values[symbolOffset + i];
                        int start = (code + i) << (LookaheadBits - length);
                        lookahead.AsSpan(start, repetitions).Fill((ushort)packed);
                    }
                }

                symbolOffset += count;
                code += count;
            }

            if (code > (1 << length))
            {
                ThrowHelper.InvalidData("A JPEG Huffman table is oversubscribed.");
            }

            code <<= 1;
        }

        maximumCode[17] = int.MaxValue;
    }

    /// <summary>Decodes one symbol using canonical length bounds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(ref JpegEntropyReader reader)
    {
        if (reader.TryPeekBits(LookaheadBits, out int prefix))
        {
            int packed = lookahead[prefix];
            if (packed != 0)
            {
                reader.DropBits(packed >> 8);
                return packed & 255;
            }
        }

        return DecodeSlow(ref reader);
    }

    /// <summary>Decodes the uncommon canonical code longer than the lookahead table.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int DecodeSlow(ref JpegEntropyReader reader)
    {
        int code = 0;
        for (int length = 1; length <= 16; length++)
        {
            code = (code << 1) | reader.ReadBit();
            if (code <= maximumCode[length] && maximumCode[length] >= 0)
            {
                int index = valueOffset[length] + code;
                if ((uint)index >= (uint)values.Length || code < minimumCode[length])
                {
                    break;
                }

                return values[index];
            }
        }

        ThrowHelper.InvalidData("JPEG entropy data contains an invalid Huffman code.");
        return 0;
    }

    /// <summary>
    /// Decodes a baseline symbol and consumes the amplitude bits described by its low
    /// nibble in the same reservoir operation whenever the canonical lookup succeeds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeWithAmplitude(ref JpegEntropyReader reader, int maximumAmplitudeBits, out int amplitude)
    {
        if (reader.TryPeekBits(LookaheadBits, out int prefix))
        {
            int packed = lookahead[prefix];
            if (packed != 0)
            {
                int codeBits = packed >> 8;
                int symbol = packed & 255;
                int amplitudeBits = symbol & 15;
                if (amplitudeBits > maximumAmplitudeBits)
                {
                    reader.DropBits(codeBits);
                    amplitude = 0;
                    return symbol;
                }

                int totalBits = codeBits + amplitudeBits;
                if (totalBits <= LookaheadBits)
                {
                    reader.DropBits(totalBits);
                    amplitude = amplitudeBits == 0
                        ? 0
                        : (prefix >> (LookaheadBits - totalBits)) & ((1 << amplitudeBits) - 1);
                    return symbol;
                }

                if (reader.TryPeekBits(totalBits, out int combined))
                {
                    reader.DropBits(totalBits);
                    amplitude = amplitudeBits == 0 ? 0 : combined & ((1 << amplitudeBits) - 1);
                    return symbol;
                }

                reader.DropBits(codeBits);
                amplitude = reader.ReadBits(amplitudeBits);
                return symbol;
            }
        }

        int fallback = DecodeSlow(ref reader);
        int fallbackBits = fallback & 15;
        amplitude = fallbackBits <= maximumAmplitudeBits ? reader.ReadBits(fallbackBits) : 0;
        return fallback;
    }
}
