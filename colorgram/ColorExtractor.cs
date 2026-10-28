using Colorgram.Internal;

namespace Colorgram;

/// <summary>
/// Extracts dominant colors from a pixel buffer.
/// </summary>
/// <remarks>
/// The pipeline is:
/// <list type="number">
/// <item>optionally drop near-white and near-black pixels;</item>
/// <item>quantize every pixel from 8 to 5 bits per channel and build a 32768-bucket histogram;</item>
/// <item>convert the non-empty buckets to CIE L*a*b* and run weighted k-means++ clustering in that space;</item>
/// <item>optionally merge perceptually close clusters using Delta E 2000;</item>
/// <item>sort by weight and return the requested number of colors.</item>
/// </list>
/// All pixel input is accepted through <see cref="ReadOnlySpan{T}"/> and no image decoder is required.
/// </remarks>
public static class ColorExtractor
{
    /// <summary>Extracts up to six dominant colors using the default options.</summary>
    public static ExtractedColor[] Extract(ReadOnlySpan<Rgb24> pixels)
        => Extract(pixels, ColorExtractionOptions.Default);

    /// <summary>Extracts dominant colors from a span of tightly packed RGB pixels.</summary>
    public static ExtractedColor[] Extract(ReadOnlySpan<Rgb24> pixels, ColorExtractionOptions options)
    {
        Validate(options);

        List<ColorCluster>? clusters = BuildClusters(
            ColorHistogram.Create(pixels, options.IgnoreWhite, options.IgnoreBlack),
            in options,
            out double totalWeight);

        if (clusters is null)
        {
            return Array.Empty<ExtractedColor>();
        }

        var result = new ExtractedColor[Math.Min(options.ColorCount, clusters.Count)];
        WriteResult(clusters, totalWeight, options.ColorCount, result);
        return result;
    }

    /// <summary>
    /// Extracts dominant colors from tightly packed RGB triples (three bytes per pixel).
    /// </summary>
    public static ExtractedColor[] Extract(ReadOnlySpan<byte> rgb)
        => Extract(rgb, ColorExtractionOptions.Default);

    /// <summary>
    /// Extracts dominant colors from tightly packed RGB triples (three bytes per pixel).
    /// The span length must be a multiple of three.
    /// </summary>
    public static ExtractedColor[] Extract(ReadOnlySpan<byte> rgb, ColorExtractionOptions options)
    {
        Validate(options);

        List<ColorCluster>? clusters = BuildClusters(
            ColorHistogram.Create(rgb, options.IgnoreWhite, options.IgnoreBlack),
            in options,
            out double totalWeight);

        if (clusters is null)
        {
            return Array.Empty<ExtractedColor>();
        }

        var result = new ExtractedColor[Math.Min(options.ColorCount, clusters.Count)];
        WriteResult(clusters, totalWeight, options.ColorCount, result);
        return result;
    }

    private static List<ColorCluster>? BuildClusters(
        ColorHistogram histogram,
        in ColorExtractionOptions options,
        out double totalWeight)
    {
        ReadOnlySpan<ColorBucket> buckets = histogram.Buckets;
        totalWeight = histogram.TotalCount;

        if (buckets.Length == 0)
        {
            return null;
        }

        List<ColorCluster> clusters;

        if (buckets.Length <= options.ColorCount)
        {
            clusters = new List<ColorCluster>(buckets.Length);
            foreach (ColorBucket bucket in buckets)
            {
                Rgb24 rgb = bucket.Rgb;
                clusters.Add(new ColorCluster(LabColor.FromRgb24(rgb), rgb, bucket.Count));
            }
        }
        else
        {
            var samples = new ColorSample[buckets.Length];
            for (int i = 0; i < buckets.Length; i++)
            {
                Rgb24 rgb = buckets[i].Rgb;
                samples[i] = new ColorSample(LabColor.FromRgb24(rgb), rgb, buckets[i].Count);
            }

            ColorCluster[] computed = WeightedKMeans.Run(
                samples,
                options.ColorCount,
                options.Seed,
                options.MaxIterations);

            clusters = new List<ColorCluster>(computed);
        }

        if (options.MergeThreshold > 0f)
        {
            MergeClusters(clusters, options.MergeThreshold);
        }

        clusters.Sort(CompareClusters);
        return clusters;
    }

