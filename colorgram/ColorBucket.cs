namespace Colorgram;

/// <summary>
/// One non-empty bucket of a quantized color histogram.
/// </summary>
public readonly struct ColorBucket : IEquatable<ColorBucket>, IComparable<ColorBucket>
{
    public ColorBucket(Rgb5 color, int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");
        }

        Color = color;
        Count = count;
    }

    /// <summary>The quantized bucket color.</summary>
    public Rgb5 Color { get; }

    /// <summary>Number of pixels that fell into this bucket.</summary>
    public int Count { get; }

    /// <summary>The bucket color expanded back to 8 bits per channel.</summary>
    public Rgb24 Rgb => Color.ToRgb24();

    public bool Equals(ColorBucket other) => Color.Equals(other.Color) && Count == other.Count;

    public override bool Equals(object? obj) => obj is ColorBucket other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Color, Count);

    public int CompareTo(ColorBucket other)
    {
        int result = Color.CompareTo(other.Color);
        return result != 0 ? result : Count.CompareTo(other.Count);
    }

    public static bool operator ==(ColorBucket left, ColorBucket right) => left.Equals(right);

    public static bool operator !=(ColorBucket left, ColorBucket right) => !left.Equals(right);

    public override string ToString() => $"{Rgb} x{Count}";
}
