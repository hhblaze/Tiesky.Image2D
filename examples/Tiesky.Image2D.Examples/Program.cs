using System.Buffers;
using Tiesky.Image2D;

CreateRequestedThumbnail();
return 0;


if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: Tiesky.Image2D.Examples <input-image> [output-directory]");
    return 1;
}

string inputPath = Path.GetFullPath(args[0]);
string outputDirectory = Path.GetFullPath(args.Length == 2 ? args[1] : Path.Combine(Environment.CurrentDirectory, "image2d-output"));
Directory.CreateDirectory(outputDirectory);

byte[] sourceBytes = File.ReadAllBytes(inputPath);
ImageInfo info = ImageInspector.Identify(sourceBytes);
Console.WriteLine($"{info.Format} ({info.MimeType}), visual {info.Width}x{info.Height}, encoded {info.EncodedWidth}x{info.EncodedHeight}, EXIF {info.ExifOrientation}");

IImageTransformation[] thumbnailPipeline =
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

// Explicit byte[] input and byte[] output.
byte[] jpegThumbnail = ImageTransformer.Transform(
    sourceBytes,
    thumbnailPipeline,
    new JpegEncoderOptions { Quality = 85, ChromaSubsampling = JpegChromaSubsampling.Yuv420 });
File.WriteAllBytes(Path.Combine(outputDirectory, "byte-array-thumbnail.jpg"), jpegThumbnail);

// Span input uses the same allocation-free input path.
ReadOnlySpan<byte> sourceSpan = sourceBytes;
byte[] pngThumbnail = ImageTransformer.Transform(
    sourceSpan,
    thumbnailPipeline,
    new PngEncoderOptions { CompressionLevel = 6 });
File.WriteAllBytes(Path.Combine(outputDirectory, "span-thumbnail.png"), pngThumbnail);

// The returned MemoryStream is owned by the caller and starts at Position 0.
using (MemoryStream encodedStream = ImageTransformer.TransformToStream(
    sourceBytes,
    Array.Empty<IImageTransformation>(),
    new PngEncoderOptions()))
using (FileStream file = File.Create(Path.Combine(outputDirectory, "returned-stream.png")))
{
    encodedStream.CopyTo(file);
}

// Caller-owned input and output streams remain open. Output starts at its current position.
using (FileStream input = File.OpenRead(inputPath))
using (FileStream output = File.Create(Path.Combine(outputDirectory, "stream-to-stream.jpg")))
{
    ImageTransformer.Transform(
        input,
        output,
        [new ResizeTransformation(new ResizeOptions { Width = 400, Height = 400, Mode = ResizeMode.Contain })],
        new JpegEncoderOptions { Quality = 82 });
}

// IBufferWriter<byte> exposes WrittenSpan without adding a stream-sized staging copy.
ArrayBufferWriter<byte> writer = new();
ImageTransformer.Transform(
    sourceBytes,
    writer,
    [new ResizeTransformation(new ResizeOptions { Width = 320, Height = 320, Mode = ResizeMode.Contain })],
    new PngEncoderOptions { CompressionLevel = 3 });
using (FileStream file = File.Create(Path.Combine(outputDirectory, "buffer-writer.png")))
{
    file.Write(writer.WrittenSpan);
}

// Identification restores a seekable stream's position.
using (FileStream seekable = File.OpenRead(inputPath))
{
    long originalPosition = seekable.Position;
    ImageInfo streamInfo = ImageInspector.Identify(seekable);
    Console.WriteLine($"Seekable identify: {streamInfo.Width}x{streamInfo.Height}; position restored: {seekable.Position == originalPosition}");
}

// A non-seekable stream remains open but consumes the header prefix it inspected.
using (NonSeekableReadStream nonSeekable = new(sourceBytes))
{
    ImageInfo streamInfo = ImageInspector.Identify(nonSeekable);
    Console.WriteLine($"Non-seekable identify: {streamInfo.Format} {streamInfo.Width}x{streamInfo.Height}");
}

// The original TransformOptions convenience API remains supported.
byte[] legacyStyle = ImageTransformer.Transform(sourceBytes, new TransformOptions
{
    Encoder = new PngEncoderOptions(),
    Rotation = ImageRotation.Clockwise180,
    Resize = new ResizeOptions { Width = 200, Height = 200, Mode = ResizeMode.Contain },
});
File.WriteAllBytes(Path.Combine(outputDirectory, "transform-options.png"), legacyStyle);

Console.WriteLine($"Wrote examples to {outputDirectory}");
return 0;

static void CreateRequestedThumbnail()
{
    const string inputPath = @"D:\VS\temp\Sixlabortest\tests\Narayana.png";
    const string outputPath = @"D:\VS\temp\Sixlabortest\tests\narayana_thumb.png";

    byte[] sourceBytes = File.ReadAllBytes(inputPath);
    byte[] thumbnail = ImageTransformer.Transform(
        sourceBytes,
        [
            new RotateTransformation(ImageRotation.Clockwise90),
            new ResizeTransformation(new ResizeOptions
            {
                Width = 800,
                Height = 800,
               // Mode = ResizeMode.Stretch,
                Mode = ResizeMode.Contain,
                Filter = ResizeFilter.Lanczos3,
            }),
        ],
        new PngEncoderOptions());

    File.WriteAllBytes(outputPath, thumbnail);
    Console.WriteLine($"Wrote requested thumbnail to {outputPath}");
}

file sealed class NonSeekableReadStream : Stream
{
    private readonly MemoryStream inner;

    public NonSeekableReadStream(byte[] bytes) => inner = new MemoryStream(bytes, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
