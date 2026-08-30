using System.Runtime.InteropServices;

namespace Tiesky.Image2D.Internal;

/// <summary>Owns one 32-byte-aligned, tightly packed RGB24 or RGBA32 image.</summary>
internal sealed unsafe class PixelBuffer : IDisposable
{
    private byte* pointer;

    /// <summary>Allocates a zeroed pixel buffer after checked dimension validation.</summary>
    public PixelBuffer(int width, int height, int bytesPerPixel = 4)
    {
        if (width <= 0 || height <= 0)
        {
            ThrowHelper.InvalidData("Image dimensions must be positive.");
        }

        if (bytesPerPixel is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel));
        }

        nuint length;
        try
        {
            length = checked((nuint)width * (nuint)height * (nuint)bytesPerPixel);
        }
        catch (OverflowException exception)
        {
            throw new Image2DException(ImageErrorCode.InvalidData, "Image dimensions overflow the address space.", exception);
        }

        pointer = (byte*)NativeMemory.AlignedAlloc(length, 32);
        if (pointer is null)
        {
            throw new OutOfMemoryException();
        }

        NativeMemory.Clear(pointer, length);
        Width = width;
        Height = height;
        BytesPerPixel = bytesPerPixel;
        Length = checked((int)length);
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the tightly packed pixel stride (RGB24 or RGBA32).</summary>
    public int BytesPerPixel { get; }

    /// <summary>Gets the byte length.</summary>
    public int Length { get; }

    /// <summary>Gets the mutable underlying bytes for the lifetime of this owner.</summary>
    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(pointer is null, this);
            return new Span<byte>(pointer, Length);
        }
    }

    /// <summary>Gets a mutable row without allocating.</summary>
    public Span<byte> GetRowSpan(int y)
    {
        int rowLength = checked(Width * BytesPerPixel);
        return Span.Slice(checked(y * rowLength), rowLength);
    }

    /// <summary>Releases the unmanaged allocation.</summary>
    public void Dispose()
    {
        byte* value = pointer;
        pointer = null;
        if (value is not null)
        {
            NativeMemory.AlignedFree(value);
        }
    }
}
