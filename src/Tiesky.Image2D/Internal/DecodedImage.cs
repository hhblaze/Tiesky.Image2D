namespace Tiesky.Image2D.Internal;

/// <summary>Couples decoded pixels with the orientation declared by the source.</summary>
internal sealed class DecodedImage : IDisposable
{
    /// <summary>Initializes decoded image state.</summary>
    public DecodedImage(PixelBuffer pixels, ExifOrientation orientation, int sourceWidth = 0, int sourceHeight = 0, bool isOpaque = false)
    {
        Pixels = pixels;
        Orientation = orientation;
        SourceWidth = sourceWidth == 0 ? pixels.Width : sourceWidth;
        SourceHeight = sourceHeight == 0 ? pixels.Height : sourceHeight;
        IsOpaque = isOpaque;
    }

    /// <summary>Gets the owned pixels.</summary>
    public PixelBuffer Pixels { get; }

    /// <summary>Gets the source EXIF orientation.</summary>
    public ExifOrientation Orientation { get; }

    /// <summary>Gets the source geometry before decoder-native reduction.</summary>
    public int SourceWidth { get; }

    /// <summary>Gets the source geometry before decoder-native reduction.</summary>
    public int SourceHeight { get; }

    /// <summary>Gets whether every decoded alpha sample is known to be fully opaque.</summary>
    public bool IsOpaque { get; }

    /// <summary>Releases pixel storage.</summary>
    public void Dispose() => Pixels.Dispose();
}
