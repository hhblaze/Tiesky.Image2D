using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

if (args.Length == 3 && args[0] == "--generate-webp-manifest")
{
    string fixtureDirectory = Path.GetFullPath(args[1]);
    string outputPath = Path.GetFullPath(args[2]);
    List<WebpOracleEntry> entries = [];
    foreach (string inputPath in Directory.GetFiles(fixtureDirectory, "*.webp").Order(StringComparer.Ordinal))
    {
        try
        {
            byte[] encoded = File.ReadAllBytes(inputPath);
            if (encoded.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4)) + 8u > encoded.Length)
            {
                Console.Error.WriteLine($"Skipping truncated WebP {Path.GetFileName(inputPath)}.");
                continue;
            }

            using Image<Rgba32> oracleImage = Image.Load<Rgba32>(inputPath);
            byte[] pixels = new byte[checked(oracleImage.Width * oracleImage.Height * 4)];
            oracleImage.CopyPixelDataTo(pixels);
            bool opaque = true;
            for (int offset = 3; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset] != 255) { opaque = false; break; }
            }

            entries.Add(new WebpOracleEntry(
                Path.GetFileName(inputPath),
                DetectWebpCodec(encoded),
                oracleImage.Width,
                oracleImage.Height,
                opaque,
                Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant()));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Skipping invalid WebP {Path.GetFileName(inputPath)}: {exception.Message}");
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote {entries.Count} WebP oracle entries to {outputPath}.");
    return 0;
}

if (args.Length == 3 && args[0] == "--generate-benchmark-corpus")
{
    string sourcePath = Path.GetFullPath(args[1]);
    string outputDirectory = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(outputDirectory);

    using Image<Rgba32> source = Image.Load<Rgba32>(sourcePath);
    source.Mutate(context => context.AutoOrient());
    if (source.Width != 4000 || source.Height != 3000)
    {
        throw new InvalidOperationException($"Benchmark source must be 4000x3000, but was {source.Width}x{source.Height}.");
    }

    source.Save(Path.Combine(outputDirectory, "benchmark-12mp.png"), new PngEncoder
    {
        BitDepth = PngBitDepth.Bit8,
        ColorType = PngColorType.Rgb,
        CompressionLevel = PngCompressionLevel.Level6,
    });
    source.Save(Path.Combine(outputDirectory, "benchmark-12mp.bmp"), new BmpEncoder
    {
        BitsPerPixel = BmpBitsPerPixel.Pixel24,
    });
    source.Save(Path.Combine(outputDirectory, "benchmark-12mp-lossy.webp"), new WebpEncoder
    {
        FileFormat = WebpFileFormatType.Lossy,
        Quality = 85,
        Method = WebpEncodingMethod.Default,
    });
    source.Save(Path.Combine(outputDirectory, "benchmark-12mp-lossless.webp"), new WebpEncoder
    {
        FileFormat = WebpFileFormatType.Lossless,
        Quality = 75,
        Method = WebpEncodingMethod.Default,
    });

    using Image<Rgba32> encoderSource = source.Clone(context => context.Resize(1600, 1200));
    byte[] rgba = new byte[checked(8 + encoderSource.Width * encoderSource.Height * 4)];
    BinaryPrimitives.WriteInt32LittleEndian(rgba, encoderSource.Width);
    BinaryPrimitives.WriteInt32LittleEndian(rgba.AsSpan(4), encoderSource.Height);
    encoderSource.CopyPixelDataTo(rgba.AsSpan(8));
    File.WriteAllBytes(Path.Combine(outputDirectory, "encoder-1600x1200.rgba"), rgba);

    Console.WriteLine($"Generated PNG, 24-bit BMP, lossy VP8, lossless VP8L, and encoder RGBA fixtures in {outputDirectory}.");
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: oracle <input-image> <output-rgba>");
    Console.Error.WriteLine("       oracle --generate-benchmark-corpus <4000x3000-source> <output-directory>");
    return 2;
}

using Image<Rgba32> image = Image.Load<Rgba32>(args[0]);
image.Mutate(context => context.AutoOrient());
byte[] output = new byte[checked(8 + image.Width * image.Height * 4)];
BinaryPrimitives.WriteInt32LittleEndian(output, image.Width);
BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), image.Height);
image.CopyPixelDataTo(output.AsSpan(8));
File.WriteAllBytes(args[1], output);
Console.WriteLine($"{image.Width}x{image.Height} -> {args[1]}");
return 0;

static string DetectWebpCodec(ReadOnlySpan<byte> input)
{
    if (input.Length < 20 || !input[..4].SequenceEqual("RIFF"u8) || !input.Slice(8, 4).SequenceEqual("WEBP"u8))
        return "unknown";
    int offset = 12;
    while (offset <= input.Length - 8)
    {
        ReadOnlySpan<byte> type = input.Slice(offset, 4);
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(offset + 4, 4)));
        if (type.SequenceEqual("VP8L"u8)) return "VP8L";
        if (type.SequenceEqual("VP8 "u8)) return "VP8";
        int padded = checked(length + (length & 1));
        if (offset > input.Length - 8 - padded) break;
        offset += 8 + padded;
    }

    return "unknown";
}

internal sealed record WebpOracleEntry(
    string File,
    string Codec,
    int Width,
    int Height,
    bool Opaque,
    string ReferenceRgbaSha256,
    string? DecoderRgbaSha256 = null);
