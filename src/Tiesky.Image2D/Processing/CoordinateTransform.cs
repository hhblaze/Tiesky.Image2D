using System.Runtime.CompilerServices;
using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Processing;

/// <summary>Maps post-orientation coordinates directly into decoded storage.</summary>
internal readonly struct CoordinateTransform
{
    private readonly int rawWidth;
    private readonly int rawHeight;
    private readonly int orientedWidth;
    private readonly int orientedHeight;
    private readonly ExifOrientation orientation;
    private readonly ImageRotation rotation;

    /// <summary>Initializes a fused EXIF and user rotation mapping.</summary>
    public CoordinateTransform(int rawWidth, int rawHeight, ExifOrientation orientation, ImageRotation rotation)
    {
        this.rawWidth = rawWidth;
        this.rawHeight = rawHeight;
        this.orientation = orientation;
        this.rotation = rotation;
        bool swapsExifAxes = orientation is ExifOrientation.MirrorHorizontalRotate270 or ExifOrientation.Rotate90 or ExifOrientation.MirrorHorizontalRotate90 or ExifOrientation.Rotate270;
        orientedWidth = swapsExifAxes ? rawHeight : rawWidth;
        orientedHeight = swapsExifAxes ? rawWidth : rawHeight;
        bool swapsRotationAxes = rotation is ImageRotation.Clockwise90 or ImageRotation.CounterClockwise90;
        Width = swapsRotationAxes ? orientedHeight : orientedWidth;
        Height = swapsRotationAxes ? orientedWidth : orientedHeight;
    }

    /// <summary>Gets the final logical width.</summary>
    public int Width { get; }

    /// <summary>Gets the final logical height.</summary>
    public int Height { get; }

    /// <summary>Maps a final logical coordinate to a raw decoded coordinate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Map(int x, int y, out int rawX, out int rawY)
    {
        int orientedX;
        int orientedY;
        switch (rotation)
        {
            case ImageRotation.Clockwise90:
                orientedX = y;
                orientedY = orientedHeight - 1 - x;
                break;
            case ImageRotation.Clockwise180:
                orientedX = orientedWidth - 1 - x;
                orientedY = orientedHeight - 1 - y;
                break;
            case ImageRotation.CounterClockwise90:
                orientedX = orientedWidth - 1 - y;
                orientedY = x;
                break;
            default:
                orientedX = x;
                orientedY = y;
                break;
        }

        switch (orientation)
        {
            case ExifOrientation.MirrorHorizontal:
                rawX = rawWidth - 1 - orientedX;
                rawY = orientedY;
                break;
            case ExifOrientation.Rotate180:
                rawX = rawWidth - 1 - orientedX;
                rawY = rawHeight - 1 - orientedY;
                break;
            case ExifOrientation.MirrorVertical:
                rawX = orientedX;
                rawY = rawHeight - 1 - orientedY;
                break;
            case ExifOrientation.MirrorHorizontalRotate270:
                rawX = orientedY;
                rawY = orientedX;
                break;
            case ExifOrientation.Rotate90:
                rawX = orientedY;
                rawY = rawHeight - 1 - orientedX;
                break;
            case ExifOrientation.MirrorHorizontalRotate90:
                rawX = rawWidth - 1 - orientedY;
                rawY = rawHeight - 1 - orientedX;
                break;
            case ExifOrientation.Rotate270:
                rawX = rawWidth - 1 - orientedY;
                rawY = orientedX;
                break;
            default:
                rawX = orientedX;
                rawY = orientedY;
                break;
        }
    }
}
