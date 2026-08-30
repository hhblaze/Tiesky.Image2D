---
name: tiesky-image2d
description: Project-local guide for generating or reviewing .NET 8 code that uses Tiesky.Image2D for image inspection, rotation, resizing, and PNG/JPEG output. Do not use it for arbitrary-angle rotation, pixel-level editing, animation, or formats outside the library's public API.
metadata:
  short-description: Use Tiesky.Image2D correctly
---

# Tiesky.Image2D LLM usage guide

Use this guide when writing code against `src/Tiesky.Image2D`. This is a manually loaded project
guide, not an installed Codex `SKILL.md` package. Treat the public source in
`src/Tiesky.Image2D/Public` as authoritative if the API changes. Do not call types in internal or
codec namespaces from consumer code.

The library targets .NET 8 and exposes synchronous inspection and transformation APIs. File or
network I/O may be asynchronous in the consuming application, but `ImageInspector` and
`ImageTransformer` themselves are synchronous.

## Add the project reference

From the repository root, reference the library from a .NET 8 project:

```powershell
dotnet add .\path\to\Consumer.csproj reference .\src\Tiesky.Image2D\Tiesky.Image2D.csproj
```

Examples below assume these imports:

```csharp
using System;
using System.Buffers;
using System.IO;
using Tiesky.Image2D;
```

## Capabilities and boundaries

| Format | Decode | Encode | Important exclusions |
|---|---|---|---|
| JPEG | 8-bit baseline/progressive Huffman; gray, YCbCr/RGB, CMYK/YCCK | Baseline JPEG, YUV 4:4:4 or 4:2:0 | Arithmetic coding, 12-bit, multi-frame |
| PNG | Static color types 0/2/3/4/6, legal 1-16-bit depths, transparency, Adam7 | Static RGB8/RGBA8 | APNG transformation |
| BMP | Windows INFO/V4/V5, indexed/direct color, bitfields, RLE4/8, top-down | No | OS/2 and embedded JPEG/PNG |
| WebP | Static VP8, VP8L, VP8X/ALPH | No | Animation |

All four input families can be transformed to PNG or JPEG. BMP and WebP are decode-only output
sources; there is no BMP or WebP encoder.

The pipeline performs these stages:

1. Decode the input.
2. Apply TIFF/EXIF orientation automatically.
3. Apply public transformations in list order.
4. Encode with the required PNG or JPEG encoder.

Output metadata is stripped. Samples are treated as sRGB values without ICC conversion. The
library has no arbitrary-angle rotation, public pixel API, compositing/padding API, custom
transformation implementation, custom encoder implementation, or multi-frame output API.

`ImageInspector` can report APNG and animated WebP containers, but `ImageTransformer` cannot
transform them.

## Preferred transformation API

For a returned result, the modern parameter order is:

```text
input, transformations, encoder, readOptions
```

For a caller-owned destination, the order is:

```text
input, destination, transformations, encoder, readOptions
```

An empty transformation list is valid and performs decode/re-encode. Encoding is always a
separate, required argument in the modern API.

### Exact 128x128 PNG after clockwise rotation

Use `Cover` to preserve aspect ratio while filling the square. `AllowUpscale = true` guarantees
the requested dimensions even when the rotated source is smaller than 128 pixels on an axis.

```csharp
byte[] source = File.ReadAllBytes("input.png");

IImageTransformation[] operations =
[
    new RotateTransformation(ImageRotation.Clockwise90),
    new ResizeTransformation(new ResizeOptions
    {
        Width = 128,
        Height = 128,
        Mode = ResizeMode.Cover,
        Filter = ResizeFilter.Lanczos3,
        AllowUpscale = true,
    }),
];

byte[] thumbnail = ImageTransformer.Transform(
    source,
    operations,
    new PngEncoderOptions { CompressionLevel = 6 });

File.WriteAllBytes("thumbnail.png", thumbnail);
```

Rotation appears before resize because transformations execute in list order. Adjacent rotations
may be composed internally, and a rotation immediately followed by resize may use a fused path,
without changing observable ordering.

