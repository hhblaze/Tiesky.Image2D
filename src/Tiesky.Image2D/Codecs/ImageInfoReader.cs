using System.Buffers.Binary;
using Tiesky.Image2D.Codecs.Jpeg;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs;

/// <summary>Parses only the container prefix required for public image information.</summary>
internal static class ImageInfoReader
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Attempts identification, requesting more bytes when an incremental header is incomplete.</summary>
    public static bool TryIdentify(
        ReadOnlySpan<byte> data,
        bool complete,
        long maximumPixels,
        out ImageInfo? info)
    {
        info = null;
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
        {
            return TryReadPng(data, complete, maximumPixels, out info);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return TryReadJpeg(data, complete, maximumPixels, out info);
        }

        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return TryReadBmp(data, complete, maximumPixels, out info);
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return TryReadWebP(data, complete, maximumPixels, out info);
        }

        if (!complete && data.Length < 12)
        {
            return false;
        }

        throw new Image2DException(ImageErrorCode.UnknownFormat, "The input signature does not identify a supported image format.");
    }

    private static bool TryReadPng(ReadOnlySpan<byte> data, bool complete, long maximumPixels, out ImageInfo? info)
    {
        info = null;
        if (!Require(data, 8, 25, complete))
        {
            return false;
        }

        int headerLength = ReadLength(data, 8, "PNG");
        ReadOnlySpan<byte> headerType = data.Slice(12, 4);
        if (headerLength != 13 || !headerType.SequenceEqual("IHDR"u8))
        {
            ThrowHelper.InvalidData("The PNG header is invalid or out of order.");
        }

        ReadOnlySpan<byte> header = data.Slice(16, 13);
        uint declaredCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(29, 4));
        if (Crc32.Compute(headerType, header) != declaredCrc)
        {
            ThrowHelper.InvalidData("The PNG header has an invalid CRC.");
        }

        int width = ReadPositiveDimension(BinaryPrimitives.ReadUInt32BigEndian(header), "PNG width");
        int height = ReadPositiveDimension(BinaryPrimitives.ReadUInt32BigEndian(header[4..]), "PNG height");
        ValidatePngHeader(header);
        ThrowHelper.ValidateDimensions(width, height, maximumPixels);

        int offset = 33;
        while (true)
        {
            if (!Require(data, offset, 8, complete))
            {
                return false;
            }

            int length = ReadLength(data, offset, "PNG");
            ReadOnlySpan<byte> type = data.Slice(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8))
            {
                info = Create(ImageFormat.Png, "image/png", width, height, ExifOrientation.Normal, isAnimated: false);
                return true;
            }

            int chunkBytes;
            try
            {
                chunkBytes = checked(length + 12);
            }
            catch (OverflowException)
            {
                ThrowHelper.InvalidData("A PNG chunk is too large.");
                throw;
            }

            if (!Require(data, offset, chunkBytes, complete))
            {
                return false;
            }

            if (type.SequenceEqual("acTL"u8))
            {
                if (length != 8)
                {
                    ThrowHelper.InvalidData("The APNG animation header is invalid.");
                }

                ReadOnlySpan<byte> payload = data.Slice(offset + 8, length);
                uint chunkCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + length, 4));
                if (Crc32.Compute(type, payload) != chunkCrc)
                {
                    ThrowHelper.InvalidData("The APNG animation header has an invalid CRC.");
                }

                info = Create(ImageFormat.Png, "image/png", width, height, ExifOrientation.Normal, isAnimated: true);
                return true;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                ThrowHelper.InvalidData("The PNG container has no image data.");
            }

            offset = checked(offset + chunkBytes);
        }
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, bool complete, long maximumPixels, out ImageInfo? info)
    {
        info = null;
        int position = 2;
        int width = 0;
        int height = 0;
        ExifOrientation orientation = ExifOrientation.Normal;
        while (true)
        {
            if (!Require(data, position, 1, complete))
            {
                return false;
            }

            if (data[position++] != 0xFF)
            {
                ThrowHelper.InvalidData("A JPEG marker prefix is missing.");
            }

            while (true)
            {
                if (!Require(data, position, 1, complete))
                {
                    return false;
                }

                if (data[position] != 0xFF)
                {
                    break;
                }

                position++;
            }

            int marker = data[position++];
            if (marker == 0 || marker is >= 0xD0 and <= 0xD8 or 0x01)
            {
                ThrowHelper.InvalidData("A JPEG marker is invalid before image data.");
            }

            if (marker == 0xD9)
            {
                ThrowHelper.InvalidData("The JPEG container has no image scan.");
            }

            if (!Require(data, position, 2, complete))
            {
                return false;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (segmentLength < 2)
            {
                ThrowHelper.InvalidData("A JPEG segment length is invalid.");
            }

            if (!Require(data, position, segmentLength, complete))
            {
                return false;
            }

            ReadOnlySpan<byte> payload = data.Slice(position + 2, segmentLength - 2);
            position += segmentLength;
            if (marker == 0xE1)
            {
                orientation = ExifOrientationReader.Read(payload);
            }
            else if (IsStartOfFrame(marker))
            {
                if (payload.Length < 6)
                {
                    ThrowHelper.InvalidData("The JPEG frame header is incomplete.");
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                ThrowHelper.ValidateDimensions(width, height, maximumPixels);
            }
            else if (marker == 0xDA)
            {
                if (width == 0 || height == 0)
                {
                    ThrowHelper.InvalidData("A JPEG scan precedes its frame header.");
                }

                info = Create(ImageFormat.Jpeg, "image/jpeg", width, height, orientation, isAnimated: false);
                return true;
            }
        }
    }

    private static bool TryReadBmp(ReadOnlySpan<byte> data, bool complete, long maximumPixels, out ImageInfo? info)
    {
        info = null;
        if (!Require(data, 0, 54, complete))
        {
            return false;
        }

        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
        uint dibValue = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        if (dibValue > int.MaxValue)
        {
            ThrowHelper.InvalidData("The BMP information header is too large.");
        }

        int dibSize = (int)dibValue;
        if (dibSize < 40)
        {
            ThrowHelper.Unsupported("OS/2 BMP headers are not supported.");
        }

        if (!Require(data, 14, dibSize, complete))
        {
            return false;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue)
        {
            ThrowHelper.InvalidData("The BMP dimensions are invalid.");
        }

        int height = Math.Abs(signedHeight);
        ThrowHelper.ValidateDimensions(width, height, maximumPixels);
        if (complete && declaredSize != 0 && declaredSize > data.Length)
        {
            ThrowHelper.UnexpectedEnd();
        }

        info = Create(ImageFormat.Bmp, "image/bmp", width, height, ExifOrientation.Normal, isAnimated: false);
        return true;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> data, bool complete, long maximumPixels, out ImageInfo? info)
    {
        info = null;
        if (!Require(data, 0, 20, complete))
        {
            return false;
        }

        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        ulong declaredEnd = (ulong)riffLength + 8;
        if (riffLength < 12)
        {
            ThrowHelper.InvalidData("The WebP RIFF length is invalid.");
        }

        if (complete && declaredEnd > (ulong)data.Length + 1)
        {
            ThrowHelper.UnexpectedEnd();
        }

        int offset = 12;
        while (true)
        {
            if (!Require(data, offset, 8, complete))
            {
                return false;
            }

            ReadOnlySpan<byte> type = data.Slice(offset, 4);
            uint lengthValue = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            if (lengthValue > int.MaxValue)
            {
                ThrowHelper.InvalidData("A WebP chunk is too large.");
            }

            int length = (int)lengthValue;
            int payloadOffset = offset + 8;
            if (type.SequenceEqual("VP8X"u8))
            {
                if (length != 10)
                {
                    ThrowHelper.InvalidData("The WebP extended header length is invalid.");
                }

                if (!Require(data, payloadOffset, 10, complete))
                {
                    return false;
                }

                ReadOnlySpan<byte> payload = data.Slice(payloadOffset, 10);
                int width = 1 + ReadUInt24(payload[4..]);
                int height = 1 + ReadUInt24(payload[7..]);
                bool animated = (payload[0] & 0x02) != 0;
                info = Create(ImageFormat.WebP, "image/webp", width, height, ExifOrientation.Normal, animated, maximumPixels);
                return true;
            }

            if (type.SequenceEqual("VP8L"u8))
            {
                if (length < 5)
                {
                    ThrowHelper.InvalidData("The VP8L header is incomplete.");
                }

                if (!Require(data, payloadOffset, 5, complete))
                {
                    return false;
                }

                ReadOnlySpan<byte> payload = data.Slice(payloadOffset, 5);
                if (payload[0] != 0x2F)
                {
                    ThrowHelper.InvalidData("The VP8L signature is invalid.");
                }

                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
                if ((bits >> 29) != 0)
                {
                    ThrowHelper.Unsupported("The VP8L version is unsupported.");
                }

                int width = 1 + (int)(bits & 0x3FFF);
                int height = 1 + (int)((bits >> 14) & 0x3FFF);
                info = Create(ImageFormat.WebP, "image/webp", width, height, ExifOrientation.Normal, isAnimated: false, maximumPixels);
                return true;
            }

            if (type.SequenceEqual("VP8 "u8))
            {
                if (length < 10)
                {
                    ThrowHelper.InvalidData("The VP8 frame header is incomplete.");
                }

                if (!Require(data, payloadOffset, 10, complete))
                {
                    return false;
                }

                ReadOnlySpan<byte> payload = data.Slice(payloadOffset, 10);
                if ((payload[0] & 1) != 0 || payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
                {
                    ThrowHelper.InvalidData("The VP8 key-frame header is invalid.");
                }

                int width = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3FFF;
                int height = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3FFF;
                info = Create(ImageFormat.WebP, "image/webp", width, height, ExifOrientation.Normal, isAnimated: false, maximumPixels);
                return true;
            }

            int paddedLength;
            try
            {
                paddedLength = checked(length + (length & 1));
            }
            catch (OverflowException)
            {
                ThrowHelper.InvalidData("A WebP chunk is too large.");
                throw;
            }

            if (!Require(data, payloadOffset, paddedLength, complete))
            {
                return false;
            }

            offset = checked(payloadOffset + paddedLength);
        }
    }

    private static ImageInfo Create(
        ImageFormat format,
        string mimeType,
        int width,
        int height,
        ExifOrientation orientation,
        bool isAnimated,
        long? maximumPixels = null)
    {
        if (maximumPixels.HasValue)
        {
            ThrowHelper.ValidateDimensions(width, height, maximumPixels.Value);
        }

        return new ImageInfo(format, mimeType, width, height, orientation, isAnimated);
    }

    private static bool Require(ReadOnlySpan<byte> data, int offset, int count, bool complete)
    {
        if ((uint)offset <= (uint)data.Length && count >= 0 && data.Length - offset >= count)
        {
            return true;
        }

        if (complete)
        {
            ThrowHelper.UnexpectedEnd();
        }

        return false;
    }

    private static int ReadLength(ReadOnlySpan<byte> data, int offset, string format)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        if (value > int.MaxValue)
        {
            ThrowHelper.InvalidData($"A {format} chunk is too large.");
        }

        return (int)value;
    }

    private static int ReadPositiveDimension(uint value, string name)
    {
        if (value == 0 || value > int.MaxValue)
        {
            ThrowHelper.InvalidData($"The {name} is invalid.");
        }

        return (int)value;
    }

    private static void ValidatePngHeader(ReadOnlySpan<byte> header)
    {
        int bitDepth = header[8];
        int colorType = header[9];
        bool valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 or 6 => bitDepth is 8 or 16,
            _ => false,
        };
        if (!valid)
        {
            ThrowHelper.Unsupported("The PNG color type and bit depth combination is unsupported.");
        }

        if (header[10] != 0 || header[11] != 0 || header[12] > 1)
        {
            ThrowHelper.Unsupported("The PNG compression, filter, or interlace method is unsupported.");
        }
    }

    private static bool IsStartOfFrame(int marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static int ReadUInt24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);
}
