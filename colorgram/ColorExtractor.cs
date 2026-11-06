using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using Colorgram.Internal;

namespace Colorgram;

/// <summary>Extracts dominant colors from RGB pixels using a quantized histogram and weighted Lab k-means.</summary>
public static class ColorExtractor
{
    private const int MaxMergeDistanceCacheBytes = 8 * 1024 * 1024;
    /// <summary>Extracts up to six dominant colors using the default options.</summary>
    public static ExtractedColor[] Extract(ReadOnlySpan<Rgb24> pixels)
        => Extract(pixels, ColorExtractionOptions.Default);

    /// <summary>Extracts dominant colors from tightly packed RGB pixels.</summary>
    public static ExtractedColor[] Extract(ReadOnlySpan<Rgb24> pixels, ColorExtractionOptions options)
    {
        ExtractCore(pixels, default, options, allocateResult: true, out ExtractedColor[] result);
        return result;
    }

    /// <summary>Extracts dominant colors from complete, tightly packed RGB triples.</summary>
    public static ExtractedColor[] Extract(ReadOnlySpan<byte> rgb)
        => Extract(rgb, ColorExtractionOptions.Default);

    /// <summary>Extracts dominant colors from complete, tightly packed RGB triples.</summary>
    public static ExtractedColor[] Extract(ReadOnlySpan<byte> rgb, ColorExtractionOptions options)
    {
        Validate(options);
        return Extract(AsPixels(rgb), options);
    }

    /// <summary>
    /// Writes dominant colors into a caller-provided buffer and returns the number written.
    /// The destination must be large enough for the actual result, which may contain fewer than ColorCount colors.
    /// </summary>
    public static int Extract(ReadOnlySpan<Rgb24> pixels, Span<ExtractedColor> destination, ColorExtractionOptions options)
        => ExtractCore(pixels, destination, options, allocateResult: false, out _);

    /// <summary>
    /// Writes dominant colors from complete RGB triples into a caller-provided buffer and returns the number written.
    /// The destination must be large enough for the actual result.
    /// </summary>
    public static int Extract(ReadOnlySpan<byte> rgb, Span<ExtractedColor> destination, ColorExtractionOptions options)
    {
        Validate(options);
        return Extract(AsPixels(rgb), destination, options);
    }

    private static ReadOnlySpan<Rgb24> AsPixels(ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length % 3 != 0)
        {
            throw new ArgumentException("RGB data must contain complete three-byte pixels.", nameof(rgb));
        }

