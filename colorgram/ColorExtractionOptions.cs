namespace Colorgram;

/// <summary>
/// Options for <see cref="ColorExtractor.Extract(System.ReadOnlySpan{Rgb24}, ColorExtractionOptions)"/>.
/// Use <c>new ColorExtractionOptions()</c> to get the defaults.
/// </summary>
public readonly record struct ColorExtractionOptions
{
    /// <summary>Creates the default options: six colors, 32 iterations, merging disabled.</summary>
    public ColorExtractionOptions()
    {
    }

    /// <summary>Default options: six colors, 32 iterations, no white/black filtering, no merging.</summary>
    public static ColorExtractionOptions Default => new();

    /// <summary>Maximum number of colors to return. Defaults to 6.</summary>
    public int ColorCount { get; init; } = 6;

    /// <summary>Skip pixels that are near white (all channels at least 250).</summary>
    public bool IgnoreWhite { get; init; }

    /// <summary>Skip pixels that are near black (all channels at most 15).</summary>
    public bool IgnoreBlack { get; init; }

    /// <summary>Seed for the deterministic k-means++ initialization.</summary>
    public int Seed { get; init; }

    /// <summary>Maximum number of Lloyd iterations. Defaults to 32.</summary>
    public int MaxIterations { get; init; } = 32;

    /// <summary>
    /// Merge clusters whose CIE Delta E 2000 distance is below this value.
    /// Zero (the default) disables merging.
    /// </summary>
    public float MergeThreshold { get; init; }
}
