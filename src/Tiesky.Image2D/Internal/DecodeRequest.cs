namespace Tiesky.Image2D.Internal;

/// <summary>Supplies transform geometry that a decoder may use for safe native downscaling.</summary>
internal readonly record struct DecodeRequest(ImageRotation Rotation, ResizeOptions? Resize);
