using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Tiesky.Image2D;
using Tiesky.Image2D.Codecs;
using Tiesky.Image2D.Internal;

return args.Length == 2 && args[0] == "--update-webp-decoder-hashes"
    ? TestRunner.UpdateWebpDecoderHashes(args[1])
    : TestRunner.Run();

internal static class TestRunner
{
    private static readonly (string Name, Action Test)[] Tests =
    [
        ("PNG RGBA identity", PngIdentity),
        ("PNG static format matrix", PngFormatMatrix),
        ("PNG opaque RGB filters and split IDAT", PngOpaqueRgbFastPaths),
        ("Rotate clockwise and counterclockwise", Rotations),
        ("Resize contain, cover, and stretch", ResizeModes),
        ("Bilinear, bicubic, and Lanczos3 filters", ResizeFilters),
        ("BMP 24-bit bottom-up", Bmp24),
        ("BMP indexed 8-bit", BmpIndexed),
        ("BMP Windows format matrix", BmpFormatMatrix),
        ("JPEG 4:4:4 round-trip", JpegRoundTrip444),
        ("JPEG 4:2:0 round-trip", JpegRoundTrip420),
        ("JPEG baseline/progressive reference corpus", JpegReferenceCorpus),
        ("JPEG native thumbnail reduction", JpegNativeThumbnailReduction),
        ("EXIF orientations 1 through 8", ExifOrientations),
        ("Alpha is flattened for JPEG", JpegAlphaFlattening),
        ("Streams remain open", StreamsRemainOpen),
        ("Ordered pipeline input and output contracts", OrderedPipelineContracts),
        ("Ordered pipeline sequencing and validation", OrderedPipelineSequencing),
        ("Header-only image information", ImageInformation),
        ("Stable errors and limits", ErrorsAndLimits),
        ("Thirty-two concurrent transforms", ConcurrentTransforms),
        ("Scalar and SIMD rows and fixtures are identical", SimdIdentity),
        ("Serial and adaptive-parallel transforms are identical", ParallelIdentity),
        ("Decoder opacity classification", DecoderOpacityClassification),
        ("Native working set remains bounded", NativeMemoryStress),
        ("VP8L lossless reference vectors", Vp8lReferenceVectors),
        ("VP8 lossy reference vector", Vp8LossyReferenceVector),
        ("WebP oracle manifest", WebpOracleManifest),
        ("Animated WebP is rejected", AnimatedWebPRejected),
    ];

