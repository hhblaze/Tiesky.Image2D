using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tiesky.Image2D;
using Tiesky.Image2D.Codecs;
using Tiesky.Image2D.Codecs.Jpeg;
using Tiesky.Image2D.Codecs.Png;
using Tiesky.Image2D.Internal;
using Tiesky.Image2D.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SlImage = SixLabors.ImageSharp.Image;
using SlJpegEncoder = SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder;
using SlPngEncoder = SixLabors.ImageSharp.Formats.Png.PngEncoder;
using SlPngCompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel;
using SlResizeMode = SixLabors.ImageSharp.Processing.ResizeMode;

if (args.Length > 0 && args[0] == "--worker")
{
    return RunWorker(args);
}

if (args.Length > 0 && args[0] == "--render-json")
{
    return RenderExistingReport(args);
}

return RunCoordinator(args);

static int RenderExistingReport(string[] args)
{
    if (args.Length < 2) throw new ArgumentException("--render-json requires the final JSON report path.");
    string? baselinePath = ReadOptionalString(args, "--baseline");
    string? htmlPath = ReadOptionalString(args, "--html");
    if (htmlPath is null) throw new ArgumentException("--render-json requires --html <path>.");
    BenchmarkReport report = ReadFinalReport(args[1]);
    report = report with
    {
        Environment = report.Environment with
        {
            ImageSharpAssembly = $"SixLabors.ImageSharp 3.1.12 (assembly {typeof(SlImage).Assembly.GetName().Version})",
        },
    };
    WriteHtml(htmlPath, report, baselinePath is null ? null : ReadBaselineSet(baselinePath));
    return 0;
}

static int RunCoordinator(string[] args)
{
    GateMode gate = ReadGate(args);
    int iterations = ReadInt(args, "--iterations", 15);
    int warmups = ReadInt(args, "--warmups", 5);
    int[] widths = ReadString(args, "--widths", "200,800,1600").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ParsePositiveInt).ToArray();
    string fixtureDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures"));
    string suites = ReadString(args, "--suite", "all");
    List<Scenario> scenarios = CreateScenarios(fixtureDirectory, suites, widths);
    string? jsonPath = ReadOptionalString(args, "--json");
    string? htmlPath = ReadOptionalString(args, "--html");
    string? baselinePath = ReadOptionalString(args, "--baseline");
    BenchmarkReport? baseline = baselinePath is null ? null : ReadBaselineSet(baselinePath);

    foreach (Scenario scenario in scenarios)
    {
        if (!File.Exists(scenario.InputPath))
        {
            throw new FileNotFoundException($"Tracked fixture for scenario '{scenario.Name}' is missing.", scenario.InputPath);
        }
    }

    string temporary = Path.Combine(Path.GetTempPath(), "Tiesky.Image2D.Benchmarks", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    List<ScenarioResult> rows = new(scenarios.Count);
    bool gatePassed = true;
    try
    {
        Console.WriteLine($"Runtime: {Environment.Version}; suites: {suites}; scenarios: {scenarios.Count}; iterations: {iterations}; warmups: {warmups}; gate: {gate}");
        Console.WriteLine("scenario                     engine       total-ms decode-ms resize-ms encode-ms private-MiB output-bytes");
        foreach (Scenario scenario in scenarios)
        {
            string extension = scenario.Output == OutputCodec.Png ? ".png" : ".jpg";
            string openOutput = Path.Combine(temporary, $"tiesky-{scenario.Key}{extension}");
            string sharpOutput = Path.Combine(temporary, $"imagesharp-{scenario.Key}{extension}");
            Result tiesky = RunChild("tiesky", scenario, iterations, warmups, openOutput);
            Result imageSharp = RunChild("imagesharp", scenario, iterations, warmups, sharpOutput);
            OutputInspection openInspection = InspectOutput(openOutput);
            OutputInspection sharpInspection = InspectOutput(sharpOutput);
            double psnr = CalculatePsnr(openOutput, sharpOutput);
            bool exactPixels = openInspection.PixelHash == sharpInspection.PixelHash;
            bool dimensionsMatch = openInspection.Width == sharpInspection.Width && openInspection.Height == sharpInspection.Height;
            ScenarioResult row = new(
                scenario.Key, scenario.Suite, scenario.Name, scenario.Width, scenario.Rotate, scenario.Workload.ToString(), scenario.Output.ToString(),
                tiesky, imageSharp, openInspection, sharpInspection, psnr, exactPixels, dimensionsMatch);
            rows.Add(row);
            Print(scenario.Name, tiesky);
            Print(scenario.Name, imageSharp);

            double latencyRatio = tiesky.MedianMilliseconds / imageSharp.MedianMilliseconds;
            double memoryRatio = tiesky.PeakPrivateBytes / (double)imageSharp.PeakPrivateBytes;
            bool qualityPassed = dimensionsMatch && (scenario.Output == OutputCodec.Png ? psnr >= 38 : psnr >= 30);
            ScenarioResult? baselineRow = baseline?.Scenarios.FirstOrDefault(candidate => candidate.Key == scenario.Key);
            bool outputSizePassed = baselineRow is null || tiesky.OutputBytes <= baselineRow.Tiesky.OutputBytes * 1.05;
            bool preservedJpegQuality = dimensionsMatch && outputSizePassed &&
                (baselineRow is null || psnr + 0.01 >= baselineRow.Psnr);
            bool preservedWebpQuality = dimensionsMatch && outputSizePassed &&
                (baselineRow is null || psnr + 0.01 >= baselineRow.Psnr);
            bool jpegGateRow = scenario.Suite == "jpeg" &&
                (scenario.Workload != Workload.Transform || scenario.Width == 1600);
            bool scenarioPassed = gate switch
            {
                GateMode.Parity => latencyRatio <= 1.10 && memoryRatio <= 1.10 && qualityPassed,
                GateMode.PngParity => scenario.Suite != "png" || (latencyRatio <= 1.05 && memoryRatio <= 1.10 && qualityPassed && outputSizePassed),
                GateMode.JpegParity => !jpegGateRow || (latencyRatio <= 1.05 && memoryRatio <= 1.10 && preservedJpegQuality),
                GateMode.WebpParity => scenario.Suite is not ("vp8l" or "vp8") ||
                    (latencyRatio <= 1.05 && memoryRatio <= 1.10 && preservedWebpQuality),
                GateMode.Outperform => latencyRatio <= .85 && memoryRatio <= .75 && qualityPassed,
                _ => qualityPassed,
            };
            gatePassed &= scenarioPassed;
            Console.WriteLine($"  comparison: latency {latencyRatio:P1}, private {memoryRatio:P1}, PSNR {Format(psnr)} dB, exact {exactPixels}, {(scenarioPassed ? "PASS" : "FAIL")}");
        }

        BenchmarkReport report = new(DateTimeOffset.UtcNow, iterations, warmups, CaptureEnvironment(), rows);
        if (jsonPath is not null)
        {
            WriteReport(jsonPath, report);
            Console.WriteLine($"JSON report: {Path.GetFullPath(jsonPath)}");
        }

        if (htmlPath is not null)
        {
            WriteHtml(htmlPath, report, baseline);
            Console.WriteLine($"HTML report: {Path.GetFullPath(htmlPath)}");
        }
    }
    finally
    {
        Directory.Delete(temporary, recursive: true);
    }

    return gate != GateMode.None && !gatePassed ? 1 : 0;
}