    private static int WriteResult(
        List<ColorCluster> clusters,
        double totalWeight,
        int maxColors,
        Span<ExtractedColor> destination)
    {
        int count = Math.Min(maxColors, clusters.Count);

        if (destination.Length < count)
        {
            throw new ArgumentException(
                $"Destination must hold at least {count} colors, but has length {destination.Length}.",
                nameof(destination));
        }

        for (int i = 0; i < count; i++)
        {
            ColorCluster cluster = clusters[i];
            destination[i] = new ExtractedColor(cluster.Rgb, cluster.Weight / totalWeight);
        }

        return count;
    }

    /// <summary>
    /// Extracts dominant colors into a caller-provided buffer.
    /// Returns the number of colors written. The destination must be large enough for the result.
    /// </summary>
    public static int Extract(
        ReadOnlySpan<Rgb24> pixels,
        Span<ExtractedColor> destination,
        ColorExtractionOptions options)
    {
        Validate(options);

        List<ColorCluster>? clusters = BuildClusters(
            ColorHistogram.Create(pixels, options.IgnoreWhite, options.IgnoreBlack),
            in options,
            out double totalWeight);

        return clusters is null
            ? 0
            : WriteResult(clusters, totalWeight, options.ColorCount, destination);
    }

    /// <summary>
    /// Extracts dominant colors from tightly packed RGB triples into a caller-provided buffer.
    /// Returns the number of colors written. The destination must be large enough for the result.
    /// </summary>
    public static int Extract(
        ReadOnlySpan<byte> rgb,
        Span<ExtractedColor> destination,
        ColorExtractionOptions options)
    {
        Validate(options);

        List<ColorCluster>? clusters = BuildClusters(
            ColorHistogram.Create(rgb, options.IgnoreWhite, options.IgnoreBlack),
            in options,
            out double totalWeight);

        return clusters is null
            ? 0
            : WriteResult(clusters, totalWeight, options.ColorCount, destination);
    }

    private static void MergeClusters(List<ColorCluster> clusters, float threshold)
    {
        while (clusters.Count > 1)
        {
            int bestFirst = -1;
            int bestSecond = -1;
            float bestDistance = threshold;

            for (int i = 0; i < clusters.Count; i++)
            {
                for (int j = i + 1; j < clusters.Count; j++)
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
                return;
            }

            ColorCluster first = clusters[bestFirst];
            ColorCluster second = clusters[bestSecond];
            double weight = first.Weight + second.Weight;

            var merged = new ColorCluster(
                new LabColor(
                    (float)((first.Lab.L * first.Weight + second.Lab.L * second.Weight) / weight),
                    (float)((first.Lab.A * first.Weight + second.Lab.A * second.Weight) / weight),
                    (float)((first.Lab.B * first.Weight + second.Lab.B * second.Weight) / weight)),
                new Rgb24(
                    ColorMath.ToByte((first.Rgb.R * first.Weight + second.Rgb.R * second.Weight) / weight),
                    ColorMath.ToByte((first.Rgb.G * first.Weight + second.Rgb.G * second.Weight) / weight),
                    ColorMath.ToByte((first.Rgb.B * first.Weight + second.Rgb.B * second.Weight) / weight)),
                weight);

            clusters[bestFirst] = merged;
            clusters.RemoveAt(bestSecond);
        }
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
            throw new ArgumentOutOfRangeException(
                nameof(options.ColorCount),
                options.ColorCount,
                "At least one color must be requested.");
        }

        if (options.MaxIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxIterations),
                options.MaxIterations,
                "Max iterations must be positive.");
        }

        if (!(options.MergeThreshold >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MergeThreshold),
                options.MergeThreshold,
                "Merge threshold cannot be negative or NaN.");
        }
    }
}
