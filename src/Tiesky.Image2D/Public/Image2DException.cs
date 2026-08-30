namespace Tiesky.Image2D;

/// <summary>Represents a stable image processing failure.</summary>
public sealed class Image2DException : Exception
{
    /// <summary>Initializes an image processing exception.</summary>
    public Image2DException(ImageErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Initializes an image processing exception with an underlying cause.</summary>
    public Image2DException(ImageErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets the stable failure category.</summary>
    public ImageErrorCode ErrorCode { get; }
}