static List<Scenario> CreateScenarios(string fixtureDirectory, string suiteArgument, int[] widths)
{
    HashSet<string> selected = suiteArgument.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => value.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    HashSet<string> valid = new(StringComparer.OrdinalIgnoreCase) { "all", "jpeg", "png", "bmp", "vp8l", "vp8", "encoders" };
    if (selected.Count == 0 || selected.Any(value => !valid.Contains(value)))
    {
        throw new ArgumentException("--suite accepts all or a comma-separated subset of jpeg,png,bmp,vp8l,vp8,encoders.");
    }

    bool Include(string suite) => selected.Contains("all") || selected.Contains(suite);
    List<Scenario> scenarios = [];
    if (Include("jpeg"))
    {
        string jpeg = Path.Combine(fixtureDirectory, "benchmark-12mp.jpg");
        scenarios.Add(new Scenario("jpeg-decode-12mp", "jpeg", "JPEG decode 4000x3000", jpeg, 4000, false, Workload.Decode, OutputCodec.Png));
        scenarios.Add(new Scenario("jpeg-rotate-12mp-r90", "jpeg", "JPEG rotate 4000x3000 R90", jpeg, 4000, true, Workload.Rotate, OutputCodec.Png));
        scenarios.Add(new Scenario("jpeg-resize-12mp-1600", "jpeg", "JPEG resize 4000x3000 to 1600px", jpeg, 1600, false, Workload.Resize, OutputCodec.Png));
        scenarios.Add(new Scenario("jpeg-encode-12mp-q85", "jpeg", "JPEG encoder 4000x3000 Q85", jpeg, 4000, false, Workload.EncodeDecoded, OutputCodec.Jpeg));
    }
    AddTransformSuite("jpeg", "JPEG to JPEG", "benchmark-12mp.jpg", OutputCodec.Jpeg);
    if (Include("png"))
    {
        string png = Path.Combine(fixtureDirectory, "benchmark-12mp.png");
        scenarios.Add(new Scenario("png-decode-12mp", "png", "PNG decode 4000x3000", png, 4000, false, Workload.Decode, OutputCodec.Png));
        scenarios.Add(new Scenario("png-rotate-12mp-r90", "png", "PNG rotate 4000x3000 R90", png, 4000, true, Workload.Rotate, OutputCodec.Png));
        scenarios.Add(new Scenario("png-encode-12mp-l6", "png", "PNG encoder 4000x3000 L6", png, 4000, false, Workload.EncodeDecoded, OutputCodec.Png));
    }
    AddTransformSuite("png", "PNG to PNG", "benchmark-12mp.png", OutputCodec.Png);
    AddTransformSuite("bmp", "BMP to PNG", "benchmark-12mp.bmp", OutputCodec.Png);
    if (Include("vp8l"))
    {
        string vp8l = Path.Combine(fixtureDirectory, "benchmark-12mp-lossless.webp");
        scenarios.Add(new Scenario("vp8l-decode-12mp", "vp8l", "VP8L decode 4000x3000", vp8l, 4000, false, Workload.Decode, OutputCodec.Png));
        scenarios.Add(new Scenario("vp8l-rotate-12mp-r90", "vp8l", "VP8L rotate 4000x3000 R90", vp8l, 4000, true, Workload.Rotate, OutputCodec.Png));
        scenarios.Add(new Scenario("vp8l-resize-12mp-1600", "vp8l", "VP8L resize 4000x3000 to 1600px", vp8l, 1600, false, Workload.Resize, OutputCodec.Png));
        scenarios.Add(new Scenario("vp8l-output-encode-12mp-png-l6", "vp8l", "VP8L decoded output to PNG 4000x3000 L6", vp8l, 4000, false, Workload.EncodeDecoded, OutputCodec.Png));
    }
    AddTransformSuite("vp8l", "VP8L to PNG", "benchmark-12mp-lossless.webp", OutputCodec.Png);
    if (Include("vp8"))
    {
        string vp8 = Path.Combine(fixtureDirectory, "benchmark-12mp-lossy.webp");
        scenarios.Add(new Scenario("vp8-decode-12mp", "vp8", "VP8 decode 4000x3000", vp8, 4000, false, Workload.Decode, OutputCodec.Png));
        scenarios.Add(new Scenario("vp8-rotate-12mp-r90", "vp8", "VP8 rotate 4000x3000 R90", vp8, 4000, true, Workload.Rotate, OutputCodec.Png));
        scenarios.Add(new Scenario("vp8-resize-12mp-1600", "vp8", "VP8 resize 4000x3000 to 1600px", vp8, 1600, false, Workload.Resize, OutputCodec.Png));
        scenarios.Add(new Scenario("vp8-output-encode-12mp-jpeg-q85", "vp8", "VP8 decoded output to JPEG 4000x3000 Q85", vp8, 4000, false, Workload.EncodeDecoded, OutputCodec.Jpeg));
    }
    AddTransformSuite("vp8", "VP8 to JPEG", "benchmark-12mp-lossy.webp", OutputCodec.Jpeg);
    if (Include("encoders"))
    {
        string raw = Path.Combine(fixtureDirectory, "encoder-1600x1200.rgba");
        scenarios.Add(new Scenario("encode-png-l6", "encoders", "PNG encoder 1600x1200 L6", raw, 1600, false, Workload.Encode, OutputCodec.Png));
        scenarios.Add(new Scenario("encode-jpeg-q85", "encoders", "JPEG encoder 1600x1200 Q85", raw, 1600, false, Workload.Encode, OutputCodec.Jpeg));
    }

    return scenarios;

    void AddTransformSuite(string suite, string title, string fileName, OutputCodec output)
    {
        if (!Include(suite)) return;
        foreach (int width in widths)
        foreach (bool rotate in new[] { false, true })
        {
            string key = $"{suite}-{width}-{(rotate ? "r90" : "r0")}";
            string name = $"{title} {width}px {(rotate ? "R90" : "R0")}";
            scenarios.Add(new Scenario(key, suite, name, Path.Combine(fixtureDirectory, fileName), width, rotate, Workload.Transform, output));
        }
    }
}

