namespace Colorgram;

/// <summary>
/// A color quantized to 5 bits per channel. The three channels are packed into a single
/// 15-bit value (<c>RRRRRGGGGGBBBBB</c>), which makes it usable as a direct histogram index.
/// </summary>
public readonly struct Rgb5 : IEquatable<Rgb5>, IComparable<Rgb5>
{
    /// <summary>Number of bits kept for each channel.</summary>
    public const int BitsPerChannel = 5;

    /// <summary>Number of possible values per channel (32).</summary>
    public const int ChannelValues = 1 << BitsPerChannel;

    /// <summary>Number of histogram buckets (32 * 32 * 32 = 32768).</summary>
    public const int BucketCount = 1 << (BitsPerChannel * 3);

    private const int ChannelMask = ChannelValues - 1;

    private readonly ushort _packed;

    private Rgb5(ushort packed) => _packed = packed;

    /// <summary>The packed 15-bit value.</summary>
    public ushort Packed => _packed;

    /// <summary>Red channel in the 0..31 range.</summary>
    public byte R => (byte)((_packed >> (BitsPerChannel * 2)) & ChannelMask);

    /// <summary>Green channel in the 0..31 range.</summary>
    public byte G => (byte)((_packed >> BitsPerChannel) & ChannelMask);

    /// <summary>Blue channel in the 0..31 range.</summary>
    public byte B => (byte)(_packed & ChannelMask);

    /// <summary>Creates a quantized color from a packed 15-bit value.</summary>
    public static Rgb5 FromPacked(ushort packed)
    {
        if (packed >= BucketCount)
        {
            throw new ArgumentOutOfRangeException(nameof(packed), packed, "Packed value must fit into 15 bits.");
        }

        return new Rgb5(packed);
    }

    /// <summary>Drops the lowest three bits of every channel (8 bits to 5 bits).</summary>
    public static Rgb5 FromRgb24(Rgb24 color) => new((ushort)(
        ((color.R >> 3) << (BitsPerChannel * 2)) |
        ((color.G >> 3) << BitsPerChannel) |
        (color.B >> 3)));

    /// <summary>Expands every 5-bit channel back to 8 bits by replicating its high bits.</summary>
    public Rgb24 ToRgb24() => new(Expand(R), Expand(G), Expand(B));

    public bool Equals(Rgb5 other) => _packed == other._packed;

    public override bool Equals(object? obj) => obj is Rgb5 other && Equals(other);

    public override int GetHashCode() => _packed;

    public int CompareTo(Rgb5 other) => _packed.CompareTo(other._packed);

    public static bool operator ==(Rgb5 left, Rgb5 right) => left.Equals(right);

    public static bool operator !=(Rgb5 left, Rgb5 right) => !left.Equals(right);

    public override string ToString() => $"R{R},G{G},B{B}";

    private static byte Expand(byte value) => (byte)((value << 3) | (value >> 2));
}
