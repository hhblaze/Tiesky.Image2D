# Tiesky.Image2D

![Tiesky.Image2D project logo](https://raw.githubusercontent.com/hhblaze/Tiesky.Image2D/main/Deployment/logo.png)

`Tiesky.Image2D` is a focused .NET 8 image transform library for server-side load, EXIF orientation, 90-degree rotation, resize, and PNG/JPEG output. 

```csharp
using Tiesky.Image2D;

IImageTransformation[] operations =
[
    new RotateTransformation(ImageRotation.Clockwise90),
    new ResizeTransformation(new ResizeOptions
    {
        Width = 800,
        Height = 800,
        Mode = ResizeMode.Contain,
        Filter = ResizeFilter.Lanczos3,
    }),
];

byte[] thumbnail = ImageTransformer.Transform(
    sourceBytes,
    operations,
    new JpegEncoderOptions { Quality = 85 });

ImageInfo info = ImageInspector.Identify(sourceBytes);
Console.WriteLine($"{info.MimeType}: {info.Width}x{info.Height}");
```

Operations execute in list order after automatic EXIF orientation. Encoding is a required,
separate final parameter. An empty operation list performs decode/re-encode only. The original
`TransformOptions` API remains available as a compact convenience contract.

Input overloads accept `byte[]`, `ReadOnlySpan<byte>`, and `Stream`. Results can be returned as
`byte[]` or an owned `MemoryStream`, or written directly to a caller-owned `Stream` or
`IBufferWriter<byte>`. Caller-owned streams remain open; returned streams are positioned at zero.
The default input limit is 100 million decoded pixels and 512 MiB of encoded data. Metadata is
stripped on output.

Build and test from the solution root:

```powershell
dotnet build Tiesky.sln -c Release
dotnet run --project tests\Tiesky.Image2D.Tests -c Release
dotnet run --project examples\Tiesky.Image2D.Examples -c Release -- <input-image> [output-directory]
dotnet run --project benchmarks\Tiesky.Image2D.Benchmarks -c Release -- --suite all --warmups 5 --iterations 15 --baseline _Docs\Implementation\BenchmarkResults.html --html _Docs\Implementation\BenchmarkResults.html
```

The benchmark accepts `--suite jpeg,png,bmp,vp8l,vp8,encoders`, `--iterations`, `--warmups`, `--html`, and optional `--gate parity|png-parity|jpeg-parity|webp-parity|outperform` controls. JPEG, VP8L, and VP8 each contain 12 MP decode, predecoded rotation, predecoded resize, output-encode, and six thumbnail rows; PNG contains decode, rotation, encode, and six thumbnail rows. The codec parity gates require their component and transform rows to stay within 1.05x ImageSharp median latency and 1.10x peak private bytes while preserving captured quality and output size. The tracked, self-contained [benchmark report](_Docs/Implementation/BenchmarkResults.html) contains the complete 47-scenario matrix and embedded raw data. JPEG transform rows use their final Tiesky measurements as a 1.00x self-baseline; separate tables preserve real pre-optimization comparisons, while ImageSharp remains the primary opponent. WebP remains decode-only: its output-encode rows are explicitly VP8L→PNG level 6 and VP8→JPEG quality 85.

The [examples](examples/README.md) cover every ownership model. The
[LLM usage guide](_Docs/LLM/tiesky_image2D_skill.md) documents transformation parameters,
resize semantics, ownership contracts, limits, and complete code examples for assistants working
with this library. Implementation notes and extension invariants live in `_Docs/Implementation`
and must be updated with architectural or algorithmic changes.