static int RunWorker(string[] args)
{
    string engine = args[1];
    Workload workload = Enum.Parse<Workload>(args[2], ignoreCase: true);
    string inputPath = args[3];
    int width = int.Parse(args[4], CultureInfo.InvariantCulture);
    int iterations = int.Parse(args[5], CultureInfo.InvariantCulture);
    int warmups = int.Parse(args[6], CultureInfo.InvariantCulture);
    bool rotate = bool.Parse(args[7]);
    OutputCodec output = Enum.Parse<OutputCodec>(args[8], ignoreCase: true);
    string outputPath = args[9];

    byte[] input = File.ReadAllBytes(inputPath);
    IDisposable? context = null;
    Func<OperationSample> operation;
    if (workload == Workload.Encode)
    {
        (int rawWidth, int rawHeight) = ReadRaw(input);
        if (engine == "tiesky")
        {
            PixelBuffer buffer = new(rawWidth, rawHeight);
            input.AsSpan(8).CopyTo(buffer.Span);
            context = buffer;
            operation = () => TieskyEncode(buffer, output);
        }
        else if (engine == "imagesharp")
        {
            Image<Rgba32> image = SlImage.LoadPixelData<Rgba32>(input.AsSpan(8), rawWidth, rawHeight);
            context = image;
            operation = () => ImageSharpEncode(image, output);
        }
        else
        {
            throw new ArgumentException("Unknown engine.");
        }
    }
    else if (workload is Workload.Rotate or Workload.Resize or Workload.EncodeDecoded)
    {
        if (engine == "tiesky")
        {
            DecodedImage decoded = ImageDecoder.Decode(input, 100_000_000, new DecodeRequest(ImageRotation.None, null));
            context = decoded;
            operation = workload switch
            {
                Workload.Rotate => () => TieskyRotate(decoded),
                Workload.Resize => () => TieskyResize(decoded, width),
                _ => () => TieskyEncode(decoded.Pixels, output, decoded.IsOpaque),
            };
        }
        else if (engine == "imagesharp")
        {
            Image<Rgba32> image = SlImage.Load<Rgba32>(input);
            context = image;
            operation = workload switch
            {
                Workload.Rotate => () => ImageSharpRotate(image),
                Workload.Resize => () => ImageSharpResize(image, width),
                _ => () => ImageSharpEncode(image, output),
            };
        }
        else
        {
            throw new ArgumentException("Unknown engine.");
        }
    }
    else if (workload == Workload.Decode)
    {
        operation = engine switch
        {
            "tiesky" => () => TieskyDecode(input),
            "imagesharp" => () => ImageSharpDecode(input),
            _ => throw new ArgumentException("Unknown engine."),
        };
    }
    else
    {
        operation = engine switch
        {
            "tiesky" => () => TieskyTransform(input, width, rotate, output),
            "imagesharp" => () => ImageSharpTransform(input, width, rotate, output),
            _ => throw new ArgumentException("Unknown engine."),
        };
    }

    try
    {
        for (int i = 0; i < warmups; i++) _ = operation();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long peak = process.PrivateMemorySize64;
        using CancellationTokenSource stop = new();
        Task sampler = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                InterlockedExtensions.Max(ref peak, process.PrivateMemorySize64);
                Thread.Sleep(1);
            }
        });

        double[] total = new double[iterations];
        double[] decode = new double[iterations];
        double[] transform = new double[iterations];
        double[] encode = new double[iterations];
        byte[]? lastOutput = null;
        for (int i = 0; i < iterations; i++)
        {
            if (i != 0)
            {
                lastOutput = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            long start = Stopwatch.GetTimestamp();
            OperationSample sample = operation();
            total[i] = sample.MeasuredMilliseconds ?? Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            decode[i] = sample.DecodeMilliseconds;
            transform[i] = sample.TransformMilliseconds;
            encode[i] = sample.EncodeMilliseconds;
            lastOutput = sample.Output;
        }

        stop.Cancel(); sampler.GetAwaiter().GetResult();
        process.Refresh(); InterlockedExtensions.Max(ref peak, process.PrivateMemorySize64);
        File.WriteAllBytes(outputPath, lastOutput!);
        Result result = new(engine, Median(total), Median(decode), Median(transform), Median(encode), peak, lastOutput!.Length);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }
    finally
    {
        context?.Dispose();
    }
}

