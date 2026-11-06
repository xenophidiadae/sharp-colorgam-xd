using System.Buffers;
using System.Runtime.InteropServices;

namespace Colorgram;

/// <summary>
/// A sparse histogram over the 32768 quantized 5-bit RGB buckets.
/// Only non-empty buckets are kept in memory; the dense scratch buffer is rented from the shared pool.
/// </summary>
public readonly struct ColorHistogram
{
    private readonly ColorBucket[]? _buckets;

    private ColorHistogram(ColorBucket[] buckets, long totalCount)
    {
        _buckets = buckets;
        TotalCount = totalCount;
    }

    /// <summary>Number of pixels that were counted (after optional white/black filtering).</summary>
    public long TotalCount { get; }

    /// <summary>Number of non-empty buckets. Buckets are sorted by their packed color value.</summary>
    public int DistinctCount => _buckets?.Length ?? 0;

    /// <summary>All non-empty buckets, sorted by packed color value.</summary>
    public ReadOnlySpan<ColorBucket> Buckets
        => _buckets is null ? ReadOnlySpan<ColorBucket>.Empty : _buckets;

    /// <summary>
    /// Gets the pixel count for a quantized color. Returns zero when the bucket is empty.
    /// </summary>
    public int this[Rgb5 color]
    {
        get
        {
            ReadOnlySpan<ColorBucket> buckets = Buckets;
            int low = 0;
            int high = buckets.Length - 1;

            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                int comparison = buckets[middle].Color.CompareTo(color);
                if (comparison == 0)
                {
                    return buckets[middle].Count;
                }

                if (comparison < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return 0;
        }
    }

    /// <summary>Builds a histogram of all pixels.</summary>
    public static ColorHistogram Create(ReadOnlySpan<Rgb24> pixels)
        => Create(pixels, ignoreWhite: false, ignoreBlack: false);

    /// <summary>Builds a histogram, optionally skipping near-white and near-black pixels.</summary>
    public static ColorHistogram Create(ReadOnlySpan<Rgb24> pixels, bool ignoreWhite, bool ignoreBlack)
    {
        ColorBucket[] buckets = Build(pixels, ignoreWhite, ignoreBlack, pooled: false, out _, out long totalCount);
        return new ColorHistogram(buckets, totalCount);
    }

    // The caller owns the rented buckets and must return them after consuming the populated prefix.
    internal static ColorBucket[] Build(
        ReadOnlySpan<Rgb24> pixels, bool ignoreWhite, bool ignoreBlack, bool pooled,
        out int bucketCount, out long totalCount)
    {
        bucketCount = 0;
        totalCount = 0;
        if (pixels.IsEmpty)
        {
            return Array.Empty<ColorBucket>();
        }

        int[] counts = ArrayPool<int>.Shared.Rent(Rgb5.BucketCount);
        try
        {
            Array.Clear(counts, 0, Rgb5.BucketCount);

            long counted = 0;
            if (!ignoreWhite && !ignoreBlack)
            {
                foreach (Rgb24 pixel in pixels)
                {
                    counts[Rgb5.FromRgb24(pixel).Packed]++;
                }
                counted = pixels.Length;
            }
            else
            {
                foreach (Rgb24 pixel in pixels)
                {
                    if (ShouldIgnore(pixel, ignoreWhite, ignoreBlack))
                    {
                        continue;
                    }

                    counts[Rgb5.FromRgb24(pixel).Packed]++;
                    counted++;
                }
            }

            totalCount = counted;
            return Finish(counts, totalCount, pooled, out bucketCount);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    /// <summary>
    /// Builds a histogram from tightly packed RGB triples (three bytes per pixel).
    /// The span length must be a multiple of three.
    /// </summary>
    public static ColorHistogram Create(ReadOnlySpan<byte> rgb)
        => Create(rgb, ignoreWhite: false, ignoreBlack: false);

    /// <summary>
    /// Builds a histogram from tightly packed RGB triples (three bytes per pixel),
    /// optionally skipping near-white and near-black pixels.
    /// </summary>
    public static ColorHistogram Create(ReadOnlySpan<byte> rgb, bool ignoreWhite, bool ignoreBlack)
    {
        if (rgb.Length % 3 != 0)
        {
            throw new ArgumentException("RGB data must contain complete three-byte pixels.", nameof(rgb));
        }

        return Create(MemoryMarshal.Cast<byte, Rgb24>(rgb), ignoreWhite, ignoreBlack);
    }

    private static bool ShouldIgnore(Rgb24 pixel, bool ignoreWhite, bool ignoreBlack)
        => (ignoreWhite && pixel.IsNearWhite()) || (ignoreBlack && pixel.IsNearBlack());

    private static ColorBucket[] Finish(int[] counts, long totalCount, bool pooled, out int distinctCount)
    {
        distinctCount = 0;
        if (totalCount == 0)
        {
            return Array.Empty<ColorBucket>();
        }

        for (int i = 0; i < Rgb5.BucketCount; i++)
        {
            if (counts[i] != 0)
            {
                distinctCount++;
            }
        }

        var buckets = pooled ? ArrayPool<ColorBucket>.Shared.Rent(distinctCount) : new ColorBucket[distinctCount];
        int index = 0;
        for (int i = 0; i < Rgb5.BucketCount; i++)
        {
            int count = counts[i];
            if (count != 0)
            {
                buckets[index++] = new ColorBucket(Rgb5.FromPacked((ushort)i), count);
            }
        }

        return buckets;
    }
}