    public static int Run()
    {
        int failures = 0;
        foreach ((string name, Action test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    public static int UpdateWebpDecoderHashes(string manifestPath)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        List<WebpOracleEntry> entries = JsonSerializer.Deserialize<List<WebpOracleEntry>>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("The WebP oracle manifest is empty.");
        string directory = Path.GetDirectoryName(manifestPath)!;
        foreach (WebpOracleEntry entry in entries)
        {
            RawImage decoded = DecodeWebpRgba(Path.Combine(directory, entry.File));
            entry.DecoderRgbaSha256 = HashPixels(decoded.Pixels);
        }

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Updated {entries.Count} Tiesky decoder hashes in {manifestPath}.");
        return 0;
    }

    private static void PngIdentity()
    {
        RawImage source = CreatePattern(7, 5, alpha: true);
        byte[] encoded = PngFixture.Encode(source);
        byte[] result = ImageTransformer.Transform(encoded, new TransformOptions { Encoder = new PngEncoderOptions() });
        RawImage actual = PngFixture.Decode(result);
        Equal(source.Width, actual.Width, "width");
        Equal(source.Height, actual.Height, "height");
        SequenceEqual(source.Pixels, actual.Pixels, "RGBA pixels");
        Equal(6, PngFixture.GetColorType(result), "transparent output color type");
    }

    private static void PngOpaqueRgbFastPaths()
    {
        foreach (int width in new[] { 1, 3, 17, 53 })
        foreach (int filter in Enumerable.Range(0, 5))
        {
            RawImage source = CreatePattern(width, 19, alpha: false);
            byte[] encoded = PngFixture.EncodeRgb(source, filter, idatChunkSize: 17);
            using DecodedImage decoded = ImageDecoder.Decode(encoded, 100_000_000, new DecodeRequest(ImageRotation.None, null));
            Equal(3, decoded.Pixels.BytesPerPixel, $"RGB storage {width}/{filter}");
            True(decoded.IsOpaque, $"RGB opacity {width}/{filter}");

            byte[] result = ImageTransformer.Transform(encoded, new TransformOptions { Encoder = new PngEncoderOptions { CompressionLevel = 6 } });
            RawImage actual = PngFixture.Decode(result);
            SequenceEqual(source.Pixels, actual.Pixels, $"RGB pixels {width}/{filter}");
            Equal(2, PngFixture.GetColorType(result), $"opaque output color type {width}/{filter}");
        }

        RawImage simdSource = CreatePattern(257, 63, alpha: false);
        byte[] paeth = PngFixture.EncodeRgb(simdSource, filter: 4, idatChunkSize: 31);
        try
        {
            SimdPrimitives.ForcedMode = SimdMode.Scalar;
            byte[] scalar = ImageTransformer.Transform(paeth, new TransformOptions { Encoder = new PngEncoderOptions() });
            foreach (SimdMode mode in new[] { SimdMode.Ssse3, SimdMode.Avx2, SimdMode.Automatic })
            {
                SimdPrimitives.ForcedMode = mode;
                SequenceEqual(scalar, ImageTransformer.Transform(paeth, new TransformOptions { Encoder = new PngEncoderOptions() }), $"RGB Paeth {mode}");
            }
        }
        finally
        {
            SimdPrimitives.ForcedMode = SimdMode.Automatic;
        }
    }

    private static void PngFormatMatrix() => AssertLosslessReferenceCorpus("png", "*.png");

    private static void BmpFormatMatrix() => AssertLosslessReferenceCorpus("bmp", "*.bmp");

    private static void AssertLosslessReferenceCorpus(string directoryName, string pattern)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fixtures", directoryName);
        foreach (string path in Directory.GetFiles(directory, pattern))
        {
            string name = Path.GetFileName(path);
            if (name.Equals("pal2.bmp", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 2-bpp BMP is deliberately outside the v1 1/4/8-bpp palette contract.
            }

            try
            {
                RawImage actual = PngFixture.Decode(ImageTransformer.Transform(File.ReadAllBytes(path), new TransformOptions { Encoder = new PngEncoderOptions() }));
                byte[] expected = File.ReadAllBytes(Path.ChangeExtension(path, ".rgba"));
                Equal(BinaryPrimitives.ReadInt32LittleEndian(expected), actual.Width, $"{name} width");
                Equal(BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(4)), actual.Height, $"{name} height");
                SequenceEqual(expected.AsSpan(8), actual.Pixels, $"{name} pixels");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Reference fixture {name} failed.", exception);
            }
        }
    }

    private static void SimdIdentity()
    {
        byte[] source = Enumerable.Range(0, 1036).Select(i => (byte)((i * 73 + 19) & 255)).ToArray();
        byte[] expectedCopy = source.ToArray();
        byte[] expectedReverse = new byte[source.Length];
        for (int i = 0; i < source.Length; i += 4)
        {
            source.AsSpan(source.Length - i - 4, 4).CopyTo(expectedReverse.AsSpan(i, 4));
        }

        foreach (SimdMode mode in Enum.GetValues<SimdMode>())
        {
            byte[] copied = new byte[source.Length];
            byte[] reversed = new byte[source.Length];
            SimdPrimitives.Copy(source, copied, mode);
            SimdPrimitives.ReversePixels(source, reversed, mode);
            SequenceEqual(expectedCopy, copied, $"{mode} copy");
            SequenceEqual(expectedReverse, reversed, $"{mode} reverse");
        }

        string webp = Path.Combine(AppContext.BaseDirectory, "fixtures", "webp");
        byte[][] fixtures =
        [
            PngFixture.Encode(CreatePattern(43, 31, alpha: true)),
            BmpFixture.Encode24(CreatePattern(43, 31, alpha: false)),
            ImageTransformer.Transform(PngFixture.Encode(CreatePattern(43, 31, alpha: false)), new TransformOptions
            {
                Encoder = new JpegEncoderOptions { Quality = 91, ChromaSubsampling = JpegChromaSubsampling.Yuv420 },
            }),
            File.ReadAllBytes(Path.Combine(webp, "lossless_vec_2_6.webp")),
            File.ReadAllBytes(Path.Combine(webp, "bike_lossy_small.webp")),
        ];
        TransformOptions options = new()
        {
            Encoder = new PngEncoderOptions { CompressionLevel = 6 },
            Rotation = ImageRotation.Clockwise90,
            Resize = new ResizeOptions { Width = 37, Height = 29, Mode = ResizeMode.Stretch, Filter = ResizeFilter.Lanczos3 },
        };
        try
        {
            SimdPrimitives.ForcedMode = SimdMode.Scalar;
            byte[][] scalar = fixtures.Select(input => ImageTransformer.Transform(input, options)).ToArray();
            foreach (SimdMode mode in Enum.GetValues<SimdMode>())
            {
                SimdPrimitives.ForcedMode = mode;
                for (int i = 0; i < fixtures.Length; i++)
                {
                    SequenceEqual(scalar[i], ImageTransformer.Transform(fixtures[i], options), $"{mode} full fixture {i}");
                }
            }
        }
        finally
        {
            SimdPrimitives.ForcedMode = SimdMode.Automatic;
        }
    }

    private static void ParallelIdentity()
    {
        RawImage source = CreatePattern(1400, 1000, alpha: false);
        byte[] encoded = PngFixture.EncodeRgb(source, filter: 4);
        byte[] encodedJpeg = ImageTransformer.Transform(encoded, new TransformOptions
        {
            Encoder = new JpegEncoderOptions { Quality = 91, ChromaSubsampling = JpegChromaSubsampling.Yuv420 },
        });
        TransformOptions[] workloads =
        [
            new TransformOptions { Encoder = new PngEncoderOptions { CompressionLevel = 6 }, Rotation = ImageRotation.Clockwise90 },
            new TransformOptions
            {
                Encoder = new PngEncoderOptions { CompressionLevel = 6 },
                Rotation = ImageRotation.Clockwise90,
                Resize = new ResizeOptions { Width = 1000, Height = 800, Mode = ResizeMode.Stretch, Filter = ResizeFilter.Lanczos3 },
            },
            new TransformOptions
            {
                Encoder = new JpegEncoderOptions { Quality = 85, ChromaSubsampling = JpegChromaSubsampling.Yuv420 },
                Rotation = ImageRotation.Clockwise90,
                Resize = new ResizeOptions { Width = 1000, Height = 800, Mode = ResizeMode.Stretch, Filter = ResizeFilter.Lanczos3 },
            },
        ];

        try
        {
            ParallelExecution.ForceSequential = true;
            byte[][] serial = workloads.Select(options => ImageTransformer.Transform(encoded, options)).ToArray();
            ParallelExecution.ForceSequential = false;
            for (int i = 0; i < workloads.Length; i++)
            {
                SequenceEqual(serial[i], ImageTransformer.Transform(encoded, workloads[i]), $"parallel workload {i}");
            }

            TransformOptions jpegDecode = new() { Encoder = new PngEncoderOptions { CompressionLevel = 6 } };
            ParallelExecution.ForceSequential = true;
            byte[] serialJpegDecode = ImageTransformer.Transform(encodedJpeg, jpegDecode);
            ParallelExecution.ForceSequential = false;
            SequenceEqual(serialJpegDecode, ImageTransformer.Transform(encodedJpeg, jpegDecode), "parallel JPEG decode pipeline");

            string webp = Path.Combine(AppContext.BaseDirectory, "fixtures", "webp");
            TransformOptions webpOptions = new()
            {
                Encoder = new PngEncoderOptions { CompressionLevel = 6 },
                Rotation = ImageRotation.Clockwise90,
                Resize = new ResizeOptions { Width = 800, Height = 800, Mode = ResizeMode.Contain, Filter = ResizeFilter.Lanczos3 },
            };
            foreach (string name in new[] { "benchmark-12mp-lossless.webp", "benchmark-12mp-lossy.webp" })
            {
                byte[] input = File.ReadAllBytes(Path.Combine(webp, name));
                ParallelExecution.ForceSequential = true;
                byte[] serialWebp = ImageTransformer.Transform(input, webpOptions);
                ParallelExecution.ForceSequential = false;
                SequenceEqual(serialWebp, ImageTransformer.Transform(input, webpOptions), $"parallel {name} decode pipeline");
            }
        }
        finally
        {
            ParallelExecution.ForceSequential = false;
        }
    }

    private static void DecoderOpacityClassification()
    {
        AssertOpacity(PngFixture.Encode(CreatePattern(19, 13, alpha: false)), expected: true, "opaque RGBA PNG");
        AssertOpacity(PngFixture.Encode(CreatePattern(19, 13, alpha: true)), expected: false, "transparent RGBA PNG");
        AssertOpacity(BmpFixture.Encode24(CreatePattern(19, 13, alpha: false)), expected: true, "24-bit BMP");

        string webp = Path.Combine(AppContext.BaseDirectory, "fixtures", "webp");
        AssertOpacity(File.ReadAllBytes(Path.Combine(webp, "bike_lossy_small.webp")), expected: true, "VP8 without ALPH");
        AssertOpacity(File.ReadAllBytes(Path.Combine(webp, "alpha_no_compression.webp")), expected: false, "VP8 with ALPH");
        AssertOpacity(File.ReadAllBytes(Path.Combine(webp, "lossless_alpha_small.webp")), expected: false, "transparent VP8L");
    }

    private static void AssertOpacity(byte[] encoded, bool expected, string name)
    {
        using DecodedImage decoded = ImageDecoder.Decode(encoded, 100_000_000, new DecodeRequest(ImageRotation.None, null));
        bool pixelsAreOpaque = true;
        ReadOnlySpan<byte> pixels = decoded.Pixels.Span;
        for (int offset = 3; decoded.Pixels.BytesPerPixel == 4 && offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != 255)
            {
                pixelsAreOpaque = false;
                break;
            }
        }

        Equal(expected, pixelsAreOpaque, $"{name} actual alpha");
        Equal(expected, decoded.IsOpaque, $"{name} classification");
    }

