using Tiesky.Image2D.Internal;

namespace Tiesky.Image2D.Processing;

/// <summary>Contains validated output dimensions and the centered source window.</summary>
internal readonly struct ResizePlan
{
    /// <summary>Initializes a computed resize plan.</summary>
    private ResizePlan(int width, int height, double sourceX, double sourceY, double sourceWidth, double sourceHeight, ResizeFilter filter)
    {
        Width = width;
        Height = height;
        SourceX = sourceX;
        SourceY = sourceY;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Filter = filter;
    }

    /// <summary>Gets the output width.</summary>
    public int Width { get; }

    /// <summary>Gets the output height.</summary>
    public int Height { get; }

    /// <summary>Gets the left edge of the sampled source window.</summary>
    public double SourceX { get; }

    /// <summary>Gets the top edge of the sampled source window.</summary>
    public double SourceY { get; }

    /// <summary>Gets the sampled source width.</summary>
    public double SourceWidth { get; }

    /// <summary>Gets the sampled source height.</summary>
    public double SourceHeight { get; }

    /// <summary>Gets the reconstruction filter.</summary>
    public ResizeFilter Filter { get; }

    /// <summary>Validates user options and computes a resize/crop mapping.</summary>
    public static ResizePlan Create(int sourceWidth, int sourceHeight, ResizeOptions? options)
    {
        if (options is null)
        {
            return new ResizePlan(sourceWidth, sourceHeight, 0, 0, sourceWidth, sourceHeight, ResizeFilter.Bilinear);
        }

        if (options.Width <= 0 || options.Height <= 0 || !Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Filter))
        {
            throw new Image2DException(ImageErrorCode.InvalidOptions, "Resize dimensions, mode, or filter are invalid.");
        }

        if (options.Mode == ResizeMode.Stretch)
        {
            int width = options.AllowUpscale ? options.Width : Math.Min(options.Width, sourceWidth);
            int height = options.AllowUpscale ? options.Height : Math.Min(options.Height, sourceHeight);
            return new ResizePlan(width, height, 0, 0, sourceWidth, sourceHeight, options.Filter);
        }

        double requestedScaleX = (double)options.Width / sourceWidth;
        double requestedScaleY = (double)options.Height / sourceHeight;
        if (options.Mode == ResizeMode.Contain)
        {
            double scale = Math.Min(requestedScaleX, requestedScaleY);
            if (!options.AllowUpscale)
            {
                scale = Math.Min(scale, 1);
            }

            int width = Math.Max(1, (int)Math.Round(sourceWidth * scale, MidpointRounding.AwayFromZero));
            int height = Math.Max(1, (int)Math.Round(sourceHeight * scale, MidpointRounding.AwayFromZero));
            return new ResizePlan(width, height, 0, 0, sourceWidth, sourceHeight, options.Filter);
        }

        int coverWidth = options.AllowUpscale ? options.Width : Math.Min(options.Width, sourceWidth);
        int coverHeight = options.AllowUpscale ? options.Height : Math.Min(options.Height, sourceHeight);
        double coverScale = Math.Max((double)coverWidth / sourceWidth, (double)coverHeight / sourceHeight);
        double sampledWidth = coverWidth / coverScale;
        double sampledHeight = coverHeight / coverScale;
        return new ResizePlan(
            coverWidth,
            coverHeight,
            (sourceWidth - sampledWidth) * 0.5,
            (sourceHeight - sampledHeight) * 0.5,
            sampledWidth,
            sampledHeight,
            options.Filter);
    }
}
