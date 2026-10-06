namespace FrigateLabeler;

/// <summary>
/// Images waiting to be labeled, in two lines:
///   all-at-once: labeled back to back in the background;
///   one-at-a-time: the next image is labeled only after the person has dealt with the previous one
///   (approved it, or pressed Next), so nothing is spent ahead of review and answers and camera notes
///   from each image apply to the next.
/// The all-at-once line goes first. Each result is saved as soon as it's done.
/// </summary>
public sealed class LabelingJob(FrigateClient frigate, Func<ClaudeLabeler> labelerFactory, RunStore store,
    CostEstimator costs, bool verbose)
{
    private readonly object _lock = new();
    private readonly LinkedList<ImageSummary> _pending = new();
    private readonly LinkedList<ImageSummary> _oneAtATime = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Dictionary<string, List<string>> _labelCache = new();
    private ClaudeLabeler? _labeler;
    /// <summary>The one-at-a-time image waiting for the person; the next one starts when it's released.</summary>
    private string? _awaiting;

    public int Processed { get; private set; }
    public int Failed { get; private set; }
    public int Flagged { get; private set; }
    public string? Current { get; private set; }
    public string? LastError { get; private set; }

    public int Queued { get { lock (_lock) return _pending.Count; } }
    public int QueuedOneAtATime { get { lock (_lock) return _oneAtATime.Count; } }

    public bool IsQueuedOrCurrent(string imageId)
    {
        lock (_lock)
            return Current?.EndsWith("/" + imageId) == true
                   || _pending.Any(i => i.Id == imageId) || _oneAtATime.Any(i => i.Id == imageId);
    }

    /// <summary>Adds images to a line, skipping any already waiting. Returns how many were added.</summary>
    public int Enqueue(IEnumerable<ImageSummary> images, bool oneAtATime = false)
    {
        var added = 0;
        lock (_lock)
        {
            var line = oneAtATime ? _oneAtATime : _pending;
            foreach (var image in images)
            {
                if (IsQueuedOrCurrent(image.Id)) continue;
                line.AddLast(image);
                added++;
            }
        }
        if (added > 0) Wake();
        return added;
    }

    /// <summary>The person approved (or otherwise finished with) an image: start the next one-at-a-time image.</summary>
    public void Released(string imageId)
    {
        lock (_lock)
        {
            if (_awaiting != imageId) return;
            _awaiting = null;
        }
        Wake();
    }

    /// <summary>"Next image": stop waiting on the current one-at-a-time image and label the next.</summary>
    public void Advance()
    {
        lock (_lock) _awaiting = null;
        Wake();
    }

    /// <summary>"Process the rest now": move the one-at-a-time line into the background line.</summary>
    public int ProcessRestNow()
    {
        int n;
        lock (_lock)
        {
            n = _oneAtATime.Count;
            foreach (var image in _oneAtATime) _pending.AddLast(image);
            _oneAtATime.Clear();
            _awaiting = null;   // nothing left to hold back; a later one-at-a-time batch starts right away
        }
        Wake();
        return n;
    }

    /// <summary>Drops everything still waiting in both lines (the image in progress finishes).</summary>
    public int CancelPending()
    {
        lock (_lock)
        {
            var n = _pending.Count + _oneAtATime.Count;
            _pending.Clear();
            _oneAtATime.Clear();
            _awaiting = null;
            return n;
        }
    }

    /// <summary>Continue after a pause (e.g. a new Frigate+ sign-in was pasted).</summary>
    public void Resume()
    {
        LastError = null;
        Wake();
    }

    private void Wake()
    {
        lock (_wake) if (_wake.CurrentCount == 0) _wake.Release();
    }

    public object Status()
    {
        lock (_lock)
        {
            var waiting = _pending.Count + _oneAtATime.Count + (Current is null ? 0 : 1);
            return new
            {
                active = Current is not null,
                current = Current,
                queued = _pending.Count,
                oneAtATime = _oneAtATime.Count,
                awaiting = _awaiting,
                processed = Processed,
                failed = Failed,
                flagged = Flagged,
                lastError = LastError,
                spent = Math.Round(costs.SessionTotal, 4),
                average = costs.Average is { } a ? Math.Round(a, 4) : (double?)null,
                remaining = costs.Average is { } r ? Math.Round(r * waiting, 2) : (double?)null,
            };
        }
    }

    /// <summary>Works the queue until cancelled (web app) or, with <paramref name="stopWhenEmpty"/>, until it's empty.</summary>
    public async Task RunAsync(CancellationToken ct, bool stopWhenEmpty = false)
    {
        while (!ct.IsCancellationRequested)
        {
            ImageSummary? image = null;
            var oneAtATime = false;
            lock (_lock)
            {
                if (_pending.First is { } next)
                {
                    image = next.Value;
                    _pending.RemoveFirst();
                }
                else if (_awaiting is null && _oneAtATime.First is { } step)
                {
                    image = step.Value;
                    _oneAtATime.RemoveFirst();
                    oneAtATime = true;
                }
                if (image is not null) Current = $"{image.Camera}/{image.Id}";
            }

            if (image is null)
            {
                if (stopWhenEmpty && Queued == 0) return;
                await _wake.WaitAsync(ct);
                continue;
            }

            Console.WriteLine($"\n[{Processed + 1}, {Queued + QueuedOneAtATime} waiting] {Current}" +
                              (oneAtATime ? "  (one at a time)" : ""));
            try
            {
                var run = await LabelOneAsync(image, ct);
                costs.Add(run.CostUsd);
                Print(run);
                // Hold the one-at-a-time line until the person has dealt with this image.
                if (oneAtATime) lock (_lock) _awaiting = image.Id;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (FrigateAuthException ex)
            {
                // Sign-in expired: put the image back and wait until a new token is pasted (Resume).
                LastError = ex.Message;
                Console.Error.WriteLine($"    paused: {ex.Message}");
                lock (_lock)
                {
                    (oneAtATime ? _oneAtATime : _pending).AddFirst(image);
                    Current = null;
                }
                await _wake.WaitAsync(ct);
                continue;
            }
            catch (Exception ex)
            {
                Failed++;
                LastError = $"{Current}: {ex.Message}";
                Console.Error.WriteLine($"    failed: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                    if (Current is not null)   // null when the image was put back (sign-in pause)
                    {
                        Processed++;
                        Current = null;
                    }
            }

            Console.WriteLine($"    spent ${costs.SessionTotal:F2}  ·  avg ${costs.Average ?? 0:F3}/image" +
                              $"  ·  {Queued} in background, {QueuedOneAtATime} one at a time");
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