static (int Width, int Height) ReadRaw(byte[] data)
{
    if (data.Length < 8) throw new InvalidDataException("Encoder RGBA fixture is truncated.");
    int width = BitConverter.ToInt32(data, 0);
    int height = BitConverter.ToInt32(data, 4);
    if (data.Length != checked(8 + width * height * 4)) throw new InvalidDataException("Encoder RGBA fixture length is invalid.");
    return (width, height);
}

static OperationSample TieskyTransform(byte[] input, int width, bool rotate, OutputCodec outputCodec)
{
    byte[] output = ImageTransformer.TransformProfiled(input, CreateOptions(width, rotate, outputCodec), out TransformTimings timings);
    return new OperationSample(output, timings.DecodeMilliseconds, timings.TransformMilliseconds, timings.EncodeMilliseconds);
}

static OperationSample TieskyDecode(byte[] input)
{
    long start = Stopwatch.GetTimestamp();
    using DecodedImage decoded = ImageDecoder.Decode(input, 100_000_000, new DecodeRequest(ImageRotation.None, null));
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    byte[] output = EncodeTieskyPng(decoded.Pixels, decoded.IsOpaque);
    return new OperationSample(output, elapsed, 0, 0, elapsed);
}

static OperationSample ImageSharpDecode(byte[] input)
{
    long start = Stopwatch.GetTimestamp();
    using Image<Rgba32> image = SlImage.Load<Rgba32>(input);
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    using MemoryStream destination = new();
    SaveImageSharp(image, destination, OutputCodec.Png);
    return new OperationSample(destination.ToArray(), elapsed, 0, 0, elapsed);
}

static OperationSample TieskyRotate(DecodedImage source)
{
    long start = Stopwatch.GetTimestamp();
    PixelBuffer rotated = TransformProcessor.Apply(source, ImageRotation.Clockwise90, null);
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    try
    {
        return new OperationSample(EncodeTieskyPng(rotated, source.IsOpaque), 0, elapsed, 0, elapsed);
    }
    finally
    {
        if (!ReferenceEquals(rotated, source.Pixels)) rotated.Dispose();
    }
}

static OperationSample ImageSharpRotate(Image<Rgba32> source)
{
    long start = Stopwatch.GetTimestamp();
    using Image<Rgba32> rotated = source.Clone(context => context.Rotate(90));
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    using MemoryStream destination = new();
    SaveImageSharp(rotated, destination, OutputCodec.Png);
    return new OperationSample(destination.ToArray(), 0, elapsed, 0, elapsed);
}

static OperationSample TieskyResize(DecodedImage source, int width)
{
    Tiesky.Image2D.ResizeOptions options = new()
    {
        Width = width,
        Height = width,
        Mode = Tiesky.Image2D.ResizeMode.Contain,
        Filter = ResizeFilter.Lanczos3,
    };
    long start = Stopwatch.GetTimestamp();
    PixelBuffer resized = TransformProcessor.Apply(source, ImageRotation.None, options);
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    try
    {
        return new OperationSample(EncodeTieskyPng(resized, source.IsOpaque), 0, elapsed, 0, elapsed);
    }
    finally
    {
        if (!ReferenceEquals(resized, source.Pixels)) resized.Dispose();
    }
}

static OperationSample ImageSharpResize(Image<Rgba32> source, int width)
{
    long start = Stopwatch.GetTimestamp();
    using Image<Rgba32> resized = source.Clone(context => context.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
    {
        Size = new SixLabors.ImageSharp.Size(width, width),
        Mode = SlResizeMode.Max,
        Sampler = KnownResamplers.Lanczos3,
    }));
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    using MemoryStream destination = new();
    SaveImageSharp(resized, destination, OutputCodec.Png);
    return new OperationSample(destination.ToArray(), 0, elapsed, 0, elapsed);
}

static byte[] EncodeTieskyPng(PixelBuffer pixels, bool isOpaque)
{
    using MemoryStream destination = new();
    PngEncoder.Encode(pixels, destination, new Tiesky.Image2D.PngEncoderOptions { CompressionLevel = 6 }, isOpaque);
    return destination.ToArray();
}

static TransformOptions CreateOptions(int width, bool rotate, OutputCodec outputCodec) => new()
{
    Encoder = outputCodec == OutputCodec.Png
        ? new Tiesky.Image2D.PngEncoderOptions { CompressionLevel = 6 }
        : new Tiesky.Image2D.JpegEncoderOptions { Quality = 85, ChromaSubsampling = JpegChromaSubsampling.Yuv420 },
    Rotation = rotate ? ImageRotation.Clockwise90 : ImageRotation.None,
    Resize = new Tiesky.Image2D.ResizeOptions
    {
        Width = width, Height = width, Mode = Tiesky.Image2D.ResizeMode.Contain, Filter = ResizeFilter.Lanczos3,
    },
};

static OperationSample ImageSharpTransform(byte[] input, int width, bool rotate, OutputCodec outputCodec)
{
    long start = Stopwatch.GetTimestamp();
    using Image image = SlImage.Load(input);
    double decode = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    start = Stopwatch.GetTimestamp();
    image.Mutate(context =>
    {
        context.AutoOrient();
        if (rotate) context.Rotate(90);
        context.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(width, width), Mode = SlResizeMode.Max, Sampler = KnownResamplers.Lanczos3,
        });
    });
    double transform = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    using MemoryStream destination = new();
    start = Stopwatch.GetTimestamp();
    SaveImageSharp(image, destination, outputCodec);
    double encode = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    return new OperationSample(destination.ToArray(), decode, transform, encode);
}

