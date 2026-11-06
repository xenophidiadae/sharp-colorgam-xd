using System.Buffers;

namespace Colorgram.Internal;

/// <summary>
/// Weighted k-means over CIE L*a*b* colors. Initialization uses weighted k-means++ and the
/// Lloyd iterations use squared L*a*b* distance (Delta E 76), so centroids stay the weighted mean.
/// </summary>
internal static class WeightedKMeans
{
    private const float ConvergenceThresholdSquared = 1e-6f;

    /// <summary>Writes non-empty clusters into the destination and returns their count.</summary>
    public static int Run(
        ReadOnlySpan<ColorSample> samples,
        int clusterCount,
        int seed,
        int maxIterations,
        Span<ColorCluster> destination)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        int k = Math.Min(clusterCount, samples.Length);
        if (k <= 0)
        {
            return 0;
        }

        if (maxIterations <= 0)
        {
            maxIterations = 1;
        }

        if (k == 1)
        {
            destination[0] = AggregateAll(samples);
            return 1;
        }

        var random = new SplitMix64((ulong)(uint)seed);
        LabColor[] centroidArray = ArrayPool<LabColor>.Shared.Rent(k);
        try
        {
            int actualK = Initialize(samples, centroidArray.AsSpan(0, k), ref random);
            Span<LabColor> centroids = centroidArray.AsSpan(0, actualK);
            int sampleCount = samples.Length;

            int[] assignments = ArrayPool<int>.Shared.Rent(sampleCount);
            double[] sums = ArrayPool<double>.Shared.Rent(actualK * 7);
            LabColor[] nextCentroids = ArrayPool<LabColor>.Shared.Rent(actualK);
            assignments.AsSpan(0, sampleCount).Fill(-1);
            Span<double> sumL = sums.AsSpan(0, actualK);
            Span<double> sumA = sums.AsSpan(actualK, actualK);
            Span<double> sumB = sums.AsSpan(actualK * 2, actualK);
            Span<double> sumR = sums.AsSpan(actualK * 3, actualK);
            Span<double> sumG = sums.AsSpan(actualK * 4, actualK);
            Span<double> sumBlue = sums.AsSpan(actualK * 5, actualK);
            Span<double> weights = sums.AsSpan(actualK * 6, actualK);
            try
            {
                bool converged = false;

                for (int iteration = 0; iteration < maxIterations; iteration++)
                {
                    sumL.Clear(); sumA.Clear(); sumB.Clear(); weights.Clear();

                    bool assignmentChanged = false;

                    for (int i = 0; i < sampleCount; i++)
                    {
                        ColorSample sample = samples[i];
                        int best = FindNearest(sample.Lab, centroids);

                        if (assignments[i] != best)
                        {
                            assignments[i] = best;
                            assignmentChanged = true;
                        }

                        double weight = sample.Weight;
                        sumL[best] += weight * sample.Lab.L;
                        sumA[best] += weight * sample.Lab.A;
                        sumB[best] += weight * sample.Lab.B;
                        weights[best] += weight;
                    }

                    double maxShiftSquared = 0.0;

                    for (int cluster = 0; cluster < actualK; cluster++)
                    {
                        if (weights[cluster] <= 0.0)
                        {
                            int replacement = FindFarthestSample(samples, centroids, assignments);
                            nextCentroids[cluster] = samples[replacement].Lab;
                            maxShiftSquared = double.MaxValue;
                            continue;
                        }

                        double weight = weights[cluster];
                        nextCentroids[cluster] = new LabColor(
                            (float)(sumL[cluster] / weight),
                            (float)(sumA[cluster] / weight),
                            (float)(sumB[cluster] / weight));

                        double shift = DistanceSquared(centroids[cluster], nextCentroids[cluster]);
                        if (shift > maxShiftSquared)
                        {
                            maxShiftSquared = shift;
                        }
                    }

                    nextCentroids.AsSpan(0, actualK).CopyTo(centroids);

                    if (!assignmentChanged && maxShiftSquared <= ConvergenceThresholdSquared)
                    {
                        converged = true;
                        break;
                    }
                }

                // Stable assignments reproduce the same centers exactly. If the iteration limit was
                // reached instead, assign once more against the updated centers before aggregating RGB.
                sumL.Clear(); sumA.Clear(); sumB.Clear(); sumR.Clear(); sumG.Clear(); sumBlue.Clear(); weights.Clear();

                for (int i = 0; i < sampleCount; i++)
                {
                    ColorSample sample = samples[i];
                    int best = converged ? assignments[i] : FindNearest(sample.Lab, centroids);

                    double weight = sample.Weight;
                    sumL[best] += weight * sample.Lab.L;
                    sumA[best] += weight * sample.Lab.A;
                    sumB[best] += weight * sample.Lab.B;
                    sumR[best] += weight * sample.Rgb.R;
                    sumG[best] += weight * sample.Rgb.G;
                    sumBlue[best] += weight * sample.Rgb.B;
                    weights[best] += weight;
                }

                int resultCount = 0;
                for (int cluster = 0; cluster < actualK; cluster++)
                {
                    double weight = weights[cluster];
                    if (weight <= 0.0)
                    {
                        continue;
                    }

                    destination[resultCount++] = new ColorCluster(
                        new LabColor(
                            (float)(sumL[cluster] / weight),
                            (float)(sumA[cluster] / weight),
                            (float)(sumB[cluster] / weight)),
                        new Rgb24(
                            ColorMath.ToByte(sumR[cluster] / weight),
                            ColorMath.ToByte(sumG[cluster] / weight),
                            ColorMath.ToByte(sumBlue[cluster] / weight)),
                        weight);
                }

                return resultCount;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(assignments);
                ArrayPool<double>.Shared.Return(sums);
                ArrayPool<LabColor>.Shared.Return(nextCentroids);
            }
        }
        finally
        {
            ArrayPool<LabColor>.Shared.Return(centroidArray);
        }
    }

    private static int Initialize(ReadOnlySpan<ColorSample> samples, Span<LabColor> centroids, ref SplitMix64 random)
    {
        bool[] chosenArray = ArrayPool<bool>.Shared.Rent(samples.Length);
        float[] distanceArray = ArrayPool<float>.Shared.Rent(samples.Length);
        Span<bool> chosen = chosenArray.AsSpan(0, samples.Length);
        Span<float> minDistanceSquared = distanceArray.AsSpan(0, samples.Length);
        chosen.Clear();
        try
        {
            double totalWeight = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                totalWeight += samples[i].Weight;
            }

            int first = PickWeighted(samples, random.NextDouble() * totalWeight);
            centroids[0] = samples[first].Lab;
            chosen[first] = true;
            int count = 1;

            minDistanceSquared.Fill(float.PositiveInfinity);

            while (count < centroids.Length)
            {
                double total = 0.0;
                for (int i = 0; i < samples.Length; i++)
                {
                    float distance = DistanceSquared(samples[i].Lab, centroids[count - 1]);
                    if (distance < minDistanceSquared[i])
                    {
                        minDistanceSquared[i] = distance;
                    }

                    if (!chosen[i])
                    {
                        total += samples[i].Weight * minDistanceSquared[i];
                    }
                }

                int next;
                if (total <= 0.0)
                {
                    next = FindHighestWeightUnchosen(samples, chosen);
                    if (next < 0)
                    {
                        break;
                    }
                }
                else
                {
                    double target = random.NextDouble() * total;
                    double cumulative = 0.0;
                    next = -1;

                    for (int i = 0; i < samples.Length; i++)
                    {
                        if (chosen[i])
                        {
                            continue;
                        }

                        cumulative += samples[i].Weight * minDistanceSquared[i];
                        if (cumulative >= target)
                        {
                            next = i;
                            break;
                        }
                    }

                    if (next < 0)
                    {
                        next = FindHighestWeightUnchosen(samples, chosen);
                        if (next < 0)
                        {
                            break;
                        }
                    }
                }

                centroids[count++] = samples[next].Lab;
                chosen[next] = true;
            }

            return count;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(chosenArray);
            ArrayPool<float>.Shared.Return(distanceArray);
        }
    }

    private static int FindNearest(LabColor color, ReadOnlySpan<LabColor> centroids)
    {
        int best = 0;
        float bestDistance = DistanceSquared(color, centroids[0]);

        for (int i = 1; i < centroids.Length; i++)
        {
            float distance = DistanceSquared(color, centroids[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static int FindFarthestSample(
        ReadOnlySpan<ColorSample> samples,
        ReadOnlySpan<LabColor> centroids,
        ReadOnlySpan<int> assignments)
    {
        int best = 0;
        double bestScore = -1.0;

        for (int i = 0; i < samples.Length; i++)
        {
            int assignment = assignments[i];
            if (assignment < 0)
            {
                continue;
            }

            double score = (double)samples[i].Weight * DistanceSquared(samples[i].Lab, centroids[assignment]);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    private static int FindHighestWeightUnchosen(ReadOnlySpan<ColorSample> samples, ReadOnlySpan<bool> chosen)
    {
        int best = -1;
        int bestWeight = -1;

        for (int i = 0; i < samples.Length; i++)
        {
            if (!chosen[i] && samples[i].Weight > bestWeight)
            {
                bestWeight = samples[i].Weight;
                best = i;
            }
        }

        return best;
    }

    private static int PickWeighted(ReadOnlySpan<ColorSample> samples, double target)
    {
        double cumulative = 0.0;

        for (int i = 0; i < samples.Length; i++)
        {
            cumulative += samples[i].Weight;
            if (cumulative >= target)
            {
                return i;
            }
        }

        return samples.Length - 1;
    }

    private static ColorCluster AggregateAll(ReadOnlySpan<ColorSample> samples)
    {
        double weight = 0.0;
        double l = 0.0;
        double a = 0.0;
        double b = 0.0;
        double r = 0.0;
        double g = 0.0;
        double blue = 0.0;

        foreach (ColorSample sample in samples)
        {
            double sampleWeight = sample.Weight;
            weight += sampleWeight;
            l += sampleWeight * sample.Lab.L;
            a += sampleWeight * sample.Lab.A;
            b += sampleWeight * sample.Lab.B;
            r += sampleWeight * sample.Rgb.R;
            g += sampleWeight * sample.Rgb.G;
            blue += sampleWeight * sample.Rgb.B;
        }

        return new ColorCluster(
            new LabColor((float)(l / weight), (float)(a / weight), (float)(b / weight)),
            new Rgb24(
                ColorMath.ToByte(r / weight),
                ColorMath.ToByte(g / weight),
                ColorMath.ToByte(blue / weight)),
            weight);
    }

    private static float DistanceSquared(LabColor left, LabColor right) => left.DistanceSquared(right);
}
