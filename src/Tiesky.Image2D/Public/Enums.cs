namespace Tiesky.Image2D;

/// <summary>Specifies the clockwise rotation applied after EXIF orientation.</summary>
public enum ImageRotation
{
    None = 0,
    Clockwise90 = 1,
    Clockwise180 = 2,
    CounterClockwise90 = 3,
}

/// <summary>Identifies an encoded image container.</summary>
public enum ImageFormat
{
    Unknown = 0,
    Jpeg = 1,
    Png = 2,
    Bmp = 3,
    WebP = 4,
}

/// <summary>Defines the eight TIFF/EXIF orientation transforms.</summary>
public enum ExifOrientation
{
    Normal = 1,
    MirrorHorizontal = 2,
    Rotate180 = 3,
    MirrorVertical = 4,
    MirrorHorizontalRotate270 = 5,
    Rotate90 = 6,
    MirrorHorizontalRotate90 = 7,
    Rotate270 = 8,
}

/// <summary>Specifies how source dimensions map to the requested rectangle.</summary>
public enum ResizeMode
{
    Contain = 0,
    Cover = 1,
    Stretch = 2,
}

/// <summary>Specifies the reconstruction filter used by resize.</summary>
public enum ResizeFilter
{
    Bilinear = 0,
    Bicubic = 1,
    Lanczos3 = 2,
}

/// <summary>Identifies a stable failure category.</summary>
public enum ImageErrorCode
{
    UnknownFormat = 1,
    UnsupportedFormat = 2,
    UnsupportedFeature = 3,
    InvalidData = 4,
    PixelLimitExceeded = 5,
    InvalidOptions = 6,
    InputTooLarge = 7,
    UnexpectedEndOfData = 8,
}

/// <summary>Specifies JPEG chroma subsampling.</summary>
public enum JpegChromaSubsampling
{
    Yuv444 = 0,
    Yuv420 = 1,
}