static OperationSample TieskyEncode(PixelBuffer pixels, OutputCodec outputCodec, bool? knownOpaque = null)
{
    using MemoryStream destination = new();
    long start = Stopwatch.GetTimestamp();
    if (outputCodec == OutputCodec.Png)
        PngEncoder.Encode(pixels, destination, new Tiesky.Image2D.PngEncoderOptions { CompressionLevel = 6 }, knownOpaque ?? PixelsAreOpaque(pixels));
    else
        JpegEncoder.Encode(pixels, destination, new Tiesky.Image2D.JpegEncoderOptions { Quality = 85, ChromaSubsampling = JpegChromaSubsampling.Yuv420 });
    double encode = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    return new OperationSample(destination.ToArray(), 0, 0, encode);
}

static bool PixelsAreOpaque(PixelBuffer pixels)
{
    ReadOnlySpan<byte> data = pixels.Span;
    for (int offset = 3; offset < data.Length; offset += 4)
        if (data[offset] != 255) return false;
    return true;
}

static OperationSample ImageSharpEncode(Image<Rgba32> image, OutputCodec outputCodec)
{
    using MemoryStream destination = new();
    long start = Stopwatch.GetTimestamp();
    SaveImageSharp(image, destination, outputCodec);
    double encode = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    return new OperationSample(destination.ToArray(), 0, 0, encode);
}

static void SaveImageSharp(Image image, Stream destination, OutputCodec outputCodec)
{
    if (outputCodec == OutputCodec.Png)
        image.Save(destination, new SlPngEncoder { CompressionLevel = SlPngCompressionLevel.Level6 });
    else
        image.Save(destination, new SlJpegEncoder { Quality = 85 });
}

static Result RunChild(string engine, Scenario scenario, int iterations, int warmups, string outputPath)
{
    ProcessStartInfo start = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (string argument in new[]
    {
        "--worker", engine, scenario.Workload.ToString(), scenario.InputPath, scenario.Width.ToString(CultureInfo.InvariantCulture),
        iterations.ToString(CultureInfo.InvariantCulture), warmups.ToString(CultureInfo.InvariantCulture), scenario.Rotate.ToString(),
        scenario.Output.ToString(), outputPath,
    }) start.ArgumentList.Add(argument);

    using Process child = Process.Start(start)!;
    string output = child.StandardOutput.ReadToEnd();
    string error = child.StandardError.ReadToEnd();
    child.WaitForExit();
    if (child.ExitCode != 0) throw new InvalidOperationException($"{engine} worker failed for {scenario.Name}: {error}{Environment.NewLine}{output}");
    return JsonSerializer.Deserialize<Result>(output.Trim()) ?? throw new InvalidOperationException("Worker emitted no result.");
}

static OutputInspection InspectOutput(string path)
{
    using Image<Rgba32> image = SlImage.Load<Rgba32>(path);
    byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
    image.CopyPixelDataTo(pixels);
    return new OutputInspection(image.Width, image.Height, Convert.ToHexString(SHA256.HashData(pixels)));
}

static double CalculatePsnr(string firstPath, string secondPath)
{
    using Image<Rgba32> first = SlImage.Load<Rgba32>(firstPath);
    using Image<Rgba32> second = SlImage.Load<Rgba32>(secondPath);
    if (first.Width != second.Width || first.Height != second.Height) return 0;
    byte[] left = new byte[checked(first.Width * first.Height * 4)];
    byte[] right = new byte[left.Length];
    first.CopyPixelDataTo(left); second.CopyPixelDataTo(right);
    double squaredError = 0;
    long samples = 0;
    for (int i = 0; i < left.Length; i += 4)
    for (int channel = 0; channel < 3; channel++)
    {
        int difference = left[i + channel] - right[i + channel];
        squaredError += difference * difference;
        samples++;
    }
    if (squaredError == 0) return 999;
    return 10 * Math.Log10(255d * 255d / (squaredError / samples));
}

static EnvironmentInfo CaptureEnvironment()
{
    AssemblyName production = typeof(ImageTransformer).Assembly.GetName();
    AssemblyName imageSharp = typeof(SlImage).Assembly.GetName();
    return new EnvironmentInfo(
        RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount, Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unavailable", GCSettings.IsServerGC,
        $"{production.Name} {production.Version}", $"{imageSharp.Name} 3.1.12 (assembly {imageSharp.Version})");
}

static void WriteReport(string path, BenchmarkReport report)
{
    path = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(report, BenchmarkJson.Options), new UTF8Encoding(false));
}

static BenchmarkReport ReadReport(string path)
{
    string text = File.ReadAllText(path);
    if (!Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase))
    {
        return JsonSerializer.Deserialize<BenchmarkReport>(text, BenchmarkJson.Options)
            ?? throw new InvalidDataException("Baseline benchmark JSON is empty.");
    }

    const string opening = "<script id=\"benchmark-data\" type=\"application/json\">";
    const string closing = "</script>";
    int start = text.IndexOf(opening, StringComparison.Ordinal);
    if (start < 0) throw new InvalidDataException("The HTML benchmark has no embedded data.");
    start += opening.Length;
    int end = text.IndexOf(closing, start, StringComparison.Ordinal);
    if (end < 0) throw new InvalidDataException("The HTML benchmark data is truncated.");
    using JsonDocument document = JsonDocument.Parse(text.Substring(start, end - start));
    if (!document.RootElement.TryGetProperty("Baseline", out JsonElement baseline) || baseline.ValueKind == JsonValueKind.Null)
        throw new InvalidDataException("The HTML benchmark has no embedded baseline.");
    return baseline.Deserialize<BenchmarkReport>(BenchmarkJson.Options)
        ?? throw new InvalidDataException("The embedded HTML baseline is empty.");
}

