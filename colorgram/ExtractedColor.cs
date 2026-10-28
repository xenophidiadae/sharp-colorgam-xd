using System.Globalization;

namespace Colorgram;

/// <summary>
/// A color returned by <see cref="ColorExtractor"/> together with its share of the analyzed pixels.
/// </summary>
public readonly struct ExtractedColor : IEquatable<ExtractedColor>
{
    public ExtractedColor(Rgb24 rgb, double proportion)
    {
        Rgb = rgb;
        Proportion = proportion;
    }

    public Rgb24 Rgb { get; }

    /// <summary>Fraction of the analyzed pixels represented by this color, in the 0..1 range.</summary>
    public double Proportion { get; }

    /// <summary>The same color in CIE L*a*b* (D65). Computed on demand.</summary>
    public LabColor Lab => LabColor.FromRgb24(Rgb);

    public bool Equals(ExtractedColor other)
        => Rgb.Equals(other.Rgb) && Proportion.Equals(other.Proportion);

    public override bool Equals(object? obj) => obj is ExtractedColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Rgb, Proportion);

    public static bool operator ==(ExtractedColor left, ExtractedColor right) => left.Equals(right);

    public static bool operator !=(ExtractedColor left, ExtractedColor right) => !left.Equals(right);

    public override string ToString()
        => $"{Rgb} {Proportion.ToString("P2", CultureInfo.InvariantCulture)}";
}
