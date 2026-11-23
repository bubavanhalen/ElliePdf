namespace ElliePdf.Telemetry;

public sealed record ConfidenceInterval(double Lower, double Upper)
{
    public double Width => Upper - Lower;
}

public sealed record BenchmarkStatistics(int SampleCount, double Median, double P95, double P99, ConfidenceInterval Bootstrap95)
{
    public double Minimum { get; init; }
    public double Maximum { get; init; }
    public bool IsStable => Bootstrap95.Width <= Math.Abs(P95) * .10;

    public static BenchmarkStatistics Compute(IReadOnlyList<double> samples, int bootstrapSamples = 10_000, int seed = 1729)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("At least one measurement is required.", nameof(samples));
        if (bootstrapSamples < 100) throw new ArgumentOutOfRangeException(nameof(bootstrapSamples));
        var sorted = samples.Order().ToArray();
        var estimates = new double[bootstrapSamples];
        var random = new Random(seed);
        var resample = new double[samples.Count];
        for (var b = 0; b < bootstrapSamples; b++)
        {
            for (var i = 0; i < resample.Length; i++) resample[i] = samples[random.Next(samples.Count)];
            estimates[b] = Percentile(resample, .95);
        }
        Array.Sort(estimates);
        return new(samples.Count, Percentile(sorted, .50), Percentile(sorted, .95), Percentile(sorted, .99),
            new(Percentile(estimates, .025), Percentile(estimates, .975)))
        {
            Minimum = sorted[0],
            Maximum = sorted[^1]
        };
    }

    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || percentile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));
        var sorted = values is double[] a ? a : values.Order().ToArray();
        var index = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(index); var upper = (int)Math.Ceiling(index);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
    }
}