static BenchmarkReport ReadFinalReport(string path)
{
    string text = File.ReadAllText(path);
    if (!Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase))
    {
        return JsonSerializer.Deserialize<BenchmarkReport>(text, BenchmarkJson.Options)
            ?? throw new InvalidDataException("Final benchmark JSON is empty.");
    }

    const string opening = "<script id=\"benchmark-data\" type=\"application/json\">";
    const string closing = "</script>";
    int start = text.IndexOf(opening, StringComparison.Ordinal);
    if (start < 0) throw new InvalidDataException("The HTML benchmark has no embedded data.");
    start += opening.Length;
    int end = text.IndexOf(closing, start, StringComparison.Ordinal);
    if (end < 0) throw new InvalidDataException("The HTML benchmark data is truncated.");
    using JsonDocument document = JsonDocument.Parse(text.Substring(start, end - start));
    if (!document.RootElement.TryGetProperty("Final", out JsonElement final) || final.ValueKind == JsonValueKind.Null)
        throw new InvalidDataException("The HTML benchmark has no embedded final report.");
    return final.Deserialize<BenchmarkReport>(BenchmarkJson.Options)
        ?? throw new InvalidDataException("The embedded HTML final report is empty.");
}

static BenchmarkReport ReadBaselineSet(string paths)
{
    BenchmarkReport[] reports = paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ReadReport).ToArray();
    if (reports.Length == 0) throw new ArgumentException("At least one baseline report path is required.");
    if (reports.Length == 1) return reports[0];
    Dictionary<string, ScenarioResult> rows = new(StringComparer.Ordinal);
    foreach (BenchmarkReport report in reports)
    foreach (ScenarioResult row in report.Scenarios)
        rows[row.Key] = row;
    BenchmarkReport latest = reports[^1];
    return latest with { Scenarios = rows.Values.ToList() };
}

