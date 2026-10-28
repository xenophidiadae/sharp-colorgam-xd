using System.Globalization;

namespace Colorgram;

/// <summary>
/// A color in CIE L*a*b* space (D65 white point, 2 degree observer).
/// L* is roughly 0..100, a* and b* are roughly -128..127.
/// </summary>
public readonly struct LabColor : IEquatable<LabColor>
{
    private const double WhiteX = 0.95047;
    private const double WhiteY = 1.00000;
    private const double WhiteZ = 1.08883;

    // CIE standard constants: epsilon = 216 / 24389, kappa = 24389 / 27.
    private const double Epsilon = 216.0 / 24389.0;
    private const double Kappa = 24389.0 / 27.0;

    // 25^7, used by the CIEDE2000 chroma correction.
    private const double Pow25To7 = 6103515625.0;

    public LabColor(float l, float a, float b)
    {
        L = l;
        A = a;
        B = b;
    }

    public float L { get; }

    public float A { get; }

    public float B { get; }

    /// <summary>Converts an sRGB color to CIE L*a*b*.</summary>
    public static LabColor FromRgb24(Rgb24 color)
    {
        double r = Decode(color.R);
        double g = Decode(color.G);
        double b = Decode(color.B);

        double x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;

        double fx = PivotXyz(x / WhiteX);
        double fy = PivotXyz(y / WhiteY);
        double fz = PivotXyz(z / WhiteZ);

        return new LabColor(
            (float)(116.0 * fy - 16.0),
            (float)(500.0 * (fx - fy)),
            (float)(200.0 * (fy - fz)));
    }

    /// <summary>Converts this L*a*b* color back to sRGB, clamping out-of-gamut values.</summary>
    public Rgb24 ToRgb24()
    {
        double fy = (L + 16.0) / 116.0;
        double fx = fy + A / 500.0;
        double fz = fy - B / 200.0;

        double xr = InversePivot(fx);
        double yr = InversePivot(fy);
        double zr = InversePivot(fz);

        double x = xr * WhiteX;
        double y = yr * WhiteY;
        double z = zr * WhiteZ;

        double r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
        double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
        double b = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;

        return new Rgb24(Encode(r), Encode(g), Encode(b));
    }

    /// <summary>Squared Euclidean distance in L*a*b* (the square of Delta E 76).</summary>
    public float DistanceSquared(LabColor other)
    {
        float dl = L - other.L;
        float da = A - other.A;
        float db = B - other.B;
        return dl * dl + da * da + db * db;
    }

    /// <summary>CIE Delta E 76 distance.</summary>
    public float DeltaE76(LabColor other) => MathF.Sqrt(DistanceSquared(other));

    /// <summary>CIE Delta E 2000 distance, which is much closer to perceived color difference.</summary>
    public float DeltaE2000(LabColor other)
    {
        double l1 = L;
        double a1 = A;
        double b1 = B;
        double l2 = other.L;
        double a2 = other.A;
        double b2 = other.B;

        double c1 = Math.Sqrt(a1 * a1 + b1 * b1);
        double c2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double cBar = (c1 + c2) * 0.5;
        double cBar7 = Math.Pow(cBar, 7);
        double g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + Pow25To7)));

        double a1p = (1.0 + g) * a1;
        double a2p = (1.0 + g) * a2;

        double c1p = Math.Sqrt(a1p * a1p + b1 * b1);
        double c2p = Math.Sqrt(a2p * a2p + b2 * b2);

        double h1p = HueAngle(a1p, b1);
        double h2p = HueAngle(a2p, b2);

        double dLp = l2 - l1;
        double dCp = c2p - c1p;

        double dhp;
        if (c1p * c2p == 0.0)
        {
            dhp = 0.0;
        }
        else
        {
            dhp = h2p - h1p;
            if (dhp > 180.0)
            {
                dhp -= 360.0;
            }
            else if (dhp < -180.0)
            {
                dhp += 360.0;
            }
        }

        double dHp = 2.0 * Math.Sqrt(c1p * c2p) * Math.Sin(ToRadians(dhp) * 0.5);

        double lBarP = (l1 + l2) * 0.5;
        double cBarP = (c1p + c2p) * 0.5;

        double hBarP;
        if (c1p * c2p == 0.0)
        {
            hBarP = h1p + h2p;
        }
        else
        {
            double hueSum = h1p + h2p;
            double hueDifference = Math.Abs(h1p - h2p);
            if (hueDifference <= 180.0)
            {
                hBarP = hueSum * 0.5;
            }
            else if (hueSum < 360.0)
            {
                hBarP = (hueSum + 360.0) * 0.5;
            }
            else
            {
                hBarP = (hueSum - 360.0) * 0.5;
            }
        }

        double t = 1.0
            - 0.17 * Math.Cos(ToRadians(hBarP - 30.0))
            + 0.24 * Math.Cos(ToRadians(2.0 * hBarP))
            + 0.32 * Math.Cos(ToRadians(3.0 * hBarP + 6.0))
            - 0.20 * Math.Cos(ToRadians(4.0 * hBarP - 63.0));

        double dTheta = 30.0 * Math.Exp(-Square((hBarP - 275.0) / 25.0));
        double cBarP7 = Math.Pow(cBarP, 7);
        double rC = 2.0 * Math.Sqrt(cBarP7 / (cBarP7 + Pow25To7));
        double lBarP50Squared = Square(lBarP - 50.0);
        double sL = 1.0 + 0.015 * lBarP50Squared / Math.Sqrt(20.0 + lBarP50Squared);
        double sC = 1.0 + 0.045 * cBarP;
        double sH = 1.0 + 0.015 * cBarP * t;
        double rT = -Math.Sin(ToRadians(2.0 * dTheta)) * rC;

        double dL = dLp / sL;
        double dC = dCp / sC;
        double dH = dHp / sH;

        return (float)Math.Sqrt(dL * dL + dC * dC + dH * dH + rT * dC * dH);
    }

    public bool Equals(LabColor other) => L.Equals(other.L) && A.Equals(other.A) && B.Equals(other.B);

    public override bool Equals(object? obj) => obj is LabColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(L, A, B);

    public static bool operator ==(LabColor left, LabColor right) => left.Equals(right);

    public static bool operator !=(LabColor left, LabColor right) => !left.Equals(right);

    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Lab({L:0.###}, {A:0.###}, {B:0.###})");

    private static double Decode(byte value)
    {
        double c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static byte Encode(double value)
    {
        double c = value <= 0.0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(c * 255.0, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static double PivotXyz(double value)
        => value > Epsilon ? Math.Cbrt(value) : (Kappa * value + 16.0) / 116.0;

    private static double InversePivot(double value)
    {
        double cube = value * value * value;
        return cube > Epsilon ? cube : (116.0 * value - 16.0) / Kappa;
    }

    private static double HueAngle(double a, double b)
    {
        if (a == 0.0 && b == 0.0)
        {
            return 0.0;
        }

        double degrees = ToDegrees(Math.Atan2(b, a));
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }

    private static double ToRadians(double degrees) => degrees * (Math.PI / 180.0);

    private static double ToDegrees(double radians) => radians * (180.0 / Math.PI);

    private static double Square(double value) => value * value;
}