Only these built-in transformation classes are accepted:

- `RotateTransformation`
- `ResizeTransformation`

Implementing `IImageTransformation` in consumer code does not extend the pipeline; the call fails
with `ImageErrorCode.InvalidOptions`.

## Rotation parameters

`RotateTransformation` takes one `ImageRotation` value:

| Value | Behavior after automatic EXIF orientation |
|---|---|
| `ImageRotation.None` | No user rotation |
| `ImageRotation.Clockwise90` | 90 degrees clockwise |
| `ImageRotation.Clockwise180` | 180 degrees |
| `ImageRotation.CounterClockwise90` | 90 degrees counter-clockwise |

Only these quarter-turn values are supported.

## Resize parameters

```csharp
new ResizeOptions
{
    Width = 320,
    Height = 200,
    Mode = ResizeMode.Contain,
    Filter = ResizeFilter.Lanczos3,
    AllowUpscale = false,
}
```

`Width` and `Height` must both be positive. Their meaning depends on `Mode`.

### ResizeMode

| Mode | Aspect ratio | Output behavior |
|---|---|---|
| `ResizeMode.Contain` | Preserved | Fits the entire image inside the requested rectangle. Returns the fitted image dimensions and adds no padding. |
| `ResizeMode.Cover` | Preserved | Center-crops excess source area and fills the output rectangle. |
| `ResizeMode.Stretch` | Not preserved | Maps the full source independently onto the output width and height. |

For an 8x4 source and a 3x3 request with upscaling disabled:

- `Contain` returns 3x2; it does not create a 3x3 letterboxed canvas.
- `Cover` returns 3x3 and center-crops the wide source.
- `Stretch` returns 3x3 and changes the aspect ratio.

If exact dimensions and preserved aspect ratio are required, use `Cover`. If the complete image
must remain visible, use `Contain` and accept fitted dimensions. The library has no padding
operation; add a canvas with another tool if both full visibility and exact dimensions are needed.

### AllowUpscale

`AllowUpscale` defaults to `false`.

- With `Contain`, the uniform scale is capped at 1, so a small image is returned at its original
  size instead of being enlarged.
- With `Cover` or `Stretch`, requested width and height are independently capped by the source
  dimensions when upscaling is disabled.
- Set `AllowUpscale = true` when a requested output must be exact even if the source is smaller.

Consequently, `Cover` and `Stretch` guarantee the requested dimensions only when upscaling is
enabled or the source is already at least as large as the requested width and height.

### ResizeFilter

`Filter` defaults to `ResizeFilter.Lanczos3`.

| Filter | Kernel | Selection guidance |
|---|---|---|
| `ResizeFilter.Bilinear` | Linear, radius 1 | Lower-cost interpolation when speed matters more than detail retention |
| `ResizeFilter.Bicubic` | Keys cubic (`a = -0.5`), radius 2 | Middle ground between bilinear and Lanczos3 |
| `ResizeFilter.Lanczos3` | Windowed sinc, radius 3 | Default general-purpose choice, especially for thumbnails and downscaling |

The library widens filter support while shrinking and handles alpha during filtering to avoid
colored halos around transparent edges.

### Resize examples

Preserve the full image within an 800x800 bound:

```csharp
var contain = new ResizeTransformation(new ResizeOptions
{
    Width = 800,
    Height = 800,
    Mode = ResizeMode.Contain,
    Filter = ResizeFilter.Lanczos3,
    AllowUpscale = false,
});
```

Create an exact 320x180 center-cropped image, enlarging if necessary:

```csharp
var cover = new ResizeTransformation(new ResizeOptions
{
    Width = 320,
    Height = 180,
    Mode = ResizeMode.Cover,
    Filter = ResizeFilter.Lanczos3,
    AllowUpscale = true,
});
```

Create an exact 64x64 image without preserving aspect ratio:

```csharp
var stretch = new ResizeTransformation(new ResizeOptions
{
    Width = 64,
    Height = 64,
    Mode = ResizeMode.Stretch,
    Filter = ResizeFilter.Bicubic,
    AllowUpscale = true,
});
```

