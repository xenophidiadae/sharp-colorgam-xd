using System.Globalization;

namespace Colorgram;

/// <summary>
/// An immutable 24-bit sRGB color. This is the public color representation used by the library.
/// </summary>
public readonly struct Rgb24 : IEquatable<Rgb24>, IComparable<Rgb24>
{
    /// <summary>A channel value at or above this threshold is considered "near white".</summary>
    public const byte NearWhiteThreshold = 250;

    /// <summary>A channel value at or below this threshold is considered "near black".</summary>
    public const byte NearBlackThreshold = 15;

    public Rgb24(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }

    public static Rgb24 White => new(255, 255, 255);

    public static Rgb24 Black => new(0, 0, 0);

    /// <summary>Returns <see langword="true"/> when all channels are at least 250.</summary>
    public bool IsNearWhite()
        => R >= NearWhiteThreshold && G >= NearWhiteThreshold && B >= NearWhiteThreshold;

    /// <summary>Returns <see langword="true"/> when all channels are at most 15.</summary>
    public bool IsNearBlack()
        => R <= NearBlackThreshold && G <= NearBlackThreshold && B <= NearBlackThreshold;

    /// <summary>Writes the three sRGB bytes into <paramref name="destination"/>.</summary>
    public void WriteRgb(Span<byte> destination)
    {
        if (destination.Length < 3)
        {
            throw new ArgumentException("Destination must be at least 3 bytes long.", nameof(destination));
        }

        destination[0] = R;
        destination[1] = G;
        destination[2] = B;
    }

    /// <summary>Returns the color as an uppercase <c>#RRGGBB</c> string.</summary>
    public string ToHex() => string.Create(7, this, static (span, color) =>
    {
        span[0] = '#';
        WriteHexByte(span[1..3], color.R);
        WriteHexByte(span[3..5], color.G);
        WriteHexByte(span[5..7], color.B);
    });

    public void Deconstruct(out byte r, out byte g, out byte b)
    {
        r = R;
        g = G;
        b = B;
    }

    public bool Equals(Rgb24 other) => R == other.R && G == other.G && B == other.B;

    public override bool Equals(object? obj) => obj is Rgb24 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B);

    public int CompareTo(Rgb24 other)
    {
        int result = R.CompareTo(other.R);
        if (result != 0)
        {
            return result;
        }

        result = G.CompareTo(other.G);
        return result != 0 ? result : B.CompareTo(other.B);
    }

    public static bool operator ==(Rgb24 left, Rgb24 right) => left.Equals(right);

    public static bool operator !=(Rgb24 left, Rgb24 right) => !left.Equals(right);

    public override string ToString() => ToHex();

    private static void WriteHexByte(Span<char> destination, byte value)
    {
        const string digits = "0123456789ABCDEF";
        destination[0] = digits[value >> 4];
        destination[1] = digits[value & 0x0F];
    }
}
