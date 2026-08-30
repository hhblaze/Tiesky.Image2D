using System.Buffers;
using System.Diagnostics;
using Tiesky.Image2D.Codecs;
using Tiesky.Image2D.Codecs.Jpeg;
using Tiesky.Image2D.Codecs.Png;
using Tiesky.Image2D.Internal;
using Tiesky.Image2D.Processing;

namespace Tiesky.Image2D;

/// <summary>Provides one-shot decode, ordered transformation, and encode operations.</summary>
public static class ImageTransformer
{
    /// <summary>Transforms encoded image bytes using the original convenience contract.</summary>
    public static byte[] Transform(ReadOnlySpan<byte> input, TransformOptions options)
    {
        SnapshotLegacy(options, out PipelineStep[] steps, out ImageEncoderOptions encoder, out InputLimits limits);
        return TransformToArrayCore(input, steps, encoder, limits);
    }

    /// <summary>Transforms encoded image bytes into a writable stream using the original convenience contract.</summary>
    public static void Transform(ReadOnlySpan<byte> input, Stream output, TransformOptions options)
    {
        SnapshotLegacy(options, out PipelineStep[] steps, out ImageEncoderOptions encoder, out InputLimits limits);
        TransformCore(input, output, steps, encoder, limits, collectTimings: false, out _);
    }

