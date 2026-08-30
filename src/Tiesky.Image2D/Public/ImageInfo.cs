namespace Tiesky.Image2D;

/// <summary>Describes an encoded image without decoding its pixels.</summary>
public sealed class ImageInfo
{
    internal ImageInfo(
        ImageFormat format,
        string mimeType,
        int encodedWidth,
        int encodedHeight,
        ExifOrientation exifOrientation,
        bool isAnimated)
    {
        Format = format;
        MimeType = mimeType;
        EncodedWidth = encodedWidth;
        EncodedHeight = encodedHeight;
        ExifOrientation = exifOrientation;
        IsAnimated = isAnimated;
        bool swapsAxes = exifOrientation is ExifOrientation.MirrorHorizontalRotate270 or ExifOrientation.Rotate90 or
            ExifOrientation.MirrorHorizontalRotate90 or ExifOrientation.Rotate270;
        Width = swapsAxes ? encodedHeight : encodedWidth;
        Height = swapsAxes ? encodedWidth : encodedHeight;
    }

    /// <summary>Gets the detected image format.</summary>
    public ImageFormat Format { get; }

    /// <summary>Gets the canonical MIME type.</summary>
    public string MimeType { get; }

    /// <summary>Gets the visual width after automatic EXIF orientation.</summary>
    public int Width { get; }

    /// <summary>Gets the visual height after automatic EXIF orientation.</summary>
    public int Height { get; }

    /// <summary>Gets the width stored in the encoded pixel grid.</summary>
    public int EncodedWidth { get; }

    /// <summary>Gets the height stored in the encoded pixel grid.</summary>
    public int EncodedHeight { get; }

    /// <summary>Gets the TIFF/EXIF orientation, or <see cref="Tiesky.Image2D.ExifOrientation.Normal"/> when absent.</summary>
    public ExifOrientation ExifOrientation { get; }

    /// <summary>Gets whether the container declares animation.</summary>
    public bool IsAnimated { get; }
}
