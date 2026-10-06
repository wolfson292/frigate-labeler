namespace FrigateLabeler;

/// <summary>Claude list prices (USD per million tokens), used for cost reporting only.</summary>
public sealed record ModelPrice(double Input, double Output, double CacheRead, double CacheWrite)
{
    private static readonly Dictionary<string, ModelPrice> Known = new()
    {
        ["claude-opus-5-5"] = new(4.00, 20.00, 0.20, 5.00),
        ["claude-opus-5"] = new(5.00, 25.00, 0.50, 6.25),
        ["claude-opus-4-8"] = new(5.00, 25.00, 0.50, 6.25),
        ["claude-sonnet-5-5"] = new(2.00, 10.00, 0.20, 2.50),
        ["claude-haiku-4-5"] = new(1.00, 5.00, 0.10, 1.25),
    };

    /// <summary>Unknown models are priced like Opus 5.5 so estimates are never silently zero.</summary>
    public static ModelPrice For(string model) => Known.GetValueOrDefault(model, Known["claude-opus-5-5"]);

    public double Cost(long input, long output, long cacheRead, long cacheWrite) =>
        (input * Input + output * Output + cacheRead * CacheRead + cacheWrite * CacheWrite) / 1_000_000;
}

/// <summary>
/// Running per-image cost average, seeded from earlier results so the very first estimate is
/// already informed. Images differ a lot (one refine call per object), so the estimate is
/// recomputed after every image.
/// </summary>
public sealed class CostEstimator(IEnumerable<double> history)
{
    private readonly List<double> _samples = history.Where(c => c > 0).ToList();

    public int SampleCount => _samples.Count;
    public double? Average => _samples.Count == 0 ? null : _samples.Average();
    public double SessionTotal { get; private set; }
    public int SessionCount { get; private set; }

    public void Add(double cost)
    {
        _samples.Add(cost);
        SessionTotal += cost;
        SessionCount++;
    }

    public string Estimate(int images) => Average is { } avg ? $"≈ ${avg * images:F2}" : "unknown until the first image is done";

    /// <summary>Per-image cost of every result already in the output folder.</summary>
    public static List<double> LoadHistory(string outDir) =>
        !Directory.Exists(outDir)
            ? []
            : Directory.EnumerateFiles(outDir, "*.json", SearchOption.AllDirectories)
                .Select(p =>
                {
                    try { return System.Text.Json.JsonSerializer.Deserialize<LabelRun>(File.ReadAllText(p), AppPaths.Json); }
                    catch (System.Text.Json.JsonException) { return null; }
                })
                .OfType<LabelRun>()
                .Select(r => r.CostUsd > 0 ? r.CostUsd : ModelPrice.For(r.Model).Cost(r.InputTokens, r.OutputTokens, 0, 0))
                .ToList();
}
