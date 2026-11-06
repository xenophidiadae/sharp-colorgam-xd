using Colorgram;

namespace Colorgram.Internal;

/// <summary>A non-empty histogram bucket prepared for clustering.</summary>
internal readonly struct ColorSample
{
    public ColorSample(LabColor lab, Rgb24 rgb, int weight)
    {
        if (weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Sample weight must be positive.");
        }

        Lab = lab;
        Rgb = rgb;
        Weight = weight;
    }

    public LabColor Lab { get; }

    public Rgb24 Rgb { get; }

    /// <summary>Number of pixels represented by this sample.</summary>
    public int Weight { get; }
}

/// <summary>A weighted cluster: its L*a*b* center, average sRGB color and total weight.</summary>
internal readonly struct ColorCluster
{
    public ColorCluster(LabColor lab, Rgb24 rgb, double weight)
    {
        Lab = lab;
        Rgb = rgb;
        Weight = weight;
    }

    public LabColor Lab { get; }

    public Rgb24 Rgb { get; }

    public double Weight { get; }
}