## Encoder parameters

The modern API accepts only `PngEncoderOptions` or `JpegEncoderOptions`. Encoding format is chosen
by the encoder type, not by an output filename.

### PNG

```csharp
var png = new PngEncoderOptions
{
    CompressionLevel = 6,
};
```

`CompressionLevel` is an integer from 0 through 9; default is 6. Higher values generally exchange
more encoding work for a smaller file. PNG preserves supported alpha.

### JPEG

```csharp
var jpeg = new JpegEncoderOptions
{
    Quality = 85,
    ChromaSubsampling = JpegChromaSubsampling.Yuv420,
    BackgroundRed = 255,
    BackgroundGreen = 255,
    BackgroundBlue = 255,
};
```

| Parameter | Range/default | Meaning |
|---|---|---|
| `Quality` | 1-100; default 85 | Visual quality setting |
| `ChromaSubsampling` | `Yuv420` default or `Yuv444` | 4:2:0 reduces chroma resolution; 4:4:4 retains full chroma resolution |
| `BackgroundRed` | byte; default 255 | Red channel used when flattening alpha |
| `BackgroundGreen` | byte; default 255 | Green channel used when flattening alpha |
| `BackgroundBlue` | byte; default 255 | Blue channel used when flattening alpha |

JPEG is opaque. The three background defaults flatten transparency against white. Encoded JPEG
dimensions cannot exceed 65,535 pixels on either axis.

## Input limits

Use `ImageReadOptions` with the modern API:

```csharp
var limits = new ImageReadOptions
{
    MaxInputPixels = 25_000_000,
    MaxInputBytes = 64L * 1024 * 1024,
};

byte[] result = ImageTransformer.Transform(
    source,
    [],
    new PngEncoderOptions(),
    limits);
```

| Parameter | Default | Constraint |
|---|---:|---|
| `MaxInputPixels` | 100,000,000 | Must be positive; limits the decoded source pixel count |
| `MaxInputBytes` | 512 MiB | Must be positive; limits encoded bytes read |

For `Stream` transformation input, `MaxInputBytes` must also be no greater than
`Int32.MaxValue`, because the current decoders require one bounded contiguous input buffer. Lower
both defaults for untrusted uploads according to the application's traffic envelope.

Options, transformations, encoder settings, and limits are snapshotted when a call begins; later
mutation of those objects does not affect the active call.

## Inspect without decoding pixels

`ImageInspector.Identify` accepts `byte[]`, `ReadOnlySpan<byte>`, or a readable `Stream`, plus an
optional `ImageReadOptions`:

```csharp
ImageInfo info = ImageInspector.Identify(source, limits);

Console.WriteLine($"{info.Format} ({info.MimeType})");
Console.WriteLine($"Visual: {info.Width}x{info.Height}");
Console.WriteLine($"Encoded: {info.EncodedWidth}x{info.EncodedHeight}");
Console.WriteLine($"EXIF: {info.ExifOrientation}; animated: {info.IsAnimated}");
```

- `Format` is `Jpeg`, `Png`, `Bmp`, or `WebP` for successful identification.
- `MimeType` is the corresponding canonical `image/*` MIME type.
- `Width` and `Height` are visual dimensions after EXIF orientation.
- `EncodedWidth` and `EncodedHeight` are dimensions of the stored pixel grid.
- `ExifOrientation` is `Normal` when no TIFF orientation is present.
- `IsAnimated` reports the APNG control chunk or WebP animation flag.

Identification validates decisive headers and limits, not the complete compressed payload. A
seekable stream is restored to its original position on success or failure. A non-seekable stream
stays open but consumes the header prefix needed for identification.

## Input and output ownership

