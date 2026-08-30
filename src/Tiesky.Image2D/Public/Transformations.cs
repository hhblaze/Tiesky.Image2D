namespace Tiesky.Image2D;

/// <summary>
/// Marks a built-in image transformation that can participate in an ordered pipeline.
/// Custom implementations are not supported in version 1.
/// </summary>
public interface IImageTransformation
{
}

/// <summary>Applies a discrete rotation to the current image.</summary>
public sealed class RotateTransformation : IImageTransformation
{
    /// <summary>Initializes a rotation transformation.</summary>
    public RotateTransformation(ImageRotation rotation) => Rotation = rotation;

    /// <summary>Gets the requested clockwise rotation.</summary>
    public ImageRotation Rotation { get; }
}

/// <summary>Resizes the current image using the supplied options.</summary>
public sealed class ResizeTransformation : IImageTransformation
{
    /// <summary>Initializes a resize transformation.</summary>
    public ResizeTransformation(ResizeOptions options) => Options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Gets the resize configuration.</summary>
    public ResizeOptions Options { get; }
}
