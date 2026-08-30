namespace Tiesky.Image2D.Internal;

/// <summary>Centralizes internal adaptive-parallel policy and deterministic test forcing.</summary>
internal static class ParallelExecution
{
    /// <summary>Forces eligible operations to execute serially for equivalence verification.</summary>
    internal static bool ForceSequential { get; set; }

    /// <summary>Returns whether an eligible workload should use bounded parallel workers.</summary>
    internal static bool ShouldRun(long work, long threshold) =>
        !ForceSequential && Environment.ProcessorCount > 1 && work >= threshold;

    /// <summary>Runs an independent range with no more than the process CPU count.</summary>
    internal static void For(int fromInclusive, int toExclusive, bool parallel, Action<int> body, int maximumDegreeOfParallelism = int.MaxValue)
    {
        if (!parallel)
        {
            for (int index = fromInclusive; index < toExclusive; index++) body(index);
            return;
        }

        Parallel.For(
            fromInclusive,
            toExclusive,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, maximumDegreeOfParallelism) },
            body);
    }
}
