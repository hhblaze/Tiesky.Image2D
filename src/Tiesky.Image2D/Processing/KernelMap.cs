namespace Tiesky.Image2D.Processing;

/// <summary>Stores normalized source weights for one destination axis.</summary>
internal sealed class KernelMap
{
    /// <summary>Builds compact per-destination coefficient arrays.</summary>
    private KernelMap(int[] offsets, int[] indices, float[] weights)
    {
        Offsets = offsets;
        Indices = indices;
        Weights = weights;
    }

    /// <summary>Gets coefficient offsets; item n+1 terminates destination n.</summary>
    public int[] Offsets { get; }

    /// <summary>Gets clamped source indices.</summary>
    public int[] Indices { get; }

    /// <summary>Gets normalized weights parallel to <see cref="Indices"/>.</summary>
    public float[] Weights { get; }

    /// <summary>Creates an anti-aliased reconstruction map for an arbitrary source window.</summary>
    public static KernelMap Create(int sourceLength, int destinationLength, double sourceStart, double sourceExtent, ResizeFilter filter)
    {
        double scale = destinationLength / sourceExtent;
        double radius = filter switch
        {
            ResizeFilter.Bilinear => 1,
            ResizeFilter.Bicubic => 2,
            _ => 3,
        };
        double filterScale = Math.Min(1, scale);
        double support = radius / filterScale;
        List<int> indices = new(checked(destinationLength * Math.Max(2, (int)Math.Ceiling(support * 2))));
        List<float> weights = new(indices.Capacity);
        int[] offsets = new int[destinationLength + 1];

        for (int destination = 0; destination < destinationLength; destination++)
        {
            offsets[destination] = indices.Count;
            double center = sourceStart + ((destination + 0.5) * sourceExtent / destinationLength) - 0.5;
            int first = (int)Math.Ceiling(center - support);
            int last = (int)Math.Floor(center + support);
            double total = 0;
            int weightStart = weights.Count;
            for (int source = first; source <= last; source++)
            {
                double distance = (source - center) * filterScale;
                double weight = Evaluate(filter, distance);
                if (weight == 0)
                {
                    continue;
                }

                indices.Add(Math.Clamp(source, 0, sourceLength - 1));
                weights.Add((float)weight);
                total += weight;
            }

            if (total == 0)
            {
                indices.Add(Math.Clamp((int)Math.Round(center), 0, sourceLength - 1));
                weights.Add(1);
            }
            else
            {
                float inverse = (float)(1 / total);
                for (int i = weightStart; i < weights.Count; i++)
                {
                    weights[i] *= inverse;
                }
            }
        }

        offsets[destinationLength] = indices.Count;
        return new KernelMap(offsets, indices.ToArray(), weights.ToArray());
    }

    /// <summary>Evaluates one reconstruction kernel.</summary>
    private static double Evaluate(ResizeFilter filter, double x)
    {
        x = Math.Abs(x);
        return filter switch
        {
            ResizeFilter.Bilinear => x < 1 ? 1 - x : 0,
            ResizeFilter.Bicubic => Bicubic(x),
            _ => x < 3 ? Sinc(x) * Sinc(x / 3) : 0,
        };
    }

    /// <summary>Evaluates the Catmull-Rom cubic kernel.</summary>
    private static double Bicubic(double x)
    {
        const double A = -0.5;
        if (x < 1)
        {
            return ((A + 2) * x - (A + 3)) * x * x + 1;
        }

        return x < 2 ? ((A * x - 5 * A) * x + 8 * A) * x - 4 * A : 0;
    }

    /// <summary>Evaluates normalized sinc with a stable zero limit.</summary>
    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12)
        {
            return 1;
        }

        double value = Math.PI * x;
        return Math.Sin(value) / value;
    }
}
