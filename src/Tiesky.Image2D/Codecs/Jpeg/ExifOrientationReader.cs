using System.Buffers.Binary;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.Jpeg;

/// <summary>Reads only the TIFF orientation tag needed by the transform pipeline.</summary>
internal static class ExifOrientationReader
{
    /// <summary>Returns a validated orientation or Normal for malformed optional metadata.</summary>
    public static ExifOrientation Read(ReadOnlySpan<byte> app1)
    {
        if (app1.Length < 14 || !app1[..6].SequenceEqual("Exif\0\0"u8))
        {
            return ExifOrientation.Normal;
        }

        ReadOnlySpan<byte> tiff = app1[6..];
        bool littleEndian;
        if (tiff[..2].SequenceEqual("II"u8))
        {
            littleEndian = true;
        }
        else if (tiff[..2].SequenceEqual("MM"u8))
        {
            littleEndian = false;
        }
        else
        {
            return ExifOrientation.Normal;
        }

        ushort magic = ReadUInt16(tiff, 2, littleEndian);
        uint ifdOffset = ReadUInt32(tiff, 4, littleEndian);
        if (magic != 42 || ifdOffset > int.MaxValue || ifdOffset + 2 > tiff.Length)
        {
            return ExifOrientation.Normal;
        }

        int directory = (int)ifdOffset;
        int count = ReadUInt16(tiff, directory, littleEndian);
        for (int i = 0; i < count; i++)
        {
            int entry = directory + 2 + i * 12;
            if (entry < 0 || entry + 12 > tiff.Length)
            {
                return ExifOrientation.Normal;
            }

            if (ReadUInt16(tiff, entry, littleEndian) == 0x0112 && ReadUInt16(tiff, entry + 2, littleEndian) == 3 && ReadUInt32(tiff, entry + 4, littleEndian) == 1)
            {
                ushort value = ReadUInt16(tiff, entry + 8, littleEndian);
                return value is >= 1 and <= 8 ? (ExifOrientation)value : ExifOrientation.Normal;
            }
        }

        return ExifOrientation.Normal;
    }

    /// <summary>Reads endian-aware TIFF UInt16 while treating malformed metadata as absent.</summary>
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if ((uint)offset > (uint)data.Length || data.Length - offset < 2)
        {
            return 0;
        }

        return littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    }

    /// <summary>Reads endian-aware TIFF UInt32 while treating malformed metadata as absent.</summary>
    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if ((uint)offset > (uint)data.Length || data.Length - offset < 4)
        {
            return 0;
        }

        return littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    }
}