        return MemoryMarshal.Cast<byte, Rgb24>(rgb);
    }

    private static int ExtractCore(
        ReadOnlySpan<Rgb24> pixels, Span<ExtractedColor> destination, in ColorExtractionOptions options,
        bool allocateResult, out ExtractedColor[] result)
    {
        Validate(options);
        result = Array.Empty<ExtractedColor>();
        ColorBucket[] buckets = ColorHistogram.Build(
            pixels, options.IgnoreWhite, options.IgnoreBlack, pooled: true, out int bucketCount, out long totalWeight);
        if (bucketCount == 0)
        {
            return 0;
        }

        try
        {
            int capacity = Math.Min(options.ColorCount, bucketCount);
            ColorCluster[]? rentedClusters = null;
            Span<ColorCluster> clusters = capacity <= 64
                ? stackalloc ColorCluster[capacity]
                : (rentedClusters = ArrayPool<ColorCluster>.Shared.Rent(capacity)).AsSpan(0, capacity);
            try
            {
                int count;
                if (bucketCount <= options.ColorCount)
                {
                    count = bucketCount;
                    for (int i = 0; i < count; i++)
                    {
                        Rgb24 rgb = buckets[i].Rgb;
                        clusters[i] = new ColorCluster(LabColor.FromRgb24(rgb), rgb, buckets[i].Count);
                    }
                }
                else
                {
                    ColorSample[] samples = ArrayPool<ColorSample>.Shared.Rent(bucketCount);
                    try
                    {
                        for (int i = 0; i < bucketCount; i++)
                        {
                            Rgb24 rgb = buckets[i].Rgb;
                            samples[i] = new ColorSample(LabColor.FromRgb24(rgb), rgb, buckets[i].Count);
                        }

                        count = WeightedKMeans.Run(samples.AsSpan(0, bucketCount), capacity,
                            options.Seed, options.MaxIterations, clusters);
                    }
                    finally
                    {
                        ArrayPool<ColorSample>.Shared.Return(samples);
                    }
                }

                clusters = clusters[..count];
                if (options.MergeThreshold > 0f)
                {
                    clusters = clusters[..MergeClusters(clusters, options.MergeThreshold)];
                }

                clusters.Sort(CompareClusters);
                count = clusters.Length;
                if (allocateResult)
                {
                    result = new ExtractedColor[count];
                    destination = result;
                }
                else if (destination.Length < count)
                {
                    throw new ArgumentException(
                        $"Destination must hold at least {count} colors, but has length {destination.Length}.",
                        nameof(destination));
                }

                for (int i = 0; i < count; i++)
                {
                    destination[i] = new ExtractedColor(clusters[i].Rgb, clusters[i].Weight / totalWeight);
                }

                return count;
            }
            finally
            {
                if (rentedClusters is not null)
                {
                    ArrayPool<ColorCluster>.Shared.Return(rentedClusters);
                }
            }
        }
        finally
        {
            ArrayPool<ColorBucket>.Shared.Return(buckets);
        }
    }

    internal static int MergeClusters(Span<ColorCluster> clusters, float threshold)
    {
        int clusterCount = clusters.Length;
        long pairCount = (long)clusterCount * (clusterCount - 1) / 2;
        long cacheBytes = pairCount * sizeof(float);

        // Bound both the pool request and temporary active flags. The fallback keeps behavior
        // unchanged for unusually large requested palettes without risking a quadratic allocation.
        if (cacheBytes > MaxMergeDistanceCacheBytes)
        {
            return MergeClustersWithoutCache(clusters, threshold);
        }

        float[] distances;
        bool[] active;
        try
        {
            distances = ArrayPool<float>.Shared.Rent((int)pairCount);
            try
            {
                active = ArrayPool<bool>.Shared.Rent(clusterCount);
            }
            catch (OutOfMemoryException)
            {
                ArrayPool<float>.Shared.Return(distances);
                return MergeClustersWithoutCache(clusters, threshold);
            }
        }
        catch (OutOfMemoryException)
        {
            return MergeClustersWithoutCache(clusters, threshold);
        }

        try
        {
            Span<bool> activeClusters = active.AsSpan(0, clusterCount);
            activeClusters.Fill(true);

            Span<float> distanceCache = distances.AsSpan(0, (int)pairCount);
            for (int i = 0; i < clusterCount; i++)
            {
                for (int j = i + 1; j < clusterCount; j++)
                {
                    distanceCache[GetPairIndex(i, j, clusterCount)] = clusters[i].Lab.DeltaE2000(clusters[j].Lab);
                }
            }

            int activeCount = clusterCount;
            while (activeCount > 1)
            {
                // Keep the hot minimum search as a contiguous span reduction. Invalid pairs
                // are set to +Infinity on merge, so this loop has no active-flag branches.
                ReadOnlySpan<float> currentDistances = distanceCache;
                float bestDistance = FindMinimum(currentDistances);
                if (bestDistance >= threshold)
                {
                    break;
                }

                // IndexOf returns the first equal entry, preserving the original nested-loop
                // tie-breaking order (i ascending, then j ascending).
                int bestPair = currentDistances.IndexOf(bestDistance);
                GetPair(bestPair, clusterCount, out int bestFirst, out int bestSecond);
                if (!activeClusters[bestFirst] || !activeClusters[bestSecond])
                {
                    throw new InvalidOperationException("The merge distance cache contains an inactive pair.");
                }

                ColorCluster first = clusters[bestFirst];
                ColorCluster second = clusters[bestSecond];
                double weight = first.Weight + second.Weight;
                clusters[bestFirst] = Merge(first, second, weight);
                activeClusters[bestSecond] = false;
                activeCount--;

                // Invalidate all pairs involving the removed cluster. This touches only O(k)
                // cache entries and keeps the next O(k²) minimum scan branch-free and contiguous.
                for (int i = 0; i < clusterCount; i++)
                {
                    if (i != bestSecond)
                    {
                        int low = Math.Min(i, bestSecond);
                        int high = Math.Max(i, bestSecond);
                        distanceCache[GetPairIndex(low, high, clusterCount)] = float.PositiveInfinity;
                    }
                }

                for (int i = 0; i < clusterCount; i++)
                {
                    if (!activeClusters[i] || i == bestFirst)
                    {
                        continue;
                    }

                    int low = Math.Min(i, bestFirst);
                    int high = Math.Max(i, bestFirst);
                    distanceCache[GetPairIndex(low, high, clusterCount)] =
                        clusters[low].Lab.DeltaE2000(clusters[high].Lab);
                }
            }

            int write = 0;
            for (int i = 0; i < clusterCount; i++)
            {
                if (activeClusters[i])
                {
                    clusters[write++] = clusters[i];
                }
            }

            return write;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(distances);
            ArrayPool<bool>.Shared.Return(active);
        }
    }

    internal static int MergeClustersWithoutCache(Span<ColorCluster> clusters, float threshold)
    {
        while (clusters.Length > 1)
        {
            int bestFirst = -1;
            int bestSecond = -1;
            float bestDistance = threshold;
            for (int i = 0; i < clusters.Length; i++)
            {
                for (int j = i + 1; j < clusters.Length; j++)
                {
                    float distance = clusters[i].Lab.DeltaE2000(clusters[j].Lab);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestFirst = i;
                        bestSecond = j;
                    }
                }
            }

            if (bestFirst < 0)
            {
                break;
            }

            ColorCluster first = clusters[bestFirst];
            ColorCluster second = clusters[bestSecond];
            double weight = first.Weight + second.Weight;
            clusters[bestFirst] = Merge(first, second, weight);
            clusters[(bestSecond + 1)..].CopyTo(clusters[bestSecond..]);
            clusters = clusters[..^1];
        }

        return clusters.Length;
    }

    private static ColorCluster Merge(ColorCluster first, ColorCluster second, double weight)
        => new(
            new LabColor(
                (float)((first.Lab.L * first.Weight + second.Lab.L * second.Weight) / weight),
                (float)((first.Lab.A * first.Weight + second.Lab.A * second.Weight) / weight),
                (float)((first.Lab.B * first.Weight + second.Lab.B * second.Weight) / weight)),
            new Rgb24(
                ColorMath.ToByte((first.Rgb.R * first.Weight + second.Rgb.R * second.Weight) / weight),
                ColorMath.ToByte((first.Rgb.G * first.Weight + second.Rgb.G * second.Weight) / weight),
                ColorMath.ToByte((first.Rgb.B * first.Weight + second.Rgb.B * second.Weight) / weight)),
            weight);

    private static int GetPairIndex(int first, int second, int clusterCount)
    {
        // The callers provide first < second. Rows are concatenated without diagonal entries.
        return (int)((long)first * (2L * clusterCount - first - 1) / 2 + second - first - 1);
    }

    private static void GetPair(int pairIndex, int clusterCount, out int first, out int second)
    {
        // Invert the row-major triangular index. The small palette makes the integer binary
        // search negligible; the expensive all-pairs reduction remains a SIMD-friendly scan.
        int low = 0;
        int high = clusterCount - 1;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            long rowStart = (long)middle * (2L * clusterCount - middle - 1) / 2;
            long nextRowStart = (long)(middle + 1) * (2L * clusterCount - middle - 2) / 2;
            if (pairIndex < rowStart)
            {
                high = middle;
            }
            else if (pairIndex >= nextRowStart)
            {
                low = middle + 1;
            }
            else
            {
                low = high = middle;
            }
        }

        first = low;
        long start = (long)first * (2L * clusterCount - first - 1) / 2;
        second = first + 1 + (int)(pairIndex - start);
    }

    private static float FindMinimum(ReadOnlySpan<float> values)
    {
        int vectorWidth = Vector<float>.Count;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= vectorWidth)
        {
            var minimum = new Vector<float>(float.PositiveInfinity);
            for (; i <= values.Length - vectorWidth; i += vectorWidth)
            {
                minimum = Vector.Min(minimum, new Vector<float>(values.Slice(i, vectorWidth)));
            }

            Span<float> lanes = stackalloc float[Vector<float>.Count];
            minimum.CopyTo(lanes);
            float result = lanes[0];
            for (int lane = 1; lane < vectorWidth; lane++)
            {
                if (lanes[lane] < result)
                {
                    result = lanes[lane];
                }
            }

            for (; i < values.Length; i++)
            {
                if (values[i] < result)
                {
                    result = values[i];
                }
            }

            return result;
        }

        float scalarMinimum = float.PositiveInfinity;
        for (; i < values.Length; i++)
        {
            if (values[i] < scalarMinimum)
            {
                scalarMinimum = values[i];
            }
        }

        return scalarMinimum;
    }

    private static int CompareClusters(ColorCluster left, ColorCluster right)
    {
        int result = right.Weight.CompareTo(left.Weight);
        return result != 0 ? result : left.Rgb.CompareTo(right.Rgb);
    }

    private static void Validate(in ColorExtractionOptions options)
    {
        if (options.ColorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ColorCount), options.ColorCount,
                "At least one color must be requested.");
        }

        if (options.MaxIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxIterations), options.MaxIterations,
                "Max iterations must be positive.");
        }

        if (!(options.MergeThreshold >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(options.MergeThreshold), options.MergeThreshold,
                "Merge threshold cannot be negative or NaN.");
        }
    }
}