    /// <summary>Transforms an encoded input stream using the original convenience contract; both streams remain open.</summary>
    public static void Transform(Stream input, Stream output, TransformOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(input));
        }

        SnapshotLegacy(options, out PipelineStep[] steps, out ImageEncoderOptions encoder, out InputLimits limits);
        using PooledInputBuffer buffered = PooledInputBuffer.Read(input, limits.MaxInputBytes);
        TransformCore(buffered.Span, output, steps, encoder, limits, collectTimings: false, out _);
    }

    /// <summary>Transforms a byte array and returns a newly allocated encoded result.</summary>
    public static byte[] Transform(
        byte[] input,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Transform((ReadOnlySpan<byte>)input, transformations, encoder, readOptions);
    }

    /// <summary>Transforms encoded bytes and returns a newly allocated encoded result.</summary>
    public static byte[] Transform(
        ReadOnlySpan<byte> input,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        SnapshotPipeline(transformations, encoder, readOptions, out PipelineStep[] steps, out ImageEncoderOptions encoderSnapshot, out InputLimits limits);
        return TransformToArrayCore(input, steps, encoderSnapshot, limits);
    }

    /// <summary>Transforms an encoded input stream and returns a newly allocated encoded result.</summary>
    public static byte[] Transform(
        Stream input,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(input));
        }

        SnapshotPipeline(transformations, encoder, readOptions, out PipelineStep[] steps, out ImageEncoderOptions encoderSnapshot, out InputLimits limits);
        using PooledInputBuffer buffered = PooledInputBuffer.Read(input, limits.MaxInputBytes);
        return TransformToArrayCore(buffered.Span, steps, encoderSnapshot, limits);
    }

    /// <summary>Transforms a byte array and returns an owned stream positioned at the beginning.</summary>
    public static MemoryStream TransformToStream(
        byte[] input,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return TransformToStream((ReadOnlySpan<byte>)input, transformations, encoder, readOptions);
    }

    /// <summary>Transforms encoded bytes and returns an owned stream positioned at the beginning.</summary>
    public static MemoryStream TransformToStream(
        ReadOnlySpan<byte> input,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        SnapshotPipeline(transformations, encoder, readOptions, out PipelineStep[] steps, out ImageEncoderOptions encoderSnapshot, out InputLimits limits);
        return TransformToStreamCore(input, steps, encoderSnapshot, limits);
    }

    /// <summary>Transforms an encoded input stream and returns an owned stream positioned at the beginning.</summary>
    public static MemoryStream TransformToStream(
        Stream input,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(input));
        }

        SnapshotPipeline(transformations, encoder, readOptions, out PipelineStep[] steps, out ImageEncoderOptions encoderSnapshot, out InputLimits limits);
        using PooledInputBuffer buffered = PooledInputBuffer.Read(input, limits.MaxInputBytes);
        return TransformToStreamCore(buffered.Span, steps, encoderSnapshot, limits);
    }

    /// <summary>Transforms a byte array into a caller-owned writable stream.</summary>
    public static void Transform(
        byte[] input,
        Stream output,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        Transform((ReadOnlySpan<byte>)input, output, transformations, encoder, readOptions);
    }

    /// <summary>Transforms encoded bytes into a caller-owned writable stream.</summary>
    public static void Transform(
        ReadOnlySpan<byte> input,
        Stream output,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        SnapshotPipeline(transformations, encoder, readOptions, out PipelineStep[] steps, out ImageEncoderOptions encoderSnapshot, out InputLimits limits);
        TransformCore(input, output, steps, encoderSnapshot, limits, collectTimings: false, out _);
    }

    /// <summary>Transforms one caller-owned input stream into another; both remain open.</summary>
    public static void Transform(
        Stream input,
        Stream output,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(input));
        }

        SnapshotPipeline(transformations, encoder, readOptions, out PipelineStep[] steps, out ImageEncoderOptions encoderSnapshot, out InputLimits limits);
        using PooledInputBuffer buffered = PooledInputBuffer.Read(input, limits.MaxInputBytes);
        TransformCore(buffered.Span, output, steps, encoderSnapshot, limits, collectTimings: false, out _);
    }

    /// <summary>Transforms a byte array into a caller-owned buffer writer.</summary>
    public static void Transform(
        byte[] input,
        IBufferWriter<byte> output,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        Transform((ReadOnlySpan<byte>)input, output, transformations, encoder, readOptions);
    }

    /// <summary>Transforms encoded bytes into a caller-owned buffer writer.</summary>
    public static void Transform(
        ReadOnlySpan<byte> input,
        IBufferWriter<byte> output,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        SnapshotPipeline(transformations, encoder, readOptions, out PipelineStep[] steps, out ImageEncoderOptions encoderSnapshot, out InputLimits limits);
        using BufferWriterStream stream = new(output);
        TransformCore(input, stream, steps, encoderSnapshot, limits, collectTimings: false, out _);
    }

    /// <summary>Transforms an encoded input stream into a caller-owned buffer writer.</summary>
    public static void Transform(
        Stream input,
        IBufferWriter<byte> output,
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(input));
        }

        SnapshotPipeline(transformations, encoder, readOptions, out PipelineStep[] steps, out ImageEncoderOptions encoderSnapshot, out InputLimits limits);
        using PooledInputBuffer buffered = PooledInputBuffer.Read(input, limits.MaxInputBytes);
        using BufferWriterStream stream = new(output);
        TransformCore(buffered.Span, stream, steps, encoderSnapshot, limits, collectTimings: false, out _);
    }

    /// <summary>Runs the ordinary pipeline while collecting benchmark-only stage timings.</summary>
    internal static byte[] TransformProfiled(ReadOnlySpan<byte> input, TransformOptions options, out TransformTimings timings)
    {
        SnapshotLegacy(options, out PipelineStep[] steps, out ImageEncoderOptions encoder, out InputLimits limits);
        int capacity = Math.Clamp(input.Length, 256, 16 * 1024 * 1024);
        using MemoryStream output = new(capacity);
        TransformCore(input, output, steps, encoder, limits, collectTimings: true, out timings);
        return output.ToArray();
    }

    /// <summary>Executes the shared decode, ordered transformation, and encode pipeline.</summary>
    private static void TransformCore(
        ReadOnlySpan<byte> input,
        Stream output,
        PipelineStep[] steps,
        ImageEncoderOptions encoder,
        InputLimits limits,
        bool collectTimings,
        out TransformTimings timings)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        ValidateInput(limits, input.Length);
        PipelineStep first = steps.Length == 0 ? default : steps[0];
        long decodeStart = collectTimings ? Stopwatch.GetTimestamp() : 0;
        using DecodedImage decoded = ImageDecoder.Decode(input, limits.MaxInputPixels, new DecodeRequest(first.Rotation, first.Resize));
        long decodeTicks = collectTimings ? Stopwatch.GetTimestamp() - decodeStart : 0;
        long transformStart = collectTimings ? Stopwatch.GetTimestamp() : 0;
        PixelBuffer? transformed = null;
        try
        {
            transformed = TransformProcessor.Apply(decoded, first.Rotation, first.Resize);
            for (int i = steps.Length == 0 ? 0 : 1; i < steps.Length; i++)
            {
                PipelineStep step = steps[i];
                PixelBuffer next = TransformProcessor.Apply(transformed, decoded.IsOpaque, step.Rotation, step.Resize);
                if (!ReferenceEquals(next, transformed))
                {
                    if (!ReferenceEquals(transformed, decoded.Pixels))
                    {
                        transformed.Dispose();
                    }

                    transformed = next;
                }
            }

            long transformTicks = collectTimings ? Stopwatch.GetTimestamp() - transformStart : 0;
            long encodeStart = collectTimings ? Stopwatch.GetTimestamp() : 0;
            Encode(transformed, output, encoder, decoded.IsOpaque);
            long encodeTicks = collectTimings ? Stopwatch.GetTimestamp() - encodeStart : 0;
            timings = new TransformTimings(decodeTicks, transformTicks, encodeTicks);
        }
        finally
        {
            if (transformed is not null && !ReferenceEquals(transformed, decoded.Pixels))
            {
                transformed.Dispose();
            }
        }
    }

    /// <summary>Encodes one final pixel buffer using a validated option snapshot.</summary>
    private static void Encode(PixelBuffer pixels, Stream output, ImageEncoderOptions encoder, bool isOpaque)
    {
        switch (encoder)
        {
            case PngEncoderOptions png:
                PngEncoder.Encode(pixels, output, png, isOpaque);
                break;
            case JpegEncoderOptions jpeg:
                JpegEncoder.Encode(pixels, output, jpeg);
                break;
            default:
                throw new Image2DException(ImageErrorCode.InvalidOptions, "The output encoder is not supported.");
        }
    }

    /// <summary>Creates an encoded array without exposing the intermediate stream.</summary>
    private static byte[] TransformToArrayCore(ReadOnlySpan<byte> input, PipelineStep[] steps, ImageEncoderOptions encoder, InputLimits limits)
    {
        int capacity = Math.Clamp(input.Length, 256, 16 * 1024 * 1024);
        using MemoryStream output = new(capacity);
        TransformCore(input, output, steps, encoder, limits, collectTimings: false, out _);
        return output.ToArray();
    }

    /// <summary>Creates a caller-owned encoded stream and rewinds it after successful encoding.</summary>
    private static MemoryStream TransformToStreamCore(ReadOnlySpan<byte> input, PipelineStep[] steps, ImageEncoderOptions encoder, InputLimits limits)
    {
        MemoryStream output = new(Math.Clamp(input.Length, 256, 16 * 1024 * 1024));
        try
        {
            TransformCore(input, output, steps, encoder, limits, collectTimings: false, out _);
            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>Snapshots a public ordered pipeline and all mutable options.</summary>
    private static void SnapshotPipeline(
        IReadOnlyList<IImageTransformation> transformations,
        ImageEncoderOptions encoder,
        ImageReadOptions? readOptions,
        out PipelineStep[] steps,
        out ImageEncoderOptions encoderSnapshot,
        out InputLimits limits)
    {
        steps = Compile(transformations);
        encoderSnapshot = CloneEncoder(encoder);
        limits = SnapshotLimits(readOptions);
    }

    /// <summary>Translates the original convenience options to the ordered pipeline.</summary>
    private static void SnapshotLegacy(
        TransformOptions options,
        out PipelineStep[] steps,
        out ImageEncoderOptions encoder,
        out InputLimits limits)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Encoder is null)
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "Encoder and positive input limits are required.");
        }

        if (!Enum.IsDefined(options.Rotation))
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "The rotation value is invalid.");
        }

        if (options.Resize is not null)
        {
            ResizeOptions resize = CloneResize(options.Resize);
            ValidateResize(resize, 0);
            steps = [new PipelineStep(options.Rotation, resize)];
        }
        else if (options.Rotation != ImageRotation.None)
        {
            steps = [new PipelineStep(options.Rotation, null)];
        }
        else
        {
            steps = Array.Empty<PipelineStep>();
        }

        encoder = CloneEncoder(options.Encoder);
        limits = new InputLimits(options.MaxInputPixels, options.MaxInputBytes);
        ValidateLimits(limits);
    }

    /// <summary>Compiles ordered operations into optimizable rotation/resize phases.</summary>
    private static PipelineStep[] Compile(IReadOnlyList<IImageTransformation> transformations)
    {
        ArgumentNullException.ThrowIfNull(transformations);
        List<PipelineStep> steps = new(transformations.Count);
        ImageRotation pendingRotation = ImageRotation.None;
        for (int i = 0; i < transformations.Count; i++)
        {
            IImageTransformation? transformation = transformations[i];
            switch (transformation)
            {
                case RotateTransformation rotate:
                    if (!Enum.IsDefined(rotate.Rotation))
                    {
                        throw new Image2DException(ImageErrorCode.InvalidOptions, $"Transformation {i} has an invalid rotation.");
                    }

                    pendingRotation = (ImageRotation)(((int)pendingRotation + (int)rotate.Rotation) & 3);
                    break;

                case ResizeTransformation resize:
                    ResizeOptions resizeSnapshot = CloneResize(resize.Options);
                    ValidateResize(resizeSnapshot, i);
                    steps.Add(new PipelineStep(pendingRotation, resizeSnapshot));
                    pendingRotation = ImageRotation.None;
                    break;

                case null:
                    throw new Image2DException(ImageErrorCode.InvalidOptions, $"Transformation {i} is null.");

                default:
                    throw new Image2DException(ImageErrorCode.InvalidOptions, $"Transformation {i} is not a supported built-in transformation.");
            }
        }

        if (pendingRotation != ImageRotation.None)
        {
            steps.Add(new PipelineStep(pendingRotation, null));
        }

        return steps.ToArray();
    }

    /// <summary>Copies mutable resize settings before executing user code.</summary>
    private static ResizeOptions CloneResize(ResizeOptions options) => new()
    {
        Width = options.Width,
        Height = options.Height,
        Mode = options.Mode,
        Filter = options.Filter,
        AllowUpscale = options.AllowUpscale,
    };

    /// <summary>Copies and validates mutable encoder settings.</summary>
    private static ImageEncoderOptions CloneEncoder(ImageEncoderOptions encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        return encoder switch
        {
            PngEncoderOptions png when (uint)png.CompressionLevel <= 9 => new PngEncoderOptions { CompressionLevel = png.CompressionLevel },
            PngEncoderOptions => throw new Image2DException(ImageErrorCode.InvalidOptions, "PNG compression level must be between 0 and 9."),
            JpegEncoderOptions jpeg when jpeg.Quality is >= 1 and <= 100 && Enum.IsDefined(jpeg.ChromaSubsampling) => new JpegEncoderOptions
            {
                Quality = jpeg.Quality,
                ChromaSubsampling = jpeg.ChromaSubsampling,
                BackgroundRed = jpeg.BackgroundRed,
                BackgroundGreen = jpeg.BackgroundGreen,
                BackgroundBlue = jpeg.BackgroundBlue,
            },
            JpegEncoderOptions => throw new Image2DException(ImageErrorCode.InvalidOptions, "JPEG quality or chroma subsampling is invalid."),
            _ => throw new Image2DException(ImageErrorCode.InvalidOptions, "The output encoder is not supported."),
        };
    }

    /// <summary>Copies and validates read limits.</summary>
    private static InputLimits SnapshotLimits(ImageReadOptions? options)
    {
        InputLimits limits = options is null
            ? new InputLimits(100_000_000, 512L * 1024 * 1024)
            : new InputLimits(options.MaxInputPixels, options.MaxInputBytes);
        ValidateLimits(limits);
        return limits;
    }

    private static void ValidateResize(ResizeOptions options, int index)
    {
        if (options.Width <= 0 || options.Height <= 0 || !Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Filter))
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, $"Transformation {index} has invalid resize dimensions, mode, or filter.");
        }
    }

    private static void ValidateLimits(InputLimits limits)
    {
        if (limits.MaxInputPixels <= 0 || limits.MaxInputBytes <= 0)
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "Positive input limits are required.");
        }
    }

    private static void ValidateInput(InputLimits limits, int inputLength)
    {
        ValidateLimits(limits);
        if (inputLength > limits.MaxInputBytes)
        {
            throw new Image2DException(ImageErrorCode.InputTooLarge, $"The encoded input exceeds {limits.MaxInputBytes} bytes.");
        }
    }

    private readonly record struct PipelineStep(ImageRotation Rotation, ResizeOptions? Resize);
    private readonly record struct InputLimits(long MaxInputPixels, long MaxInputBytes);
}
