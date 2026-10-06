using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FrigateLabeler;

/// <summary>
/// The web app (127.0.0.1 by default; in Docker it listens on all interfaces behind a reverse proxy):
///   Label tab: browse Frigate+ images by camera and pick what to label; it joins the background queue.
///   Review tab: answer Claude's questions, describe errors for Claude to fix, edit boxes, approve.
/// </summary>
public sealed class ReviewServer(RunStore store, FrigateClient frigate, Func<ClaudeLabeler> labelerFactory,
    LabelingJob job, CostEstimator costs)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, byte[]> _thumbs = new();
    private readonly ConcurrentDictionary<string, List<string>> _cameraLabels = new();
    private readonly ConcurrentDictionary<string, CameraCounts> _counts = new();
    private readonly ConcurrentDictionary<string, bool> _counting = new();
    private ClaudeLabeler? _labeler;

    public async Task RunAsync(string host, int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        // Served under a subfolder behind a reverse proxy (e.g. https://host/frigate-labeler/):
        // strip the prefix, and send the bare prefix to the trailing-slash URL the page's relative links need.
        if (Environment.GetEnvironmentVariable("FRIGATE_LABELER_BASE_PATH") is { Length: > 1 } basePath)
        {
            basePath = "/" + basePath.Trim('/');
            app.UsePathBase(basePath);
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.PathBase.HasValue && !ctx.Request.Path.HasValue)
                    ctx.Response.Redirect(ctx.Request.PathBase + "/");
                else
                    await next();
            });
            Console.WriteLine($"Base path: {basePath}");
        }
        app.UseRouting();   // after UsePathBase, so routes see the stripped path

        var index = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        app.MapGet("/", () => Results.File(index, "text/html; charset=utf-8"));
        app.MapGet("/healthz", () => "ok");

        // Frigate+ sign-in: status, and replacing the refresh token from the page (no terminal in Docker).
        app.MapGet("/api/auth", async (CancellationToken rct) =>
            await frigate.CheckAuthAsync(rct) is { } problem
                ? new { ok = false, problem = (string?)problem }
                : new { ok = true, problem = (string?)null });
        app.MapPost("/api/auth", async (AuthRequest req, CancellationToken rct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RefreshToken)) return Results.BadRequest("Paste a refresh token.");
            frigate.ReplaceRefreshToken(req.RefreshToken);
            if (await frigate.CheckAuthAsync(rct) is { } problem) return Results.Ok(new { ok = false, problem });
            job.Resume();   // labeling paused on the expired sign-in carries on
            return Results.Ok(new { ok = true, problem = (string?)null });
        });

        // ---------- shared state ----------

        app.MapGet("/api/state", () => new
        {
            labeling = job.Status(),
            runs = store.LoadAll().OrderBy(StatusRank).ThenBy(r => r.CreatedAt).Select(Summary),
        });

        // ---------- Label tab ----------

        app.MapGet("/api/cameras", async (CancellationToken rct) =>
        {
            var cameras = await frigate.GetCamerasAsync(rct);
            foreach (var c in cameras) _cameraLabels[c.Name] = c.Labels;
            _ = RefreshCountsAsync(cameras, ct);   // counts fill in as they're computed
            return new
            {
                supported = FrigateLabels.Groups,
                cameras = cameras.Select(c => new
                {
                    name = c.Name,
                    images = c.NumImages,
                    labels = c.Labels,
                    notEnabled = FrigateLabels.NotEnabled(c.Labels),
                    hasNotes = CameraNotes.Load(c.Name) is not null,
                    counts = _counts.TryGetValue(c.Name, out var n) ? n : null,
                }),
            };
        });

        app.MapGet("/api/browse", async (string camera, string? filter, string? cursor, CancellationToken rct) =>
        {
            var f = ParseFilter(filter);
            var labels = await CameraLabelsAsync(camera, rct);
            var page = await frigate.ListImagesPageAsync(camera, f, cursor, 60, labels, rct);
            // Page sizes vary, so "more" means the cursor moved and this page wasn't empty.
            var local = store.LoadAll().ToDictionary(r => r.ImageId);
            return new
            {
                next = page.List.Count == 0 || page.LastImageId == cursor ? null : page.LastImageId,
                images = page.List.Select(i => new
                {
                    id = i.Id,
                    camera = i.Camera,
                    verifiedLabels = i.VerifiedLabels,
                    unverifiedLabels = FrigateLabels.Unverified(labels, i.VerifiedLabels),
                    local = job.IsQueuedOrCurrent(i.Id) ? "queued"
                        : local.TryGetValue(i.Id, out var r) ? Status(r) : null,
                }),
            };
        });

        app.MapGet("/api/thumbs/{id}", async (string id, CancellationToken rct) =>
        {
            if (!_thumbs.TryGetValue(id, out var bytes))
                _thumbs[id] = bytes = await frigate.DownloadThumbnailAsync(id, rct);
            return Results.File(bytes, "image/jpeg");
        });

        // Works out what a selection means (dry run), or queues it.
        app.MapPost("/api/label", async (LabelRequest req, CancellationToken rct) =>
        {
            var plan = await PlanAsync(req, rct);
            var queued = req.DryRun ? 0 : job.Enqueue(plan.ToLabel, req.OneAtATime);
            if (queued > 0) Console.WriteLine($"  queued {queued} image(s) from the Label tab " +
                                              (req.OneAtATime ? "(one at a time)" : "(all at once)"));
            return new
            {
                matched = plan.Matched,
                toLabel = plan.ToLabel.Count,
                queued,
                skippedVerified = plan.SkippedVerified,
                skippedLabeled = plan.SkippedLabeled,
                skippedQueued = plan.SkippedQueued,
                averageCost = costs.Average,
                estimate = costs.Average is { } avg ? Math.Round(avg * plan.ToLabel.Count, 2) : (double?)null,
            };
        });

        app.MapPost("/api/queue/cancel", () => new { cancelled = job.CancelPending() });
        app.MapPost("/api/queue/next", () => { job.Advance(); return new { ok = true }; });
        app.MapPost("/api/queue/all", () => new { moved = job.ProcessRestNow() });

        // ---------- Review tab ----------

        app.MapGet("/api/runs/{id}", (string id) =>
            store.Find(id) is { } run
                ? Results.Ok(new { run, status = Status(run), cameraNotes = CameraNotes.Load(run.Camera) ?? "" })
                : Results.NotFound());

        app.MapGet("/api/runs/{id}/image", async (string id, CancellationToken rct) =>
            store.Find(id) is { } run ? Results.File(await OriginalAsync(run, rct), "image/jpeg") : Results.NotFound());

        // Hand edits replace this run's boxes as-is (locked verified boxes aren't part of them).
        app.MapPut("/api/runs/{id}/objects", (string id, SaveObjectsRequest req) => WithLock(id, () =>
        {
            if (store.Find(id) is not { } run) return Task.FromResult(Results.NotFound());
            if (run.SubmittedAt is not null) return Task.FromResult(Results.Conflict("Already submitted."));
            var objects = req.Objects.Select(b =>
            {
                var box = new PixelBox(b.X1, b.Y1, b.X2, b.Y2).ClampTo(run.ImageWidth, run.ImageHeight);
                // Unchanged boxes keep their origin; anything the person moved, resized, relabeled or
                // drew is theirs, and Claude won't change it.
                var same = run.Objects.FirstOrDefault(o => o.Label == b.Label && o.Difficult == b.Difficult
                                                          && Math.Abs(o.Box.X1 - box.X1) < 0.5 && Math.Abs(o.Box.Y1 - box.Y1) < 0.5
                                                          && Math.Abs(o.Box.X2 - box.X2) < 0.5 && Math.Abs(o.Box.Y2 - box.Y2) < 0.5);
                return same ?? new LabeledObject
                {
                    Label = b.Label, Box = box, Difficult = b.Difficult, Confidence = 1, Refined = true,
                    Note = b.Note, Human = true,
                };
            }).Where(o => o.Box.Width >= 1 && o.Box.Height >= 1
                          && (run.AllowedLabels.Contains(o.Label) || run.VerifiedLabels.Contains(o.Label))).ToList();
            var updated = run with { Objects = objects, HumanEdited = true };
            store.Save(updated);
            return Task.FromResult(Results.Ok(new { run = updated, status = Status(updated) }));
        }));

        // Corrections and answers to Claude's questions; Claude applies them and re-checks.
        app.MapPost("/api/runs/{id}/feedback", (string id, FeedbackRequest req, CancellationToken rct) => WithLock(id, async () =>
        {
            if (store.Find(id) is not { } run) return Results.NotFound();
            if (run.SubmittedAt is not null) return Results.Conflict("Already submitted.");

            var parts = new List<string>();
            var answered = run.Answered.ToList();
            foreach (var a in req.Answers ?? [])
            {
                if (string.IsNullOrWhiteSpace(a.Answer)) continue;
                answered.Add(new UncertainRegion(a.Question,
                    run.Uncertain.FirstOrDefault(u => u.Description == a.Question)?.Region));
                parts.Add($"Q: {a.Question}\nA: {a.Answer.Trim()}");
                if (a.Remember) CameraNotes.Append(run.Camera, $"{a.Question} → {a.Answer.Trim()}");
            }
            if (!string.IsNullOrWhiteSpace(req.Text)) parts.Add(req.Text.Trim());
            if (parts.Count == 0) return Results.BadRequest("Nothing to send.");

            ClaudeLabeler labeler;
            try { labeler = _labeler ??= labelerFactory(); }
            catch (ConfigException ex) { return Results.BadRequest(ex.Message); }

            var updated = await labeler.ReviseAsync(run with { Answered = answered }, await OriginalAsync(run, rct),
                string.Join("\n\n", parts), rct);
            store.Save(updated);
            Console.WriteLine($"  review page: revised {run.Camera}/{run.ImageId} (${updated.CostUsd - run.CostUsd:F3})");
            return Results.Ok(new { run = updated, status = Status(updated) });
        }));

        app.MapPost("/api/runs/{id}/approve", (string id, ApproveRequest req, CancellationToken rct) => WithLock(id, async () =>
        {
            if (store.Find(id) is not { } run) return Results.NotFound();
            if (run.SubmittedAt is not null)
            {
                // Saved earlier without verifying: verify now.
                if (!req.Verify || run.VerifiedAt is not null || run.LabelsToVerify().Count == 0)
                    return Results.Ok(new { ok = true, message = "Already submitted." });
                await frigate.VerifyAsync(run.ImageId, run.LabelsToVerify(), rct);
                store.Save(run with { VerifiedAt = DateTimeOffset.UtcNow });
                Console.WriteLine($"  review page: verified {run.Camera}/{run.ImageId}");
                return Results.Ok(new { ok = true, message = $"Verified {run.LabelsToVerify().Count} label(s)." });
            }
            var (ok, message) = await Submitter.SubmitAsync(frigate, store, run, req.Force, req.Verify, rct);
            if (ok) job.Released(run.ImageId);   // one at a time: start labeling the next image
            Console.WriteLine($"  review page: {(ok ? "submitted" : "submit failed for")} {run.Camera}/{run.ImageId} — {message}");
            return Results.Ok(new { ok, message });
        }));

        app.MapGet("/api/cameras/{camera}/notes", (string camera) =>
            Results.Ok(new { camera, notes = CameraNotes.Load(camera) ?? "", path = CameraNotes.PathFor(camera) }));

        app.MapPut("/api/cameras/{camera}/notes", (string camera, NotesRequest req) =>
        {
            CameraNotes.Save(camera, req.Notes ?? "");
            return Results.Ok(new { camera, notes = CameraNotes.Load(camera) ?? "" });
        });

        await app.StartAsync(ct);
        Console.WriteLine(host is "127.0.0.1" or "localhost"
            ? $"Open http://localhost:{port}   (Ctrl+C to stop)"
            : $"Listening on http://{host}:{port}");
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        await app.StopAsync(CancellationToken.None);
    }

    // ---------- selection planning ----------

    private sealed record Plan(int Matched, List<ImageSummary> ToLabel, int SkippedVerified, int SkippedLabeled,
        int SkippedQueued);

    private async Task<Plan> PlanAsync(LabelRequest req, CancellationToken ct)
    {
        var filter = ParseFilter(req.Filter);
        var candidates = new List<ImageSummary>();
        if (req.ImageIds is { Count: > 0 })
        {
            foreach (var id in req.ImageIds.Distinct())
                candidates.Add(await frigate.GetImageAsync(id, ct));
        }
        else
        {
            var cameras = req.Camera is not null
                ? new List<string> { req.Camera }
                : (await frigate.GetCamerasAsync(ct)).Select(c => c.Name).ToList();
            foreach (var camera in cameras)
                await foreach (var img in frigate.ListImagesAsync(camera, filter, ct: ct))
                    candidates.Add(img);
        }

        // Picking images by hand, or the All filter, means "check these" even if every label is verified.
        var audit = req.ImageIds is { Count: > 0 } || filter == ImageFilter.All;
        var local = store.LoadAll().ToDictionary(r => r.ImageId);
        var toLabel = new List<ImageSummary>();
        int verified = 0, labeled = 0, queued = 0;
        foreach (var img in candidates)
        {
            var cameraLabels = await CameraLabelsAsync(img.Camera, ct);
            var needed = FrigateLabels.Unverified(cameraLabels, img.VerifiedLabels);
            if (needed.Count == 0 && !audit) { verified++; continue; }
            if (job.IsQueuedOrCurrent(img.Id)) { queued++; continue; }
            // Already labeled for every label it still needs: it's waiting in Review (or was submitted
            // without verifying). Relabel overrides that.
            if (!req.Relabel && local.TryGetValue(img.Id, out var run) && cameraLabels.All(run.AllowedLabels.Contains))
            {
                labeled++;
                continue;
            }
            toLabel.Add(img);
        }
        return new Plan(candidates.Count, toLabel, verified, labeled, queued);
    }

    private async Task RefreshCountsAsync(List<CameraInfo> cameras, CancellationToken ct)
    {
        foreach (var c in cameras)
        {
            if (_counts.TryGetValue(c.Name, out var existing) && existing.At > DateTimeOffset.UtcNow.AddMinutes(-2)) continue;
            if (!_counting.TryAdd(c.Name, true)) continue;   // already being counted
            try
            {
                var unverified = 0;
                await foreach (var _ in frigate.ListImagesAsync(c.Name, ImageFilter.Unverified, ct: ct)) unverified++;
                var fresh = 0;
                await foreach (var _ in frigate.ListImagesAsync(c.Name, ImageFilter.New, ct: ct)) fresh++;
                _counts[c.Name] = new CameraCounts(unverified, fresh, DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            finally { _counting.TryRemove(c.Name, out _); }
        }
    }

    private async Task<List<string>> CameraLabelsAsync(string camera, CancellationToken ct)
    {
        if (!_cameraLabels.TryGetValue(camera, out var labels))
            _cameraLabels[camera] = labels = await frigate.GetCameraLabelsAsync(camera, ct);
        return labels;
    }

    private static ImageFilter ParseFilter(string? filter) =>
        Enum.TryParse<ImageFilter>(filter, ignoreCase: true, out var f) ? f : ImageFilter.Unverified;

    // ---------- helpers ----------

    private async Task<byte[]> OriginalAsync(LabelRun run, CancellationToken ct)
    {
        var path = store.OriginalPath(run.Camera, run.ImageId);
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
        var jpeg = await frigate.DownloadImageAsync(run.ImageId, ct);   // older results didn't keep it
        await File.WriteAllBytesAsync(path, jpeg, ct);
        return jpeg;
    }

    private async Task<IResult> WithLock(string id, Func<Task<IResult>> action)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await action(); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or FrigateAuthException)
        {
            return Results.Problem(ex.Message);
        }
        finally { gate.Release(); }
    }

    private static string Status(LabelRun r) =>
        r.SubmittedAt is not null ? "submitted"
        : r.AllowedLabels.Count == 0 ? "nothing-to-do"
        : r.NeedsHuman ? "needs-input"
        : r.ReviewRounds == 0 && !r.HumanEdited ? "unreviewed"
        : "ready";

    private static int StatusRank(LabelRun r) => Status(r) switch
    {
        "needs-input" => 0, "ready" => 1, "unreviewed" => 2, "nothing-to-do" => 4, _ => 3,
    };

    private static object Summary(LabelRun r) => new
    {
        id = r.ImageId,
        camera = r.Camera,
        status = Status(r),
        objects = r.Objects.Count,
        locked = r.Locked.Count,
        labels = r.Objects.GroupBy(o => o.Label).ToDictionary(g => g.Key, g => g.Count()),
        questions = r.Issues.Count,
        cost = Math.Round(r.CostUsd, 4),
        humanEdited = r.HumanEdited,
        submittedAt = r.SubmittedAt,
        verifiedAt = r.VerifiedAt,
        submitError = r.SubmitError,
    };

    public sealed record CameraCounts(int Unverified, int New, DateTimeOffset At);
    public sealed record LabelRequest(List<string>? ImageIds, string? Camera, string? Filter, bool Relabel, bool DryRun,
        bool OneAtATime = true);
    public sealed record BoxDto(string Label, double X1, double Y1, double X2, double Y2, bool Difficult, string? Note);
    public sealed record SaveObjectsRequest(List<BoxDto> Objects);
    public sealed record AnswerDto(string Question, string Answer, bool Remember);
    public sealed record FeedbackRequest(string? Text, List<AnswerDto>? Answers);
    public sealed record ApproveRequest(bool Force, bool Verify = true);
    public sealed record NotesRequest(string? Notes);
    public sealed record AuthRequest(string? RefreshToken);
}
