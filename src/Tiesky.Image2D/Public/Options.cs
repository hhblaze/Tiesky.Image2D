namespace Tiesky.Image2D;

/// <summary>Base type for output encoder settings.</summary>
public abstract class ImageEncoderOptions
{
    private protected ImageEncoderOptions()
    {
    }
}

/// <summary>Settings for static PNG output.</summary>
public sealed class PngEncoderOptions : ImageEncoderOptions
{
    /// <summary>Gets or sets the zlib compression level from 0 through 9.</summary>
    public int CompressionLevel { get; set; } = 6;
}

/// <summary>Settings for baseline JPEG output.</summary>
public sealed class JpegEncoderOptions : ImageEncoderOptions
{
    /// <summary>Gets or sets visual quality from 1 through 100.</summary>
    public int Quality { get; set; } = 85;

    /// <summary>Gets or sets chroma subsampling.</summary>
    public JpegChromaSubsampling ChromaSubsampling { get; set; } = JpegChromaSubsampling.Yuv420;

    /// <summary>Gets or sets the red channel used when flattening alpha.</summary>
    public byte BackgroundRed { get; set; } = 255;

    /// <summary>Gets or sets the green channel used when flattening alpha.</summary>
    public byte BackgroundGreen { get; set; } = 255;

    /// <summary>Gets or sets the blue channel used when flattening alpha.</summary>
    public byte BackgroundBlue { get; set; } = 255;
}

/// <summary>Settings for a resize operation.</summary>
public sealed class ResizeOptions
{
    /// <summary>Gets or sets the requested width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the requested height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets how the source is fitted to the requested dimensions.</summary>
    public ResizeMode Mode { get; set; } = ResizeMode.Contain;

    /// <summary>Gets or sets the reconstruction filter.</summary>
    public ResizeFilter Filter { get; set; } = ResizeFilter.Lanczos3;

    /// <summary>Gets or sets whether images may be enlarged.</summary>
    public bool AllowUpscale { get; set; }
}

/// <summary>Settings for a decode, transform, and encode operation.</summary>
public sealed class TransformOptions
{
    /// <summary>Gets or sets output encoder settings.</summary>
    public ImageEncoderOptions Encoder { get; set; } = new PngEncoderOptions();

    /// <summary>Gets or sets the user rotation applied after EXIF orientation.</summary>
    public ImageRotation Rotation { get; set; }

    /// <summary>Gets or sets optional resize settings.</summary>
    public ResizeOptions? Resize { get; set; }

    /// <summary>Gets or sets the maximum decoded source pixel count.</summary>
    public long MaxInputPixels { get; set; } = 100_000_000;

    /// <summary>Gets or sets the maximum accepted encoded input length.</summary>
    public long MaxInputBytes { get; set; } = 512L * 1024 * 1024;
}

/// <summary>Resource limits applied while reading encoded image input.</summary>
public sealed class ImageReadOptions
{
    /// <summary>Gets or sets the maximum decoded source pixel count.</summary>
    public long MaxInputPixels { get; set; } = 100_000_000;

    /// <summary>Gets or sets the maximum accepted encoded input length.</summary>
    public long MaxInputBytes { get; set; } = 512L * 1024 * 1024;
}
