using Colorgram;

namespace Colorgram.Internal;

/// <summary>Small deterministic PRNG; avoids depending on the runtime's <see cref="Random"/> implementation.</summary>
internal struct SplitMix64
{
    private ulong _state;

    public SplitMix64(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Returns a double in the [0, 1) range.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