    private static void Rotations()
    {
        RawImage source = CreatePattern(3, 2, alpha: false);
        byte[] encoded = PngFixture.Encode(source);
        RawImage clockwise = PngFixture.Decode(ImageTransformer.Transform(encoded, new TransformOptions
        {
            Encoder = new PngEncoderOptions(),
            Rotation = ImageRotation.Clockwise90,
        }));
        Equal(2, clockwise.Width, "clockwise width");
        Equal(3, clockwise.Height, "clockwise height");
        AssertPixel(source, 0, 1, clockwise, 0, 0);
        AssertPixel(source, 0, 0, clockwise, 1, 0);
        AssertPixel(source, 2, 1, clockwise, 0, 2);

        RawImage counterclockwise = PngFixture.Decode(ImageTransformer.Transform(encoded, new TransformOptions
        {
            Encoder = new PngEncoderOptions(),
            Rotation = ImageRotation.CounterClockwise90,
        }));
        AssertPixel(source, 2, 0, counterclockwise, 0, 0);
        AssertPixel(source, 2, 1, counterclockwise, 1, 0);
        AssertPixel(source, 0, 0, counterclockwise, 0, 2);
    }

    private static void ResizeModes()
    {
        RawImage source = CreatePattern(8, 4, alpha: false);
        byte[] encoded = PngFixture.Encode(source);
        RawImage contain = TransformResize(encoded, 3, 3, ResizeMode.Contain);
        Equal(3, contain.Width, "contain width");
        Equal(2, contain.Height, "contain height");
        RawImage cover = TransformResize(encoded, 3, 3, ResizeMode.Cover);
        Equal(3, cover.Width, "cover width");
        Equal(3, cover.Height, "cover height");
        RawImage stretch = TransformResize(encoded, 3, 3, ResizeMode.Stretch);
        Equal(3, stretch.Width, "stretch width");
        Equal(3, stretch.Height, "stretch height");

        RawImage noUpscale = TransformResize(encoded, 80, 40, ResizeMode.Contain);
        Equal(source.Width, noUpscale.Width, "no-upscale width");
        Equal(source.Height, noUpscale.Height, "no-upscale height");
    }

    private static void ResizeFilters()
    {
        byte[] input = PngFixture.Encode(CreatePattern(31, 23, alpha: true));
        foreach (ResizeFilter filter in Enum.GetValues<ResizeFilter>())
        {
            TransformOptions options = new()
            {
                Encoder = new PngEncoderOptions(),
                Resize = new ResizeOptions { Width = 13, Height = 11, Mode = ResizeMode.Stretch, Filter = filter },
            };
            byte[] first = ImageTransformer.Transform(input, options);
            byte[] second = ImageTransformer.Transform(input, options);
            SequenceEqual(first, second, $"{filter} deterministic encoding");
            RawImage decoded = PngFixture.Decode(first);
            Equal(13, decoded.Width, $"{filter} width");
            Equal(11, decoded.Height, $"{filter} height");
        }
    }

    private static void Bmp24()
    {
        RawImage source = CreatePattern(5, 3, alpha: false);
        RawImage actual = PngFixture.Decode(ImageTransformer.Transform(BmpFixture.Encode24(source), new TransformOptions { Encoder = new PngEncoderOptions() }));
        SequenceEqual(source.Pixels, actual.Pixels, "24-bit BMP pixels");
    }

    private static void BmpIndexed()
    {
        byte[] bmp = BmpFixture.EncodeIndexed();
        RawImage actual = PngFixture.Decode(ImageTransformer.Transform(bmp, new TransformOptions { Encoder = new PngEncoderOptions() }));
        Equal(4, actual.Width, "indexed width");
        Equal(2, actual.Height, "indexed height");
        byte[] expected =
        [
            255,0,0,255, 0,255,0,255, 0,0,255,255, 255,255,255,255,
            255,255,255,255, 0,0,255,255, 0,255,0,255, 255,0,0,255,
        ];
        SequenceEqual(expected, actual.Pixels, "indexed BMP pixels");
    }

    private static void JpegRoundTrip444() => JpegRoundTrip(JpegChromaSubsampling.Yuv444, 10);

    private static void JpegRoundTrip420() => JpegRoundTrip(JpegChromaSubsampling.Yuv420, 20);

