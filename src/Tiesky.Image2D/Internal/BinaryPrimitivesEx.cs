using System.Buffers.Binary;

namespace Tiesky.Image2D.Internal;

/// <summary>Provides bounds-checked binary parsing helpers with stable failures.</summary>
internal static class BinaryPrimitivesEx
{
    /// <summary>Reads a big-endian unsigned 16-bit integer.</summary>
    public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    }

    /// <summary>Reads a big-endian unsigned 32-bit integer.</summary>
    public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    }

    /// <summary>Reads a little-endian unsigned 16-bit integer.</summary>
    public static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    /// <summary>Reads a little-endian signed 32-bit integer.</summary>
    public static int ReadInt32LittleEndian(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    }

    /// <summary>Reads a little-endian unsigned 32-bit integer.</summary>
    public static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    /// <summary>Ensures a range exists in the input.</summary>
    public static void Ensure(ReadOnlySpan<byte> data, int offset, int count)
    {
        if ((uint)offset > (uint)data.Length || count < 0 || data.Length - offset < count)
        {
            ThrowHelper.UnexpectedEnd();
        }
    }
}
