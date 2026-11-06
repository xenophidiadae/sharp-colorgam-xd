using Colorgram;

namespace Colorgram.Internal;

internal static class ColorMath
{
    public static byte ToByte(double value)
        => (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