    private static void JpegRoundTrip(JpegChromaSubsampling subsampling, double maximumMeanError)
    {
        RawImage source = CreateSmoothPattern(32, 24);
        byte[] png = PngFixture.Encode(source);
        byte[] jpeg = ImageTransformer.Transform(png, new TransformOptions
        {
            Encoder = new JpegEncoderOptions { Quality = 95, ChromaSubsampling = subsampling },
        });
        True(jpeg.Length > 100 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "JPEG signature");
        RawImage actual = PngFixture.Decode(ImageTransformer.Transform(jpeg, new TransformOptions { Encoder = new PngEncoderOptions() }));
        Equal(source.Width, actual.Width, "JPEG width");
        Equal(source.Height, actual.Height, "JPEG height");
        double total = 0;
        for (int i = 0; i < source.Pixels.Length; i += 4)
        {
            total += Math.Abs(source.Pixels[i] - actual.Pixels[i]);
            total += Math.Abs(source.Pixels[i + 1] - actual.Pixels[i + 1]);
            total += Math.Abs(source.Pixels[i + 2] - actual.Pixels[i + 2]);
        }

        double mean = total / (source.Width * source.Height * 3);
        True(mean <= maximumMeanError, $"JPEG mean error {mean:F2} > {maximumMeanError:F2}");
    }

