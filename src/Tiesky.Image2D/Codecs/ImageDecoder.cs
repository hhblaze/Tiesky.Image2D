using Tiesky.Image2D.Codecs.Bmp;
using Tiesky.Image2D.Codecs.Jpeg;
using Tiesky.Image2D.Codecs.Png;
using Tiesky.Image2D.Codecs.WebP;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Codecs;

/// <summary>Detects an encoded format from its signature and dispatches its decoder.</summary>
internal static class ImageDecoder
{
    /// <summary>Decodes a supported image without relying on file extensions.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> data, long maximumPixels, DecodeRequest request)
    {
        if (data.Length >= 8 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 &&
            data[4] == 13 && data[5] == 10 && data[6] == 26 && data[7] == 10)
        {
            return PngDecoder.Decode(data, maximumPixels);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return JpegDecoder.Decode(data, maximumPixels, request);
        }

        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return BmpDecoder.Decode(data, maximumPixels);
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return WebPDecoder.Decode(data, maximumPixels);
        }

        throw new Image2DException(ImageErrorCode.UnknownFormat, "The input signature does not identify a supported image format.");
    }
}
