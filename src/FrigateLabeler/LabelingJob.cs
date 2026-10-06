namespace FrigateLabeler;

/// <summary>
/// A queue of images to label, worked through one at a time in the background. The review page
/// adds to it (and can cancel what's still waiting); each result is saved as soon as it's done.
/// </summary>
public sealed class LabelingJob(FrigateClient frigate, Func<ClaudeLabeler> labelerFactory, RunStore store,
    CostEstimator costs, bool verbose)
{
    private readonly object _lock = new();
    private readonly LinkedList<ImageSummary> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Dictionary<string, List<string>> _labelCache = new();
    private ClaudeLabeler? _labeler;

    public int Processed { get; private set; }
    public int Failed { get; private set; }
    public int Flagged { get; private set; }
    public string? Current { get; private set; }
    public string? LastError { get; private set; }

    public int Queued { get { lock (_lock) return _pending.Count; } }

    public bool IsQueuedOrCurrent(string imageId)
    {
        lock (_lock) return Current?.EndsWith("/" + imageId) == true || _pending.Any(i => i.Id == imageId);
    }

    /// <summary>Adds images to the end of the queue, skipping any already waiting. Returns how many were added.</summary>
    public int Enqueue(IEnumerable<ImageSummary> images)
    {
        var added = 0;
        lock (_lock)
            foreach (var image in images)
            {
                if (_pending.Any(i => i.Id == image.Id) || Current?.EndsWith("/" + image.Id) == true) continue;
                _pending.AddLast(image);
                added++;
            }
        if (added > 0) _signal.Release(added);
        return added;
    }

    /// <summary>Drops everything still waiting (the image in progress finishes).</summary>
    public int CancelPending()
    {
        lock (_lock)
        {
            var n = _pending.Count;
            _pending.Clear();
            return n;
        }
    }

    public object Status() => new
    {
        active = Current is not null,
        current = Current,
        queued = Queued,
        processed = Processed,
        failed = Failed,
        flagged = Flagged,
        lastError = LastError,
        spent = Math.Round(costs.SessionTotal, 4),
        average = costs.Average is { } a ? Math.Round(a, 4) : (double?)null,
        remaining = costs.Average is { } r ? Math.Round(r * (Queued + (Current is null ? 0 : 1)), 2) : (double?)null,
    };

    /// <summary>Works the queue until cancelled (review page) or, with <paramref name="stopWhenEmpty"/>, until it's empty.</summary>
    public async Task RunAsync(CancellationToken ct, bool stopWhenEmpty = false)
    {
        while (!ct.IsCancellationRequested)
        {
            if (stopWhenEmpty && Queued == 0) return;
            await _signal.WaitAsync(ct);

            ImageSummary? image;
            lock (_lock)
            {
                image = _pending.First?.Value;
                if (image is null) continue;           // cancelled while we waited
                _pending.RemoveFirst();
                Current = $"{image.Camera}/{image.Id}";
            }

            Console.WriteLine($"\n[{Processed + 1}, {Queued} waiting] {Current}");
            try
            {
                var run = await LabelOneAsync(image, ct);
                costs.Add(run.CostUsd);
                Print(run);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not FrigateAuthException)
            {
                Failed++;
                LastError = $"{Current}: {ex.Message}";
                Console.Error.WriteLine($"    failed: {ex.Message}");
            }
            finally
            {
                Processed++;
                lock (_lock) Current = null;
            }

            Console.WriteLine($"    spent ${costs.SessionTotal:F2}  ·  avg ${costs.Average ?? 0:F3}/image" +
                              $"  ·  {Queued} waiting {costs.Estimate(Queued)}");
        }
    }

    private async Task<LabelRun> LabelOneAsync(ImageSummary image, CancellationToken ct)
    {
        if (!_labelCache.TryGetValue(image.Camera, out var labels))
            _labelCache[image.Camera] = labels = await frigate.GetCameraLabelsAsync(image.Camera, ct);

        _labeler ??= labelerFactory();
        // Re-read the image: its verified labels may have changed since it was queued.
        var fresh = await frigate.GetImageAsync(image.Id, ct);
        var jpeg = await frigate.DownloadImageAsync(image.Id, ct);
        var data = await frigate.GetImageDataAsync(image.Id, ct);
        var run = await _labeler.LabelAsync(fresh, jpeg, data, labels, ct);
        store.Save(run, jpeg);
        return run;
    }

    private void Print(LabelRun run)
    {
        if (run.AllowedLabels.Count == 0)
        {
            Console.WriteLine("    nothing to do: every label on this camera is already verified");
            return;
        }
        var summary = run.Objects.GroupBy(o => o.Label).Select(g => $"{g.Count()} {g.Key}");
        Console.WriteLine($"    {run.AllowedLabels.Count} labels checked; " +
                          (run.VerifiedLabels.Count == 0 ? "none verified before"
                              : $"{run.VerifiedLabels.Count} were verified before and were re-checked"));
        Console.WriteLine($"    {(run.Objects.Count == 0 ? "nothing found" : string.Join(", ", summary))}" +
                          $"  ·  ${run.CostUsd:F3} ({run.ApiCalls} API calls, {run.ReviewRounds} review round(s))");
        if (run.NeedsHuman)
        {
            Flagged++;
            Console.WriteLine("    ? needs your input:");
            var n = 0;
            foreach (var u in run.Uncertain) Console.WriteLine($"      ?{++n} {u.Description}");
            foreach (var issue in run.Issues.Except(run.Uncertain.Select(u => u.Description)))
                Console.WriteLine($"      - {issue}");
        }
        else
            Console.WriteLine("    ✓ review passed");

        if (verbose)
            foreach (var o in run.Objects.Where(o => o.Note is not null))
                Console.WriteLine($"      note [{o.Label}]: {o.Note}");
    }
}