    private static void JpegReferenceCorpus()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "jpeg");
        foreach (string path in Directory.GetFiles(directory, "*.jpg"))
        {
            try
            {
                RawImage decoded = PngFixture.Decode(ImageTransformer.Transform(File.ReadAllBytes(path), new TransformOptions { Encoder = new PngEncoderOptions() }));
                byte[] expected = File.ReadAllBytes(Path.ChangeExtension(path, ".rgba"));
                Equal(BinaryPrimitives.ReadInt32LittleEndian(expected), decoded.Width, $"{Path.GetFileName(path)} width");
                Equal(BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(4)), decoded.Height, $"{Path.GetFileName(path)} height");
                AssertPerceptual(expected.AsSpan(8), decoded.Pixels, 8, 90, Path.GetFileName(path));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"JPEG fixture {Path.GetFileName(path)} failed.", exception);
            }
        }
    }

    private static void JpegNativeThumbnailReduction()
    {
        RawImage source = CreateSmoothPattern(641, 479);
        byte[] jpeg = ImageTransformer.Transform(PngFixture.Encode(source), new TransformOptions
        {
            Encoder = new JpegEncoderOptions { Quality = 95, ChromaSubsampling = JpegChromaSubsampling.Yuv420 },
        });
        byte[] fullyDecodedPng = ImageTransformer.Transform(jpeg, new TransformOptions { Encoder = new PngEncoderOptions() });

        foreach (ImageRotation rotation in new[] { ImageRotation.None, ImageRotation.Clockwise90 })
        {
            TransformOptions options = new()
            {
                Encoder = new PngEncoderOptions(),
                Rotation = rotation,
                Resize = new ResizeOptions { Width = 100, Height = 100, Mode = ResizeMode.Contain, Filter = ResizeFilter.Lanczos3 },
            };
            RawImage reduced = PngFixture.Decode(ImageTransformer.Transform(jpeg, options));
            RawImage reference = PngFixture.Decode(ImageTransformer.Transform(fullyDecodedPng, options));
            Equal(reference.Width, reduced.Width, $"native reduction {rotation} width");
            Equal(reference.Height, reduced.Height, $"native reduction {rotation} height");
            AssertPerceptual(reference.Pixels, reduced.Pixels, 13, 125, $"native reduction {rotation}");
        }
    }

    private static void ExifOrientations()
    {
        RawImage source = CreateSmoothPattern(24, 16);
        byte[] jpeg = ImageTransformer.Transform(PngFixture.Encode(source), new TransformOptions
        {
            Encoder = new JpegEncoderOptions { Quality = 96, ChromaSubsampling = JpegChromaSubsampling.Yuv444 },
        });
        RawImage baseline = PngFixture.Decode(ImageTransformer.Transform(jpeg, new TransformOptions { Encoder = new PngEncoderOptions() }));
        for (ushort orientation = 1; orientation <= 8; orientation++)
        {
            RawImage actual = PngFixture.Decode(ImageTransformer.Transform(InjectExifOrientation(jpeg, orientation), new TransformOptions { Encoder = new PngEncoderOptions() }));
            bool swapsAxes = orientation >= 5;
            Equal(swapsAxes ? baseline.Height : baseline.Width, actual.Width, $"orientation {orientation} width");
            Equal(swapsAxes ? baseline.Width : baseline.Height, actual.Height, $"orientation {orientation} height");
            for (int y = 0; y < actual.Height; y++)
            for (int x = 0; x < actual.Width; x++)
            {
                (int rawX, int rawY) = orientation switch
                {
                    2 => (baseline.Width - 1 - x, y),
                    3 => (baseline.Width - 1 - x, baseline.Height - 1 - y),
                    4 => (x, baseline.Height - 1 - y),
                    5 => (y, x),
                    6 => (y, baseline.Height - 1 - x),
                    7 => (baseline.Width - 1 - y, baseline.Height - 1 - x),
                    8 => (baseline.Width - 1 - y, x),
                    _ => (x, y),
                };
                AssertPixel(baseline, rawX, rawY, actual, x, y);
            }
        }

        RawImage composed = PngFixture.Decode(ImageTransformer.Transform(InjectExifOrientation(jpeg, 6), new TransformOptions
        {
            Encoder = new PngEncoderOptions(), Rotation = ImageRotation.Clockwise90,
        }));
        Equal(baseline.Width, composed.Width, "composed orientation width");
        Equal(baseline.Height, composed.Height, "composed orientation height");
        for (int y = 0; y < composed.Height; y++)
        for (int x = 0; x < composed.Width; x++)
            AssertPixel(baseline, baseline.Width - 1 - x, baseline.Height - 1 - y, composed, x, y);
    }

    private static byte[] InjectExifOrientation(ReadOnlySpan<byte> jpeg, ushort orientation)
    {
        byte[] app1 = new byte[36];
        app1[0] = 0xFF; app1[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(app1.AsSpan(2), 34);
        "Exif\0\0II"u8.CopyTo(app1.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(app1.AsSpan(12), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(app1.AsSpan(14), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(app1.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(app1.AsSpan(20), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(app1.AsSpan(22), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(app1.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(app1.AsSpan(28), orientation);
        byte[] result = new byte[jpeg.Length + app1.Length];
        jpeg[..2].CopyTo(result);
        app1.CopyTo(result, 2);
        jpeg[2..].CopyTo(result.AsSpan(2 + app1.Length));
        return result;
    }

    private static void JpegAlphaFlattening()
    {
        RawImage source = new(8, 8, Enumerable.Repeat(new byte[] { 255, 0, 0, 0 }, 64).SelectMany(x => x).ToArray());
        byte[] jpeg = ImageTransformer.Transform(PngFixture.Encode(source), new TransformOptions
        {
            Encoder = new JpegEncoderOptions { Quality = 100, ChromaSubsampling = JpegChromaSubsampling.Yuv444, BackgroundRed = 12, BackgroundGreen = 80, BackgroundBlue = 190 },
        });
        RawImage actual = PngFixture.Decode(ImageTransformer.Transform(jpeg, new TransformOptions { Encoder = new PngEncoderOptions() }));
        True(Math.Abs(actual.Pixels[0] - 12) <= 3 && Math.Abs(actual.Pixels[1] - 80) <= 3 && Math.Abs(actual.Pixels[2] - 190) <= 3, "JPEG alpha background was not applied");
        True(actual.Pixels[3] == 255, "JPEG output is not opaque");
    }

    private static void StreamsRemainOpen()
    {
        byte[] encoded = PngFixture.Encode(CreatePattern(4, 3, alpha: true));
        using TrackingStream input = new(encoded);
        using TrackingStream output = new();
        ImageTransformer.Transform(input, output, new TransformOptions { Encoder = new PngEncoderOptions() });
        True(!input.WasDisposed && !output.WasDisposed, "transform disposed a caller-owned stream");
        True(output.Length > 0, "stream output is empty");

        using NonSeekableReadStream sequential = new(encoded);
        using MemoryStream sequentialOutput = new();
        ImageTransformer.Transform(sequential, sequentialOutput, new TransformOptions { Encoder = new PngEncoderOptions() });
        True(sequentialOutput.Length > 0, "non-seekable stream output is empty");
    }

    private static void OrderedPipelineContracts()
    {
        byte[] encoded = PngFixture.Encode(CreatePattern(11, 7, alpha: true));
        IImageTransformation[] transformations =
        [
            new RotateTransformation(ImageRotation.Clockwise90),
            new ResizeTransformation(new ResizeOptions
            {
                Width = 5,
                Height = 4,
                Mode = ResizeMode.Stretch,
                Filter = ResizeFilter.Bicubic,
                AllowUpscale = true,
            }),
        ];
        PngEncoderOptions encoder = new() { CompressionLevel = 6 };
        byte[] expected = ImageTransformer.Transform(encoded, new TransformOptions
        {
            Encoder = encoder,
            Rotation = ImageRotation.Clockwise90,
            Resize = new ResizeOptions
            {
                Width = 5,
                Height = 4,
                Mode = ResizeMode.Stretch,
                Filter = ResizeFilter.Bicubic,
                AllowUpscale = true,
            },
        });

        SequenceEqual(expected, ImageTransformer.Transform(encoded, transformations, encoder), "byte[] to byte[]");
        SequenceEqual(expected, ImageTransformer.Transform(encoded.AsSpan(), transformations, encoder), "span to byte[]");
        using (TrackingStream input = new(encoded))
        {
            SequenceEqual(expected, ImageTransformer.Transform(input, transformations, encoder), "stream to byte[]");
            True(!input.WasDisposed, "stream-to-array disposed input");
        }

        using (MemoryStream output = ImageTransformer.TransformToStream(encoded, transformations, encoder))
        {
            Equal(0L, output.Position, "byte[] returned stream position");
            SequenceEqual(expected, output.ToArray(), "byte[] returned stream bytes");
        }

        using (MemoryStream output = ImageTransformer.TransformToStream(encoded.AsSpan(), transformations, encoder))
        {
            Equal(0L, output.Position, "span returned stream position");
            SequenceEqual(expected, output.ToArray(), "span returned stream bytes");
        }

        using (TrackingStream input = new(encoded))
        using (MemoryStream output = ImageTransformer.TransformToStream(input, transformations, encoder))
        {
            Equal(0L, output.Position, "stream returned stream position");
            SequenceEqual(expected, output.ToArray(), "stream returned stream bytes");
            True(!input.WasDisposed, "returned-stream overload disposed input");
        }

        using (TrackingStream output = new())
        {
            output.Write([1, 2, 3]);
            ImageTransformer.Transform(encoded, output, transformations, encoder);
            SequenceEqual(expected, output.ToArray().AsSpan(3), "byte[] to destination stream");
            True(!output.WasDisposed, "byte[] transform disposed output");
        }

        using (TrackingStream output = new())
        {
            ImageTransformer.Transform(encoded.AsSpan(), output, transformations, encoder);
            SequenceEqual(expected, output.ToArray(), "span to destination stream");
        }

        using (TrackingStream input = new(encoded))
        using (TrackingStream output = new())
        {
            ImageTransformer.Transform(input, output, transformations, encoder);
            SequenceEqual(expected, output.ToArray(), "stream to destination stream");
            True(!input.WasDisposed && !output.WasDisposed, "stream transform changed caller ownership");
        }

        ArrayBufferWriter<byte> arrayWriter = new();
        ImageTransformer.Transform(encoded, arrayWriter, transformations, encoder);
        SequenceEqual(expected, arrayWriter.WrittenSpan, "byte[] to buffer writer");

        ArrayBufferWriter<byte> spanWriter = new();
        ImageTransformer.Transform(encoded.AsSpan(), spanWriter, transformations, encoder);
        SequenceEqual(expected, spanWriter.WrittenSpan, "span to buffer writer");

        using (TrackingStream input = new(encoded))
        {
            ArrayBufferWriter<byte> streamWriter = new();
            ImageTransformer.Transform(input, streamWriter, transformations, encoder);
            SequenceEqual(expected, streamWriter.WrittenSpan, "stream to buffer writer");
            True(!input.WasDisposed, "buffer-writer overload disposed input");
        }
    }

    private static void OrderedPipelineSequencing()
    {
        byte[] encoded = PngFixture.Encode(CreatePattern(9, 6, alpha: false));
        PngEncoderOptions encoder = new();
        byte[] empty = ImageTransformer.Transform(encoded, Array.Empty<IImageTransformation>(), encoder);
        SequenceEqual(ImageTransformer.Transform(encoded, new TransformOptions { Encoder = new PngEncoderOptions() }), empty, "empty pipeline");

        IImageTransformation[] repeatedRotations =
        [
            new RotateTransformation(ImageRotation.Clockwise90),
            new RotateTransformation(ImageRotation.Clockwise90),
        ];
        SequenceEqual(
            ImageTransformer.Transform(encoded, new TransformOptions { Encoder = new PngEncoderOptions(), Rotation = ImageRotation.Clockwise180 }),
            ImageTransformer.Transform(encoded, repeatedRotations, encoder),
            "adjacent rotation composition");

        RawImage resizeThenRotate = PngFixture.Decode(ImageTransformer.Transform(encoded,
        [
            new ResizeTransformation(new ResizeOptions { Width = 5, Height = 3, Mode = ResizeMode.Stretch, AllowUpscale = true }),
            new RotateTransformation(ImageRotation.Clockwise90),
        ], encoder));
        Equal(3, resizeThenRotate.Width, "resize-then-rotate width");
        Equal(5, resizeThenRotate.Height, "resize-then-rotate height");

        RawImage rotateThenResize = PngFixture.Decode(ImageTransformer.Transform(encoded,
        [
            new RotateTransformation(ImageRotation.Clockwise90),
            new ResizeTransformation(new ResizeOptions { Width = 5, Height = 3, Mode = ResizeMode.Stretch, AllowUpscale = true }),
        ], encoder));
        Equal(5, rotateThenResize.Width, "rotate-then-resize width");
        Equal(3, rotateThenResize.Height, "rotate-then-resize height");

        RawImage repeatedResize = PngFixture.Decode(ImageTransformer.Transform(encoded,
        [
            new ResizeTransformation(new ResizeOptions { Width = 7, Height = 5, Mode = ResizeMode.Stretch, AllowUpscale = true }),
            new ResizeTransformation(new ResizeOptions { Width = 2, Height = 4, Mode = ResizeMode.Stretch, AllowUpscale = true }),
        ], encoder));
        Equal(2, repeatedResize.Width, "repeated resize width");
        Equal(4, repeatedResize.Height, "repeated resize height");

        Throws(ImageErrorCode.InvalidOptions, () => ImageTransformer.Transform(encoded, [new UnknownTransformation()], encoder));
        Throws(ImageErrorCode.InvalidOptions, () => ImageTransformer.Transform(encoded, [null!], encoder));
        Throws(ImageErrorCode.InvalidOptions, () => ImageTransformer.Transform(encoded, [new RotateTransformation((ImageRotation)99)], encoder));
        Throws(ImageErrorCode.InvalidOptions, () => ImageTransformer.Transform(encoded,
            [new ResizeTransformation(new ResizeOptions { Width = 0, Height = 3 })], encoder));
        Throws(ImageErrorCode.PixelLimitExceeded, () => ImageTransformer.Transform(encoded, [], encoder, new ImageReadOptions { MaxInputPixels = 53 }));
    }

    private static void ImageInformation()
    {
        RawImage source = CreatePattern(13, 7, alpha: false);
        byte[] png = PngFixture.Encode(source);
        AssertInfo(ImageInspector.Identify(png), ImageFormat.Png, "image/png", 13, 7, 13, 7, ExifOrientation.Normal, false, "PNG");
        AssertInfo(ImageInspector.Identify(png.AsSpan()), ImageFormat.Png, "image/png", 13, 7, 13, 7, ExifOrientation.Normal, false, "PNG span");

        byte[] bmp = BmpFixture.Encode24(source);
        AssertInfo(ImageInspector.Identify(bmp), ImageFormat.Bmp, "image/bmp", 13, 7, 13, 7, ExifOrientation.Normal, false, "BMP");

        byte[] jpeg = ImageTransformer.Transform(png, new TransformOptions
        {
            Encoder = new JpegEncoderOptions { Quality = 90, ChromaSubsampling = JpegChromaSubsampling.Yuv444 },
        });
        for (ushort value = 1; value <= 8; value++)
        {
            ImageInfo jpegInfo = ImageInspector.Identify(InjectExifOrientation(jpeg, value));
            bool swaps = value >= 5;
            AssertInfo(jpegInfo, ImageFormat.Jpeg, "image/jpeg", swaps ? 7 : 13, swaps ? 13 : 7, 13, 7, (ExifOrientation)value, false, $"JPEG EXIF {value}");
        }

        string webpDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "webp");
        foreach (string file in new[] { "lossless_vec_2_6.webp", "bike_lossy_small.webp" })
        {
            string path = Path.Combine(webpDirectory, file);
            ImageInfo webpInfo = ImageInspector.Identify(File.ReadAllBytes(path));
            RawImage decoded = DecodeWebpRgba(path);
            Equal((int)ImageFormat.WebP, (int)webpInfo.Format, $"{file} format");
            Equal("image/webp", webpInfo.MimeType, $"{file} MIME");
            Equal(decoded.Width, webpInfo.Width, $"{file} width");
            Equal(decoded.Height, webpInfo.Height, $"{file} height");
            True(!webpInfo.IsAnimated, $"{file} animation");
        }

        byte[] animatedWebp = CreateAnimatedWebP();
        AssertInfo(ImageInspector.Identify(animatedWebp), ImageFormat.WebP, "image/webp", 1, 1, 1, 1, ExifOrientation.Normal, true, "animated WebP");
        byte[] animatedPng = InjectAnimationControl(png);
        AssertInfo(ImageInspector.Identify(animatedPng), ImageFormat.Png, "image/png", 13, 7, 13, 7, ExifOrientation.Normal, true, "animated PNG");

        byte[] prefixed = new byte[png.Length + 5];
        png.CopyTo(prefixed, 5);
        using (TrackingStream seekable = new(prefixed))
        {
            seekable.Position = 5;
            ImageInfo streamInfo = ImageInspector.Identify(seekable);
            Equal(5L, seekable.Position, "seekable identify position");
            True(!seekable.WasDisposed, "identify disposed seekable stream");
            Equal(13, streamInfo.Width, "seekable identify width");
        }

        using (NonSeekableReadStream nonSeekable = new(png))
        {
            Equal(13, ImageInspector.Identify(nonSeekable).Width, "non-seekable identify width");
            _ = nonSeekable.ReadByte();
        }

        using (TrackingStream invalid = new("not an image"u8.ToArray()))
        {
            Throws(ImageErrorCode.UnknownFormat, () => ImageInspector.Identify(invalid));
            Equal(0L, invalid.Position, "failed seekable identify position");
            True(!invalid.WasDisposed, "failed identify disposed seekable stream");
        }

        Throws(ImageErrorCode.UnknownFormat, () => ImageInspector.Identify("not an image"u8));
        Throws(ImageErrorCode.UnexpectedEndOfData, () => ImageInspector.Identify(png.AsSpan(0, 20)));
        Throws(ImageErrorCode.PixelLimitExceeded, () => ImageInspector.Identify(png, new ImageReadOptions { MaxInputPixels = 90 }));
        Throws(ImageErrorCode.InputTooLarge, () => ImageInspector.Identify(png, new ImageReadOptions { MaxInputBytes = png.Length - 1 }));
    }

    private static void AssertInfo(
        ImageInfo info,
        ImageFormat format,
        string mime,
        int width,
        int height,
        int encodedWidth,
        int encodedHeight,
        ExifOrientation orientation,
        bool animated,
        string name)
    {
        Equal((int)format, (int)info.Format, $"{name} format");
        Equal(mime, info.MimeType, $"{name} MIME");
        Equal(width, info.Width, $"{name} width");
        Equal(height, info.Height, $"{name} height");
        Equal(encodedWidth, info.EncodedWidth, $"{name} encoded width");
        Equal(encodedHeight, info.EncodedHeight, $"{name} encoded height");
        Equal((int)orientation, (int)info.ExifOrientation, $"{name} orientation");
        Equal(animated, info.IsAnimated, $"{name} animation");
    }

    private static byte[] InjectAnimationControl(ReadOnlySpan<byte> png)
    {
        const int InsertOffset = 33;
        byte[] chunk = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, 8);
        "acTL"u8.CopyTo(chunk.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16), Crc32.Compute(chunk.AsSpan(4, 4), chunk.AsSpan(8, 8)));
        byte[] result = new byte[png.Length + chunk.Length];
        png[..InsertOffset].CopyTo(result);
        chunk.CopyTo(result, InsertOffset);
        png[InsertOffset..].CopyTo(result.AsSpan(InsertOffset + chunk.Length));
        return result;
    }

    private static byte[] CreateAnimatedWebP()
    {
        byte[] webp = new byte[30];
        "RIFF"u8.CopyTo(webp);
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4), 22);
        "WEBPVP8X"u8.CopyTo(webp.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16), 10);
        webp[20] = 2;
        return webp;
    }

    private static void ErrorsAndLimits()
    {
        Throws(ImageErrorCode.UnknownFormat, () => ImageTransformer.Transform("not an image"u8, new TransformOptions()));
        byte[] png = PngFixture.Encode(CreatePattern(4, 3, alpha: false));
        Throws(ImageErrorCode.PixelLimitExceeded, () => ImageTransformer.Transform(png, new TransformOptions { MaxInputPixels = 11 }));
        Throws(ImageErrorCode.InputTooLarge, () => ImageTransformer.Transform(png, new TransformOptions { MaxInputBytes = png.Length - 1 }));
        Throws(ImageErrorCode.InvalidOptions, () => ImageTransformer.Transform(png, new TransformOptions { Resize = new ResizeOptions { Width = 0, Height = 2 } }));
        byte[] corruptPng = png.ToArray();
        corruptPng[^5] ^= 0x40;
        Throws(ImageErrorCode.InvalidData, () => ImageTransformer.Transform(corruptPng, new TransformOptions()));

        byte[] jpeg = ImageTransformer.Transform(png, new TransformOptions { Encoder = new JpegEncoderOptions() });
        Throws(ImageErrorCode.UnexpectedEndOfData, () => ImageTransformer.Transform(jpeg.AsSpan(0, jpeg.Length / 2), new TransformOptions()));
    }

    private static void ConcurrentTransforms()
    {
        byte[] png = PngFixture.Encode(CreatePattern(96, 64, alpha: true));
        string webp = Path.Combine(AppContext.BaseDirectory, "fixtures", "webp");
        byte[][] inputs =
        [
            png,
            File.ReadAllBytes(Path.Combine(webp, "lossless_vec_2_6.webp")),
            File.ReadAllBytes(Path.Combine(webp, "bike_lossy_small.webp")),
        ];
        ConcurrentQueue<Exception> failures = new();
        Parallel.For(0, 32, index =>
        {
            try
            {
                byte[] result = ImageTransformer.Transform(inputs[index % inputs.Length], new TransformOptions
                {
                    Encoder = index % 2 == 0 ? new PngEncoderOptions() : new JpegEncoderOptions { Quality = 80 },
                    Rotation = (ImageRotation)(index % 4),
                    Resize = new ResizeOptions { Width = 48, Height = 48, Mode = ResizeMode.Contain, Filter = (ResizeFilter)(index % 3) },
                });
                True(result.Length > 32, "concurrent result is empty");
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        });
        if (failures.TryDequeue(out Exception? failure))
        {
            throw failure;
        }
    }

    private static void NativeMemoryStress()
    {
        byte[] png = PngFixture.Encode(CreatePattern(256, 192, alpha: true));
        TransformOptions options = new()
        {
            Encoder = new PngEncoderOptions { CompressionLevel = 1 },
            Resize = new ResizeOptions { Width = 96, Height = 96, Mode = ResizeMode.Contain, Filter = ResizeFilter.Lanczos3 },
        };
        for (int i = 0; i < 8; i++) _ = ImageTransformer.Transform(png, options);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = Process.GetCurrentProcess().PrivateMemorySize64;
        for (int i = 0; i < 300; i++) _ = ImageTransformer.Transform(png, options);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long growth = Process.GetCurrentProcess().PrivateMemorySize64 - before;
        True(growth < 64L * 1024 * 1024, $"private bytes grew by {growth / 1024 / 1024} MiB");
    }

    private static void AnimatedWebPRejected()
    {
        byte[] webp = CreateAnimatedWebP();
        Throws(ImageErrorCode.UnsupportedFeature, () => ImageTransformer.Transform(webp, new TransformOptions()));
    }

    private static void Vp8lReferenceVectors()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "webp");
        string[] paths = Directory.GetFiles(directory, "lossless_vec_*.webp").Append(Path.Combine(directory, "lossless_alpha_small.webp")).ToArray();
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            try
            {
                byte[] input = File.ReadAllBytes(path);
                if (BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4)) + 8u > input.Length)
                {
                    continue; // The upstream corpus also contains deliberate truncation vectors.
                }

                RawImage decoded = PngFixture.Decode(ImageTransformer.Transform(input, new TransformOptions { Encoder = new PngEncoderOptions() }));
                True(decoded.Width > 0 && decoded.Height > 0 && decoded.Pixels.Length == decoded.Width * decoded.Height * 4, $"VP8L fixture {name} did not decode");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"VP8L fixture {name} failed.", exception);
            }
        }
    }

    private static void Vp8LossyReferenceVector()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "webp");
        foreach (string name in new[]
        {
            "small_1x1", "alpha_no_compression", "bike_lossy_small", "bike_lossy_complex_filter",
            "alpha_filter_0_method_0", "alpha_filter_0_method_1",
            "alpha_filter_1_method_0", "alpha_filter_1_method_1",
            "alpha_filter_2_method_0", "alpha_filter_2_method_1",
            "alpha_filter_3_method_0", "alpha_filter_3_method_1",
        })
        {
            string path = Path.Combine(directory, name + ".webp");
            byte[] transformed = ImageTransformer.Transform(File.ReadAllBytes(path), new TransformOptions { Encoder = new PngEncoderOptions() });
            RawImage decoded = PngFixture.Decode(transformed);
            byte[] expected = File.ReadAllBytes(Path.ChangeExtension(path, ".rgba"));
            Equal(BinaryPrimitives.ReadInt32LittleEndian(expected), decoded.Width, $"{name} width");
            Equal(BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(4)), decoded.Height, $"{name} height");
            AssertPerceptual(expected.AsSpan(8), decoded.Pixels, 8, 80, name);
        }
    }

    private static void WebpOracleManifest()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "webp");
        string manifestPath = Path.Combine(directory, "oracle-manifest.json");
        List<WebpOracleEntry> entries = JsonSerializer.Deserialize<List<WebpOracleEntry>>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("The WebP oracle manifest is empty.");
        True(entries.Count == 43, $"expected 43 valid WebP oracle entries, found {entries.Count}");
        foreach (WebpOracleEntry entry in entries)
        {
            RawImage decoded = DecodeWebpRgba(Path.Combine(directory, entry.File));
            Equal(entry.Width, decoded.Width, $"{entry.File} manifest width");
            Equal(entry.Height, decoded.Height, $"{entry.File} manifest height");
            bool opaque = true;
            for (int offset = 3; offset < decoded.Pixels.Length; offset += 4)
            {
                if (decoded.Pixels[offset] != 255) { opaque = false; break; }
            }

            Equal(entry.Opaque, opaque, $"{entry.File} manifest opacity");
            string decoderHash = HashPixels(decoded.Pixels);
            Equal(entry.DecoderRgbaSha256, decoderHash, $"{entry.File} decoder hash");
            if (entry.Codec == "VP8L")
            {
                Equal(entry.ReferenceRgbaSha256, decoderHash, $"{entry.File} ImageSharp lossless hash");
            }
        }
    }

    private static RawImage DecodeWebpRgba(string path) => PngFixture.Decode(ImageTransformer.Transform(
        File.ReadAllBytes(path),
        new TransformOptions { Encoder = new PngEncoderOptions { CompressionLevel = 6 } }));

    private static string HashPixels(ReadOnlySpan<byte> pixels) => Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();

    private static void AssertPerceptual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, double maximumMean, int maximumPeak, string name)
    {
        Equal(expected.Length, actual.Length, $"{name} sample count");
        long totalError = 0;
        int peakError = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            int error = Math.Abs(expected[i] - actual[i]);
            totalError += error;
            peakError = Math.Max(peakError, error);
        }

        double meanError = totalError / (double)actual.Length;
        True(meanError <= maximumMean && peakError <= maximumPeak, $"{name} perceptual error is mean={meanError:F2}, max={peakError}");
    }

    private static RawImage TransformResize(byte[] input, int width, int height, ResizeMode mode) => PngFixture.Decode(ImageTransformer.Transform(input, new TransformOptions
    {
        Encoder = new PngEncoderOptions(),
        Resize = new ResizeOptions { Width = width, Height = height, Mode = mode, Filter = ResizeFilter.Bilinear },
    }));

    private static RawImage CreatePattern(int width, int height, bool alpha)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x * 31 + y * 7);
                pixels[offset + 1] = (byte)(x * 11 + y * 43);
                pixels[offset + 2] = (byte)(x * 19 + y * 17);
                pixels[offset + 3] = alpha ? (byte)(32 + ((x * 23 + y * 29) % 224)) : (byte)255;
            }
        }

        return new RawImage(width, height, pixels);
    }

    private static RawImage CreateSmoothPattern(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(20 + x * 5);
                pixels[offset + 1] = (byte)(30 + y * 7);
                pixels[offset + 2] = (byte)(40 + (x + y) * 3);
                pixels[offset + 3] = 255;
            }
        }

        return new RawImage(width, height, pixels);
    }

    private static void AssertPixel(RawImage expected, int expectedX, int expectedY, RawImage actual, int actualX, int actualY)
    {
        int expectedOffset = (expectedY * expected.Width + expectedX) * 4;
        int actualOffset = (actualY * actual.Width + actualX) * 4;
        SequenceEqual(expected.Pixels.AsSpan(expectedOffset, 4), actual.Pixels.AsSpan(actualOffset, 4), "pixel");
    }

    private static void Throws(ImageErrorCode code, Action action)
    {
        try
        {
            action();
        }
        catch (Image2DException exception) when (exception.ErrorCode == code)
        {
            return;
        }

        throw new InvalidOperationException($"Expected Image2DException({code}).");
    }

    private static void Equal<T>(T expected, T actual, string name) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
        }
    }

    private static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string name)
    {
        if (!expected.SequenceEqual(actual))
        {
            int mismatch = -1;
            if (expected.Length == actual.Length)
            {
                for (int i = 0; i < expected.Length; i++)
                {
                    if (expected[i] != actual[i])
                    {
                        mismatch = i;
                        break;
                    }
                }
            }
            string values = mismatch >= 0 ? $", values {expected[mismatch]}/{actual[mismatch]}" : string.Empty;
            throw new InvalidOperationException($"{name}: byte sequences differ (lengths {expected.Length}/{actual.Length}, mismatch {mismatch}{values}).");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class WebpOracleEntry
{
    public string File { get; set; } = string.Empty;
    public string Codec { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Opaque { get; set; }
    public string ReferenceRgbaSha256 { get; set; } = string.Empty;
    public string DecoderRgbaSha256 { get; set; } = string.Empty;
}

internal sealed class UnknownTransformation : IImageTransformation
{
}

internal sealed class TrackingStream : MemoryStream
{
    public TrackingStream()
    {
    }

    public TrackingStream(byte[] bytes) : base(bytes)
    {
    }

    public bool WasDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class NonSeekableReadStream : Stream
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

internal sealed class RawImage
{
    public RawImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}
