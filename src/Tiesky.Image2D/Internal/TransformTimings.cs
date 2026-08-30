using System.Diagnostics;

namespace Tiesky.Image2D.Internal;

/// <summary>Captures benchmark-only stage durations without changing the public API.</summary>
internal readonly record struct TransformTimings(long DecodeTicks, long TransformTicks, long EncodeTicks)
{
    public double DecodeMilliseconds => ToMilliseconds(DecodeTicks);
    public double TransformMilliseconds => ToMilliseconds(TransformTicks);
    public double EncodeMilliseconds => ToMilliseconds(EncodeTicks);

    private static double ToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
}