| Result | API shape | Ownership and position |
|---|---|---|
| `byte[]` | `ImageTransformer.Transform(...)` | Returned array belongs to the caller |
| `MemoryStream` | `ImageTransformer.TransformToStream(...)` | Caller owns and disposes it; starts at position 0 |
| Caller `Stream` | `ImageTransformer.Transform(input, destination, ...)` | Remains open; output appends at its current position |
| `IBufferWriter<byte>` | `ImageTransformer.Transform(input, writer, ...)` | Remains caller-owned; encoded bytes are advanced into it |

Input streams are consumed from their current position and remain open. Output streams are not
repositioned. `Span<byte>` can be supplied as `ReadOnlySpan<byte>`, but there is no fixed output
span because encoded size is unknown; use `ArrayBufferWriter<byte>.WrittenSpan` when span-oriented
output is needed.

### Stream to stream

```csharp
using FileStream input = File.OpenRead("input.png");
using FileStream output = File.Create("output.jpg");

ImageTransformer.Transform(
    input,
    output,
    [new ResizeTransformation(new ResizeOptions
    {
        Width = 800,
        Height = 800,
        Mode = ResizeMode.Contain,
        Filter = ResizeFilter.Lanczos3,
    })],
    new JpegEncoderOptions { Quality = 82 });
```

### Returned MemoryStream

```csharp
using MemoryStream encoded = ImageTransformer.TransformToStream(
    source,
    [],
    new PngEncoderOptions());

using FileStream output = File.Create("copy.png");
encoded.CopyTo(output);
```

### IBufferWriter<byte>

```csharp
var writer = new ArrayBufferWriter<byte>();

ImageTransformer.Transform(
    source,
    writer,
    [new ResizeTransformation(new ResizeOptions
    {
        Width = 320,
        Height = 320,
        Mode = ResizeMode.Contain,
    })],
    new PngEncoderOptions { CompressionLevel = 3 });

ReadOnlySpan<byte> encodedPng = writer.WrittenSpan;
```

## Legacy TransformOptions API

`TransformOptions` remains supported when one user rotation and one optional resize are enough:

```csharp
byte[] jpegOutput = ImageTransformer.Transform(source, new TransformOptions
{
    Encoder = new JpegEncoderOptions
    {
        Quality = 85,
        ChromaSubsampling = JpegChromaSubsampling.Yuv420,
    },
    Rotation = ImageRotation.Clockwise90,
    Resize = new ResizeOptions
    {
        Width = 640,
        Height = 480,
        Mode = ResizeMode.Cover,
        Filter = ResizeFilter.Lanczos3,
        AllowUpscale = false,
    },
    MaxInputPixels = 25_000_000,
    MaxInputBytes = 64L * 1024 * 1024,
});
```

Legacy defaults are a PNG encoder, `ImageRotation.None`, no resize, 100,000,000 pixels, and
512 MiB. Prefer the modern transformation list when operation order or multiple operations must
be explicit.

## Errors and validation

Catch `Image2DException` when the application needs a stable image-processing category:

```csharp
try
{
    byte[] output = ImageTransformer.Transform(source, [], new PngEncoderOptions());
}
catch (Image2DException exception)
{
    Console.Error.WriteLine($"{exception.ErrorCode}: {exception.Message}");
}
```

| ErrorCode | Meaning |
|---|---|
| `UnknownFormat` | Input signature does not identify JPEG, PNG, BMP, or WebP |
| `UnsupportedFormat` | Declared stable category; the current implementation does not emit it |
| `UnsupportedFeature` | Recognized but unsupported codec feature, animation, or output dimension constraint |
| `InvalidData` | Malformed or inconsistent encoded image data |
| `PixelLimitExceeded` | Decoded source exceeds `MaxInputPixels` |
| `InvalidOptions` | Invalid limits, dimensions, enum values, encoder settings, null pipeline entries, or unsupported public implementations |
| `InputTooLarge` | Encoded input exceeds `MaxInputBytes` |
| `UnexpectedEndOfData` | Required encoded data ended prematurely |

Null arguments may also raise `ArgumentNullException`; unreadable streams may raise
`ArgumentException`; filesystem and stream failures may raise normal I/O exceptions. Do not
convert every failure into `Image2DException` in documentation or calling code.
