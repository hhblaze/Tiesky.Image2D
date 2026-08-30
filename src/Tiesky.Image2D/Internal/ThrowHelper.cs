using System.Diagnostics.CodeAnalysis;

namespace Tiesky.Image2D.Internal;

/// <summary>Centralizes cold exception construction outside hot paths.</summary>
internal static class ThrowHelper
{
    /// <summary>Throws an invalid-data exception.</summary>
    [DoesNotReturn]
    public static void InvalidData(string message) => throw new Image2DException(ImageErrorCode.InvalidData, message);

    /// <summary>Throws an unexpected-end exception.</summary>
    [DoesNotReturn]
    public static void UnexpectedEnd() => throw new Image2DException(ImageErrorCode.UnexpectedEndOfData, "The encoded image ended unexpectedly.");

    /// <summary>Throws an unsupported-feature exception.</summary>
    [DoesNotReturn]
    public static void Unsupported(string message) => throw new Image2DException(ImageErrorCode.UnsupportedFeature, message);

    /// <summary>Checks the configured pixel limit before allocation.</summary>
    public static void ValidateDimensions(int width, int height, long maximumPixels)
    {
        if (width <= 0 || height <= 0)
        {
            InvalidData("Image dimensions must be positive.");
        }

        long pixels = checked((long)width * height);
        if (pixels > maximumPixels)
        {
            throw new Image2DException(ImageErrorCode.PixelLimitExceeded, $"The image contains {pixels} pixels; the configured limit is {maximumPixels}.");
        }
    }
}
