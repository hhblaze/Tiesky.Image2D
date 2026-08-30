using System.Buffers.Binary;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs.WebP;

/// <summary>Validates the WebP container and dispatches static VP8 family payloads.</summary>
internal static class WebPDecoder
{
    /// <summary>Decodes a static WebP image.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> data, long maximumPixels)
    {
        BinaryPrimitivesEx.Ensure(data, 0, 20);
        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        ulong declaredEnd = (ulong)riffLength + 8;
        // A final odd-sized RIFF chunk has a padding byte. Some established encoders
        // count that byte in RIFF size but omit it at end-of-file; accept only that
        // one-byte interoperability case, never a truncated payload.
        if (riffLength < 12 || declaredEnd > (ulong)data.Length + 1)
        {
            ThrowHelper.InvalidData("The WebP RIFF length is invalid.");
        }

        int offset = 12;
        bool animation = false;
        ReadOnlySpan<byte> vp8 = default;
        ReadOnlySpan<byte> vp8l = default;
        ReadOnlySpan<byte> alpha = default;
        int canvasWidth = 0;
        int canvasHeight = 0;

        while (offset + 8 <= data.Length && (ulong)(offset + 8) <= declaredEnd)
        {
            ReadOnlySpan<byte> type = data.Slice(offset, 4);
            uint chunkLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            if (chunkLengthValue > int.MaxValue)
            {
                ThrowHelper.InvalidData("A WebP chunk is too large.");
            }

            int chunkLength = (int)chunkLengthValue;
            BinaryPrimitivesEx.Ensure(data, offset + 8, chunkLength);
            ReadOnlySpan<byte> payload = data.Slice(offset + 8, chunkLength);
            if (type.SequenceEqual("VP8X"u8))
            {
                if (chunkLength != 10)
                {
                    ThrowHelper.InvalidData("The WebP extended header length is invalid.");
                }

                animation = (payload[0] & 0x02) != 0;
                canvasWidth = 1 + ReadUInt24(payload[4..]);
                canvasHeight = 1 + ReadUInt24(payload[7..]);
                ThrowHelper.ValidateDimensions(canvasWidth, canvasHeight, maximumPixels);
            }
            else if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
            {
                animation = true;
            }
            else if (type.SequenceEqual("ALPH"u8))
            {
                alpha = payload;
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                vp8 = payload;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                vp8l = payload;
            }

            offset = checked(offset + 8 + chunkLength + (chunkLength & 1));
        }

        if (animation)
        {
            ThrowHelper.Unsupported("Animated WebP is not supported.");
        }

        if (!vp8l.IsEmpty)
        {
            return Vp8LosslessDecoder.Decode(vp8l, maximumPixels, canvasWidth, canvasHeight);
        }

        if (!vp8.IsEmpty)
        {
            return Vp8LossyDecoder.Decode(vp8, alpha, maximumPixels, canvasWidth, canvasHeight);
        }

        ThrowHelper.InvalidData("The WebP container has no image bitstream.");
        return null!;
    }

    /// <summary>Reads a little-endian unsigned 24-bit integer.</summary>
    private static int ReadUInt24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);
}
