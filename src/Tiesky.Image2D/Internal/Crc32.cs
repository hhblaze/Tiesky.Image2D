namespace Tiesky.Image2D.Internal;

/// <summary>Computes the CRC-32 used by PNG chunks.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>Updates a PNG CRC value.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint value = crc;
        foreach (byte item in data)
        {
            value = Table[(value ^ item) & 0xFF] ^ (value >> 8);
        }

        return value;
    }

    /// <summary>Computes a PNG CRC across chunk type and payload.</summary>
    public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        uint value = Update(uint.MaxValue, type);
        return ~Update(value, payload);
    }

    /// <summary>Builds the immutable polynomial lookup table once.</summary>
    private static uint[] CreateTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