static void WriteHtml(string path, BenchmarkReport report, BenchmarkReport? baseline)
{
    Dictionary<string, ScenarioResult> baselineRows = (baseline?.Scenarios ?? []).ToDictionary(row => row.Key, StringComparer.Ordinal);
    StringBuilder html = new();
    html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
        .Append("<title>Tiesky.Image2D benchmark results</title><style>")
        .Append("body{font:14px/1.45 system-ui,-apple-system,Segoe UI,sans-serif;margin:0;background:#f4f6f8;color:#17212b}main{max-width:1500px;margin:auto;padding:32px}h1{margin:0 0 8px}h2{margin-top:32px}.meta,.note{background:white;border:1px solid #d9e0e7;border-radius:8px;padding:16px}.meta{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:8px 20px}table{width:100%;border-collapse:collapse;background:white;font-variant-numeric:tabular-nums}th,td{border:1px solid #d9e0e7;padding:7px 8px;text-align:right;white-space:nowrap}th{background:#eaf0f5;position:sticky;top:0}th:first-child,td:first-child{text-align:left}.good{color:#087b35;font-weight:650}.bad{color:#b3261e;font-weight:650}.muted{color:#607080}.scroll{overflow:auto;border-radius:8px;box-shadow:0 1px 3px #0001}code{font-size:12px}</style></head><body><main>")
        .Append("<h1>Tiesky.Image2D codec performance</h1><p class=\"muted\">Self-contained benchmark report. Lower latency and memory ratios are better.</p>")
        .Append("<section class=\"meta\">");
    AddMeta("Generated (UTC)", report.GeneratedUtc.ToString("u", CultureInfo.InvariantCulture));
    AddMeta("Runtime", report.Environment.Framework);
    AddMeta("Operating system", report.Environment.OperatingSystem);
    AddMeta("Architecture", report.Environment.Architecture);
    AddMeta("Processor", report.Environment.Processor);
    AddMeta("Logical processors", report.Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
    AddMeta("Server GC", report.Environment.ServerGc.ToString());
    AddMeta("Iterations", $"{report.Warmups} warmups / {report.Iterations} measured");
    AddMeta("Scenarios", report.Scenarios.Count.ToString(CultureInfo.InvariantCulture));
    AddMeta("Tiesky", report.Environment.TieskyAssembly);
    AddMeta("Reference", report.Environment.ImageSharpAssembly);
    AddMeta("Adaptive parallelism", "rotation ≥1 MP; resize ≥4M taps; JPEG IDCT pipeline ≥2M work; JPEG encode ≥1 MP; PNG filtering ≥4 MiB; VP8 token/reconstruction pipeline ≥1 MP; WebP output conversion capped at 4 workers");
    if (baseline is not null) AddMeta("Baseline sample", $"{baseline.Warmups} warmups / {baseline.Iterations} measured");
    html.Append("</section><p class=\"note\">PNG rows, the JPEG component/1600 px gate rows, and every VP8L/VP8 component and transform row target final/ImageSharp median latency ≤1.05x and peak private bytes ≤1.10x; dimensions and quality must also pass. WebP remains decode-only: its encode components measure the existing VP8L→PNG level 6 and VP8→JPEG quality 85 output stages. Total time includes output materialization, while decode/rotation/resize/encode component medians are measured inside each implementation. Peak private bytes are sampled in isolated worker processes. JPEG rows retain the requested final self-baseline (1.00x); their primary performance comparison is against ImageSharp.</p>");

    List<(ScenarioResult Final, ScenarioResult Baseline)> jpegChanges = report.Scenarios
        .Where(row => row.Suite.Equals("jpeg", StringComparison.OrdinalIgnoreCase) && baselineRows.ContainsKey(row.Key))
        .Select(row => (row, baselineRows[row.Key]))
        .ToList();
    if (jpegChanges.Count != 0)
    {
        html.Append("<h2>JPEG pre-optimization to final</h2><div class=\"scroll\"><table><thead><tr>")
            .Append("<th>Scenario</th><th>Before ms</th><th>Final ms</th><th>Before / final</th><th>Before MiB</th><th>Final MiB</th><th>Final / before memory</th></tr></thead><tbody>");
        foreach ((ScenarioResult finalRow, ScenarioResult beforeRow) in jpegChanges)
        {
            html.Append("<tr class=\"improvement\"><td>").Append(H(finalRow.Name)).Append("</td>")
                .Append(Td(beforeRow.Tiesky.MedianMilliseconds)).Append(Td(finalRow.Tiesky.MedianMilliseconds))
                .Append(SpeedupTd(beforeRow.Tiesky.MedianMilliseconds / finalRow.Tiesky.MedianMilliseconds))
                .Append(Td(beforeRow.Tiesky.PeakPrivateBytes / 1048576d, 1)).Append(Td(finalRow.Tiesky.PeakPrivateBytes / 1048576d, 1))
                .Append(RatioTd(finalRow.Tiesky.PeakPrivateBytes / (double)beforeRow.Tiesky.PeakPrivateBytes)).Append("</tr>");
        }

        html.Append("</tbody></table></div>");
    }

    List<(ScenarioResult Final, ScenarioResult Baseline)> webpChanges = report.Scenarios
        .Where(row => row.Suite is "vp8l" or "vp8" && baselineRows.ContainsKey(row.Key))
        .Select(row => (row, baselineRows[row.Key]))
        .ToList();
    if (webpChanges.Count != 0)
    {
        html.Append("<h2>WebP pre-optimization to final</h2><div class=\"scroll\"><table><thead><tr>")
            .Append("<th>Scenario</th><th>Before ms</th><th>Final ms</th><th>Before / final</th><th>Before MiB</th><th>Final MiB</th><th>Final / before memory</th></tr></thead><tbody>");
        foreach ((ScenarioResult finalRow, ScenarioResult beforeRow) in webpChanges)
        {
            html.Append("<tr class=\"improvement\"><td>").Append(H(finalRow.Name)).Append("</td>")
                .Append(Td(beforeRow.Tiesky.MedianMilliseconds)).Append(Td(finalRow.Tiesky.MedianMilliseconds))
                .Append(SpeedupTd(beforeRow.Tiesky.MedianMilliseconds / finalRow.Tiesky.MedianMilliseconds))
                .Append(Td(beforeRow.Tiesky.PeakPrivateBytes / 1048576d, 1)).Append(Td(finalRow.Tiesky.PeakPrivateBytes / 1048576d, 1))
                .Append(RatioTd(finalRow.Tiesky.PeakPrivateBytes / (double)beforeRow.Tiesky.PeakPrivateBytes)).Append("</tr>");
        }

        html.Append("</tbody></table></div>");
    }

    foreach (IGrouping<string, ScenarioResult> group in report.Scenarios.GroupBy(row => row.Suite))
    {
        html.Append("<h2>").Append(H(group.Key.ToUpperInvariant())).Append("</h2><div class=\"scroll\"><table><thead><tr>")
            .Append("<th>Scenario</th><th>Final total ms</th><th>Baseline total ms</th><th>Baseline / final</th><th>ImageSharp ms</th><th>Final / IS</th><th>Final decode</th><th>Final resize</th><th>Final encode</th><th>Baseline decode</th><th>Baseline resize</th><th>Baseline encode</th><th>Final MiB</th><th>Baseline MiB</th><th>Final / baseline memory</th><th>IS MiB</th><th>Final / IS memory</th><th>Output bytes</th><th>Dimensions</th><th>PSNR dB</th><th>Exact hash</th></tr></thead><tbody>");
        foreach (ScenarioResult row in group)
        {
            baselineRows.TryGetValue(row.Key, out ScenarioResult? baselineRow);
            if (row.Suite.Equals("jpeg", StringComparison.OrdinalIgnoreCase)) baselineRow = row;
            double? baselineTime = baselineRow?.Tiesky.MedianMilliseconds;
            double? baselineSpeedup = baselineTime is null ? null : baselineTime.Value / row.Tiesky.MedianMilliseconds;
            double? baselineMemoryRatio = baselineRow is null ? null : row.Tiesky.PeakPrivateBytes / (double)baselineRow.Tiesky.PeakPrivateBytes;
            double sharpRatio = row.Tiesky.MedianMilliseconds / row.ImageSharp.MedianMilliseconds;
            double memoryRatio = row.Tiesky.PeakPrivateBytes / (double)row.ImageSharp.PeakPrivateBytes;
            html.Append("<tr><td>").Append(H(row.Name)).Append("</td>")
                .Append(Td(row.Tiesky.MedianMilliseconds)).Append(Td(baselineTime)).Append(SpeedupTd(baselineSpeedup))
                .Append(Td(row.ImageSharp.MedianMilliseconds)).Append(RatioTd(sharpRatio))
                .Append(Td(row.Tiesky.DecodeMilliseconds)).Append(Td(row.Tiesky.TransformMilliseconds)).Append(Td(row.Tiesky.EncodeMilliseconds))
                .Append(Td(baselineRow?.Tiesky.DecodeMilliseconds)).Append(Td(baselineRow?.Tiesky.TransformMilliseconds)).Append(Td(baselineRow?.Tiesky.EncodeMilliseconds))
                .Append(Td(row.Tiesky.PeakPrivateBytes / 1048576d, 1)).Append(Td(baselineRow?.Tiesky.PeakPrivateBytes / 1048576d, 1)).Append(RatioTd(baselineMemoryRatio))
                .Append(Td(row.ImageSharp.PeakPrivateBytes / 1048576d, 1)).Append(RatioTd(memoryRatio))
                .Append("<td>").Append(row.Tiesky.OutputBytes.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>")
                .Append("<td class=\"").Append(row.DimensionsMatch ? "good" : "bad").Append("\">").Append(row.OpenOutput.Width).Append('x').Append(row.OpenOutput.Height).Append("</td>")
                .Append(Td(row.Psnr, 2))
                .Append("<td class=\"").Append(row.ExactPixels ? "good" : "muted").Append("\">").Append(row.ExactPixels ? "yes" : "no").Append("</td></tr>");
        }
        html.Append("</tbody></table></div>");
    }

    string embeddedJson = JsonSerializer.Serialize(new { Final = report, Baseline = baseline, SelfBaselineSuites = new[] { "jpeg" } }, BenchmarkJson.Options).Replace("<", "\\u003C", StringComparison.Ordinal);
    html.Append("<h2>Reproduction</h2><p class=\"note\"><code>dotnet run --project benchmarks/Tiesky.Image2D.Benchmarks -c Release -- --suite all --warmups 5 --iterations 15 --baseline _Docs/Implementation/BenchmarkResults.html --html _Docs/Implementation/BenchmarkResults.html</code></p>")
        .Append("<script id=\"benchmark-data\" type=\"application/json\">").Append(embeddedJson).Append("</script></main></body></html>");
    string document = html.ToString();
    ValidateHtml(document, report.Scenarios.Count);
    path = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, document, new UTF8Encoding(false));

    void AddMeta(string label, string value) => html.Append("<div><strong>").Append(H(label)).Append(":</strong> ").Append(H(value)).Append("</div>");
}

static void ValidateHtml(string html, int scenarioCount)
{
    if (!html.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase) || html.Contains("<link", StringComparison.OrdinalIgnoreCase) || html.Contains("src=\"http", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("HTML report must be a self-contained document without external assets.");
    int rows = html.Split("<tr><td>", StringSplitOptions.None).Length - 1;
    if (rows != scenarioCount) throw new InvalidDataException($"HTML report contains {rows} scenario rows, expected {scenarioCount}.");
    if (html.Contains("NaN", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("HTML report contains non-finite metrics.");
}

static string H(string value) => WebUtility.HtmlEncode(value);
static string Td(double? value, int decimals = 2) => value is null ? "<td class=\"muted\">n/a</td>" : $"<td>{value.Value.ToString($"F{decimals}", CultureInfo.InvariantCulture)}</td>";
static string RatioTd(double? ratio)
{
    if (ratio is null) return "<td class=\"muted\">n/a</td>";
    string cssClass = ratio <= 1 ? "good" : "bad";
    return $"<td class=\"{cssClass}\">{ratio.Value.ToString("F2", CultureInfo.InvariantCulture)}x</td>";
}
static string SpeedupTd(double? ratio)
{
    if (ratio is null) return "<td class=\"muted\">n/a</td>";
    string cssClass = ratio >= 1 ? "good" : "bad";
    return $"<td class=\"{cssClass}\">{ratio.Value.ToString("F2", CultureInfo.InvariantCulture)}x</td>";
}

static GateMode ReadGate(string[] arguments)
{
    int index = Array.IndexOf(arguments, "--gate");
    if (index < 0) return GateMode.None;
    if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)) return GateMode.Parity;
    return arguments[index + 1].ToLowerInvariant() switch
    {
        "parity" => GateMode.Parity,
        "png" or "png-parity" => GateMode.PngParity,
        "jpeg" or "jpeg-parity" => GateMode.JpegParity,
        "webp" or "webp-parity" => GateMode.WebpParity,
        "outperform" => GateMode.Outperform,
        _ => throw new ArgumentException("--gate must be parity, png-parity, jpeg-parity, webp-parity, or outperform."),
    };
}

static int ReadInt(string[] arguments, string key, int fallback) => ParsePositiveInt(ReadString(arguments, key, fallback.ToString(CultureInfo.InvariantCulture)));
static int ParsePositiveInt(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : throw new ArgumentException($"Expected a positive integer, got '{value}'.");
static string ReadString(string[] arguments, string key, string fallback) => ReadOptionalString(arguments, key) ?? fallback;
static string? ReadOptionalString(string[] arguments, string key)
{
    int index = Array.IndexOf(arguments, key);
    if (index < 0) return null;
    if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{key} requires a value.");
    return arguments[index + 1];
}

static double Median(double[] values) { Array.Sort(values); return values[values.Length / 2]; }
static string Format(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
static void Print(string scenario, Result result) => Console.WriteLine(
    $"{scenario,-28} {result.Engine,-12} {result.MedianMilliseconds,8:F2} {result.DecodeMilliseconds,9:F2} {result.TransformMilliseconds,9:F2} {result.EncodeMilliseconds,9:F2} {result.PeakPrivateBytes / 1024d / 1024d,11:F1} {result.OutputBytes,12}");

internal enum GateMode { None, Parity, PngParity, JpegParity, WebpParity, Outperform }
internal enum Workload { Transform, Encode, Decode, Rotate, Resize, EncodeDecoded }
internal enum OutputCodec { Png, Jpeg }
internal sealed record Scenario(string Key, string Suite, string Name, string InputPath, int Width, bool Rotate, Workload Workload, OutputCodec Output);
internal sealed record OperationSample(byte[] Output, double DecodeMilliseconds, double TransformMilliseconds, double EncodeMilliseconds, double? MeasuredMilliseconds = null);
internal sealed record Result(string Engine, double MedianMilliseconds, double DecodeMilliseconds, double TransformMilliseconds, double EncodeMilliseconds, long PeakPrivateBytes, int OutputBytes);
internal sealed record OutputInspection(int Width, int Height, string PixelHash);
internal sealed record ScenarioResult(string Key, string Suite, string Name, int Width, bool Rotate, string Workload, string OutputCodec, Result Tiesky, Result ImageSharp, OutputInspection OpenOutput, OutputInspection ImageSharpOutput, double Psnr, bool ExactPixels, bool DimensionsMatch);
internal sealed record EnvironmentInfo(string OperatingSystem, string Framework, string Architecture, int ProcessorCount, string Processor, bool ServerGc, string TieskyAssembly, string ImageSharpAssembly);
internal sealed record BenchmarkReport(DateTimeOffset GeneratedUtc, int Iterations, int Warmups, EnvironmentInfo Environment, List<ScenarioResult> Scenarios);

internal static class BenchmarkJson
{
    internal static JsonSerializerOptions Options { get; } = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Default };
}

internal static class InterlockedExtensions
{
    public static void Max(ref long target, long value)
    {
        long current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
