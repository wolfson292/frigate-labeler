using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using SkiaSharp;

namespace FrigateLabeler;

public sealed record LabelerOptions
{
    public string Model { get; init; } = "claude-opus-5-5";
    /// <summary>Long edge (px) of the full image sent for detection and review.</summary>
    public int DetectEdge { get; init; } = 1568;
    /// <summary>Long edge (px) of each per-object crop sent for refinement.</summary>
    public int RefineEdge { get; init; } = 768;
    /// <summary>Long edge (px) of the zoomed review views.</summary>
    public int ReviewZoomEdge { get; init; } = 1024;
    public bool Refine { get; init; } = true;
    /// <summary>Maximum review rounds; 0 disables the review pass.</summary>
    public int ReviewRounds { get; init; } = 3;
    public int MaxConcurrency { get; init; } = 4;
    public required string Guidelines { get; init; }
    /// <summary>Returns the owner's notes for a camera (see <see cref="CameraNotes"/>), or null.</summary>
    public Func<string, string?> CameraNotes { get; init; } = FrigateLabeler.CameraNotes.Load;
    /// <summary>If set, the rendered review views are saved here (for checking what Claude saw).</summary>
    public string? DebugDir { get; init; }
}

/// <summary>
/// Labeling with Claude in three passes:
///   1. Detect: the whole image, the camera's allowed labels and Frigate's own detections as hints;
///      Claude returns every object with an approximate pixel box.
///   2. Refine: each object is cropped with padding and upscaled; Claude returns a tight box in
///      crop space, which is mapped back to the original image. This is where precision comes from.
///   3. Review: Claude sees the image (plus zoomed views of small objects) with every box drawn and
///      numbered, and keeps / deletes / relabels / re-fits each one and reports missed objects.
///      Re-fits and additions go back through Refine; the review repeats until it makes no changes.
///      Anything ambiguous, or still changing after the last round, flags the image for a human.
/// </summary>
public sealed class ClaudeLabeler(AnthropicClient client, LabelerOptions options)
{
    private readonly SemaphoreSlim _gate = new(options.MaxConcurrency);

    /// <summary>
    /// Labels an image for every label enabled on its camera. Earlier verification isn't assumed to be
    /// right: existing boxes (verified or not) are strong hints that get checked like everything else.
    /// Any change to a label that was verified becomes a question for the person, so earlier ground
    /// truth never changes without their sign-off.
    /// </summary>
    public async Task<LabelRun> LabelAsync(ImageSummary image, byte[] jpeg, ImageData data, List<string> cameraLabels,
        CancellationToken ct)
    {
        var verified = image.VerifiedLabels.Intersect(cameraLabels).ToList();
        var labels = cameraLabels.ToList();

        using var bitmap = ImageTools.Decode(jpeg);
        var full = ImageTools.PrepareFull(bitmap, options.DetectEdge);
        var usage = new Usage(ModelPrice.For(options.Model));
        var issues = new List<string>();
        var uncertain = new List<UncertainRegion>();

        var detection = await DetectAsync(image.Camera, full, data, labels, verified, bitmap.Width, bitmap.Height, usage, ct);

        var objects = detection.Objects
            .Select(o => new LabeledObject
            {
                Label = o.Label,
                Box = full.ToOriginal(new PixelBox(o.X1, o.Y1, o.X2, o.Y2)).ClampTo(bitmap.Width, bitmap.Height),
                Confidence = o.Confidence,
                Difficult = o.Difficult,
                Note = string.IsNullOrWhiteSpace(o.Note) ? null : o.Note,
            })
            .Where(o => o.Box.Width >= 2 && o.Box.Height >= 2)
            .ToList();

        if (options.Refine && objects.Count > 0)
            objects = await RefineAllAsync(bitmap, objects.Select(o => (o, (string?)null)), labels, usage, issues, ct, image.Camera);

        int rounds;
        (objects, rounds) = await ReviewLoopAsync(image.Camera, bitmap, objects, labels, null, usage, issues, uncertain, ct);
        uncertain.AddRange(VerifiedChanges(data.Annotations, verified, objects, bitmap.Width, bitmap.Height));

        return new LabelRun
        {
            ImageId = image.Id,
            Camera = image.Camera,
            ImageWidth = bitmap.Width,
            ImageHeight = bitmap.Height,
            AllowedLabels = labels,
            VerifiedLabels = verified,
            Original = data,
            Objects = objects,
            Notes = detection.Notes,
            Model = options.Model,
            ReviewRounds = rounds,
            Issues = issues.Concat(uncertain.Select(u => u.Description)).Distinct().ToList(),
            Uncertain = uncertain,
            InputTokens = usage.Input,
            OutputTokens = usage.Output,
            ApiCalls = usage.Calls,
            CostUsd = usage.CostUsd,
        };
    }

    /// <summary>
    /// Applies a person's corrections from the review page: their text is authoritative. The review
    /// loop runs with it until stable, re-fitting and adding boxes through the refine pass.
    /// </summary>
    public async Task<LabelRun> ReviseAsync(LabelRun run, byte[] jpeg, string feedback, CancellationToken ct)
    {
        using var bitmap = ImageTools.Decode(jpeg);
        var usage = new Usage(ModelPrice.For(options.Model));
        var issues = new List<string>();
        var uncertain = new List<UncertainRegion>();
        var allFeedback = run.Feedback.Append(feedback.Trim()).ToList();

        // The person can correct anything, so every camera label is available here. (Runs from the
        // version that locked verified boxes still carry them; those are passed along as context.)
        var (objects, rounds) = await ReviewLoopAsync(run.Camera, bitmap, run.Objects.ToList(),
            run.AllowedLabels.Concat(run.VerifiedLabels).Distinct().ToList(),
            string.Join("\n\n", allFeedback), usage, issues, uncertain, ct, minRounds: 1,
            verified: run.Locked.Count > 0 ? new VerifiedContext(run.VerifiedLabels, run.Locked) : null);
        if (run.Locked.Count == 0)
            uncertain.AddRange(VerifiedChanges(run.Original.Annotations, run.VerifiedLabels, objects, bitmap.Width,
                bitmap.Height, allFeedback));

        // Never ask again about something the person already answered (same wording or same spot).
        uncertain.RemoveAll(u => run.Answered.Any(a => a.Description == u.Description
            || (a.Region is not null && u.Region is not null && a.Region.IoU(u.Region) > 0.3)));

        return run with
        {
            Objects = objects,
            Feedback = allFeedback,
            Issues = issues.Concat(uncertain.Select(u => u.Description)).Distinct().ToList(),
            Uncertain = uncertain,
            ReviewRounds = run.ReviewRounds + rounds,
            InputTokens = run.InputTokens + usage.Input,
            OutputTokens = run.OutputTokens + usage.Output,
            ApiCalls = run.ApiCalls + usage.Calls,
            CostUsd = run.CostUsd + usage.CostUsd,
        };
    }

    private async Task<(List<LabeledObject> Objects, int Rounds)> ReviewLoopAsync(string camera, SKBitmap bitmap,
        List<LabeledObject> objects, List<string> labels, string? guidance, Usage usage, List<string> issues,
        List<UncertainRegion> uncertain, CancellationToken ct, int minRounds = 0, VerifiedContext? verified = null)
    {
        var maxRounds = Math.Max(options.ReviewRounds, minRounds);
        var rounds = 0;
        var settled = false;
        while (!settled && rounds < maxRounds)
        {
            rounds++;
            // Doubts from earlier rounds may have been resolved by this round's changes; keep only the latest.
            uncertain.Clear();
            (objects, settled) = await ReviewRoundAsync(camera, bitmap, objects, labels, guidance, usage, issues,
                uncertain, ct, verified);
        }
        if (!settled && rounds > 0)
            issues.Add($"review was still changing boxes after {rounds} rounds");
        return (objects, rounds);
    }

    /// <summary>
    /// Compares the final boxes with what a person verified earlier and turns every real difference
    /// (a verified box removed or relabeled, or a new box for a verified label) into a question, so
    /// changes to earlier ground truth are always confirmed by a person. Small tightening isn't flagged.
    /// Differences the person already asked for in <paramref name="feedback"/> are reported once, as
    /// confirmations of their request.
    /// </summary>
    private static List<UncertainRegion> VerifiedChanges(List<Annotation> before, List<string> verifiedLabels,
        List<LabeledObject> after, int w, int h, List<string>? feedback = null)
    {
        static PixelBox ToBox(Annotation a, int w, int h) => new(a.X * w, a.Y * h, (a.X + a.W) * w, (a.Y + a.H) * h);
        string Where(PixelBox b) => $"around x {b.X1:F0}–{b.X2:F0}, y {b.Y1:F0}–{b.Y2:F0}";
        var asked = feedback is { Count: > 0 } ? " (you asked for this; approve to confirm)" : "";

        var changes = new List<UncertainRegion>();
        var oldBoxes = before.Where(a => verifiedLabels.Contains(a.Label)).Select(a => (a.Label, Box: ToBox(a, w, h))).ToList();
        var newBoxes = after.Where(o => verifiedLabels.Contains(o.Label)).ToList();

        foreach (var (label, box) in oldBoxes)
        {
            if (newBoxes.Any(o => o.Label == label && o.Box.IoU(box) > 0.5)) continue;
            var relabeled = after.FirstOrDefault(o => o.Box.IoU(box) > 0.5);
            changes.Add(new UncertainRegion(relabeled is not null
                ? $"Previously verified {label} {Where(box)} is now labeled {relabeled.Label}. Is that right?{asked}"
                : $"Previously verified {label} {Where(box)} was removed. Is it really not a {label}?{asked}", box));
        }
        foreach (var o in newBoxes)
            if (!oldBoxes.Any(b => b.Label == o.Label && b.Box.IoU(o.Box) > 0.5))
                changes.Add(new UncertainRegion(
                    $"New {o.Label} {Where(o.Box)} that the earlier verification didn't have. Is it a {o.Label}?{asked}", o.Box));
        return changes;
    }

    /// <summary>Labels a person already verified on this image, with their boxes, for the review prompt.</summary>
    private static string VerifiedSection(VerifiedContext? v, int w, int h, bool hasGuidance)
    {
        if (v is null || v.Labels.Count == 0) return "";
        var boxes = v.Boxes.Count == 0
            ? "  (no boxes: none of those objects were in the image when it was verified)"
            : string.Join("\n", v.Boxes.Select(a =>
                $"  - {a.Label} at [{a.X * w:F0}, {a.Y * h:F0}, {(a.X + a.W) * w:F0}, {(a.Y + a.H) * h:F0}] (not drawn)"));
        var rule = hasGuidance
            ? "Only add or change boxes for these labels when the person's feedback asks for it."
            : "Don't box objects of these labels yourself. If you see one of them with no verified box above " +
              "(for example a second car), list it in `uncertain` so the person can add it.";
        return $"""

            These labels were already verified by a person on this image: {string.Join(", ", v.Labels)}.
            Their verified boxes (original-image pixels):
            {boxes}
            {rule}

            """;
    }

    private string CameraNotesSection(string camera) =>
        options.CameraNotes(camera) is { } notes
            ? $"""

               Notes from the owner of camera "{camera}". These are authoritative; follow them:
               {notes}

               """
            : "";

    // ---------------- Pass 1: detection ----------------

    private async Task<DetectResult> DetectAsync(string camera, PreparedImage full, ImageData data, List<string> labels,
        List<string> verifiedLabels, int originalW, int originalH, Usage usage, CancellationToken ct)
    {
        var hints = new StringBuilder();
        void AddHints(string title, IEnumerable<(string Label, double X, double Y, double W, double H, string Extra)> boxes)
        {
            var list = boxes.ToList();
            if (list.Count == 0) return;
            hints.AppendLine(title);
            foreach (var b in list)
            {
                var p = full.FromOriginal(new PixelBox(b.X * originalW, b.Y * originalH,
                    (b.X + b.W) * originalW, (b.Y + b.H) * originalH));
                hints.AppendLine($"- {b.Label} [{p.X1:F0}, {p.Y1:F0}, {p.X2:F0}, {p.Y2:F0}]{b.Extra}");
            }
            hints.AppendLine();
        }

        AddHints("Boxes Frigate's current detector reported (may be wrong labels, loose, or false positives):",
            data.FalsePositives.Concat(data.Suggestions).Where(d => labels.Contains(d.Label))
                .Select(d => (d.Label, d.X, d.Y, d.W, d.H, $" score {d.Score:F2}")));
        AddHints("Boxes a person verified earlier (usually right, but people miss objects and draw loose boxes: " +
                 "check them like any other box and return the corrected set):",
            data.Annotations.Where(a => verifiedLabels.Contains(a.Label))
                .Select(a => (a.Label, a.X, a.Y, a.W, a.H, a.Difficult ? " (difficult)" : "")));
        AddHints("Other boxes already saved on this image (an earlier pass may have drawn these; keep, fix, or drop them):",
            data.Annotations.Where(a => !verifiedLabels.Contains(a.Label))
                .Select(a => (a.Label, a.X, a.Y, a.W, a.H, a.Difficult ? " (difficult)" : "")));
        if (verifiedLabels.Count > 0 && !data.Annotations.Any(a => verifiedLabels.Contains(a.Label)))
            hints.AppendLine($"A person verified earlier that there are no {string.Join(", ", verifiedLabels)} " +
                             "in this image. That may be wrong; check.\n");

        var prompt = $"""
            Camera: {camera}
            Image size: {full.Width} x {full.Height} pixels. Give all coordinates in this pixel space,
            as [x1, y1, x2, y2] with (x1, y1) the top-left corner.

            Labels to find: {string.Join(", ", labels)}
            {CameraNotesSection(camera)}
            {(hints.Length > 0 ? hints.ToString() : "Frigate reported no detections for this image.\n")}
            Find every instance of every allowed label in the image and return the complete final list.
            The hints above are only hints: verify each one against the image, correct its label or box,
            drop it if it is not a real object, and add anything they missed. Scan the whole frame,
            including the edges, the background, and dark areas, before answering.

            Use `note` for anything a human reviewer should double-check. Use `notes` for a one-line
            summary of the scene.
            """;

        var schema = Schema(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "objects", "notes" },
            properties = new
            {
                notes = new { type = "string" },
                objects = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "label", "x1", "y1", "x2", "y2", "confidence", "difficult", "note" },
                        properties = new
                        {
                            label = new { type = "string", @enum = labels },
                            x1 = new { type = "integer" },
                            y1 = new { type = "integer" },
                            x2 = new { type = "integer" },
                            y2 = new { type = "integer" },
                            confidence = new { type = "number" },
                            difficult = new { type = "boolean" },
                            note = new { type = "string" },
                        },
                    },
                },
            },
        });

        return await CallAsync<DetectResult>([(null, full.Jpeg)], prompt, schema, Effort.High, usage, ct);
    }

    // ---------------- Pass 2: per-object refinement ----------------

    /// <summary>
    /// Refines every object in parallel, folds in objects split out of over-wide boxes, and drops
    /// duplicates. <paramref name="items"/> pairs each object with an optional reviewer hint.
    /// </summary>
    private async Task<List<LabeledObject>> RefineAllAsync(SKBitmap bitmap,
        IEnumerable<(LabeledObject Obj, string? Hint)> items, List<string> labels, Usage usage, List<string> issues,
        CancellationToken ct, string? camera = null)
    {
        var refined = await Task.WhenAll(items.Select(i => RefineAsync(bitmap, i.Obj, i.Hint, labels, usage, issues, ct, camera)));
        var objects = refined.Select(r => r.Main).OfType<LabeledObject>().ToList();

        // Several crops can see the same neighbour, so keep a split-out object only if it's new.
        foreach (var extra in refined.SelectMany(r => r.Split))
            if (!objects.Any(o => o.Label == extra.Label && o.Box.IoU(extra.Box) > 0.4))
                objects.Add(extra);

        return RemoveDuplicates(objects);
    }

    private async Task<(LabeledObject? Main, List<LabeledObject> Split)> RefineAsync(SKBitmap bitmap, LabeledObject obj,
        string? hint, List<string> labels, Usage usage, List<string> issues, CancellationToken ct, string? camera = null)
    {
        var crop = ImageTools.PrepareCrop(bitmap, obj.Box, options.RefineEdge);
        var approx = crop.FromOriginal(obj.Box);

        var prompt = $"""
            This is a zoomed-in crop ({crop.Width} x {crop.Height} pixels) from a security camera image.
            An earlier pass found a `{obj.Label}` at approximately [{approx.X1:F0}, {approx.Y1:F0}, {approx.X2:F0}, {approx.Y2:F0}]
            in this crop's pixel space (x1, y1, x2, y2; top-left origin). That box may be loose, shifted, or clipped.
            {(hint is null ? "" : $"\nA reviewer looked at this box and said: {hint}\n")}{(camera is null ? "" : CameraNotesSection(camera))}
            Return a tight box for that one object in this crop's pixel space: each edge on the outermost
            visible pixel of the object, following the annotation guidelines. If other objects appear in
            the crop, ignore them; box only the one near the approximate position.

            Also confirm the label (allowed: {string.Join(", ", labels)}) and whether it is `difficult`.
            Set `present` to false only if there is clearly no such object there (a false positive).

            If the approximate box actually covered more than one separate object (for example two bins
            side by side), box the main one above and give each of the others a tight box in
            `split_objects`. Otherwise leave `split_objects` empty.
            """;

        var schema = Schema(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "present", "label", "x1", "y1", "x2", "y2", "difficult", "note", "split_objects" },
            properties = new
            {
                present = new { type = "boolean" },
                split_objects = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "label", "x1", "y1", "x2", "y2", "difficult" },
                        properties = new
                        {
                            label = new { type = "string", @enum = labels },
                            x1 = new { type = "integer" },
                            y1 = new { type = "integer" },
                            x2 = new { type = "integer" },
                            y2 = new { type = "integer" },
                            difficult = new { type = "boolean" },
                        },
                    },
                },
                label = new { type = "string", @enum = labels },
                x1 = new { type = "integer" },
                y1 = new { type = "integer" },
                x2 = new { type = "integer" },
                y2 = new { type = "integer" },
                difficult = new { type = "boolean" },
                note = new { type = "string" },
            },
        });

        RefineResult r;
        try
        {
            r = await CallAsync<RefineResult>([(null, crop.Jpeg)], prompt, schema, Effort.Medium, usage, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"    refine failed for {obj.Label}, keeping the unrefined box: {ex.Message}");
            lock (issues) issues.Add($"a {obj.Label} box could not be refined");
            return (obj, []);
        }

        var split = (r.SplitObjects ?? [])
            .Select(s => new LabeledObject
            {
                Label = s.Label,
                Box = crop.ToOriginal(new PixelBox(s.X1, s.Y1, s.X2, s.Y2)).ClampTo(bitmap.Width, bitmap.Height),
                Difficult = s.Difficult,
                Confidence = obj.Confidence,
                Note = "split from a box that covered several objects",
                Refined = true,
            })
            .Where(o => o.Box.Width >= 2 && o.Box.Height >= 2)
            .ToList();

        if (!r.Present)
        {
            // A reviewer asked for this box/object but the close-up says there's nothing: a human decides.
            if (hint is not null)
                lock (issues) issues.Add($"reviewer and close-up disagree about a {obj.Label} near " +
                                         $"[{obj.Box.X1:F0},{obj.Box.Y1:F0}] ({hint})");
            return (null, split);
        }

        var box = crop.ToOriginal(new PixelBox(r.X1, r.Y1, r.X2, r.Y2)).ClampTo(bitmap.Width, bitmap.Height);
        if (box.Width < 2 || box.Height < 2) return (obj, split);

        var note = string.Join("; ", new[] { obj.Note, r.Note }.Where(n => !string.IsNullOrWhiteSpace(n)));
        return (obj with
        {
            Label = r.Label,
            Box = box,
            Difficult = r.Difficult,
            Note = note.Length == 0 ? null : note,
            Refined = true,
        }, split);
    }

    // ---------------- Pass 3: review ----------------

    private async Task<(List<LabeledObject> Objects, bool Settled)> ReviewRoundAsync(string camera, SKBitmap bitmap,
        List<LabeledObject> objects, List<string> labels, string? guidance, Usage usage, List<string> issues,
        List<UncertainRegion> uncertain, CancellationToken ct, VerifiedContext? verified = null)
    {
        var numbered = objects.Select((o, i) => (Id: i + 1, Box: o.Box)).ToList();
        var views = new List<PreparedImage>
        {
            ImageTools.RenderReviewView(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height), options.DetectEdge,
                numbered, maxScale: 1.0),
        };
        views.AddRange(ZoomRegions(bitmap, objects)
            .Select(region => ImageTools.RenderReviewView(bitmap, region, options.ReviewZoomEdge, numbered)));

        if (options.DebugDir is not null)
        {
            Directory.CreateDirectory(options.DebugDir);
            var stamp = DateTime.Now.ToString("HHmmssfff");
            for (var i = 0; i < views.Count; i++)
                await File.WriteAllBytesAsync(Path.Combine(options.DebugDir, $"review-{stamp}-view{i + 1}.jpg"), views[i].Jpeg, ct);
        }

        var boxList = objects.Count == 0
            ? "(no boxes)"
            : string.Join("\n", objects.Select((o, i) =>
                $"#{i + 1} {o.Label}{(o.Difficult ? " (difficult)" : "")} at [{o.Box.X1:F0}, {o.Box.Y1:F0}, {o.Box.X2:F0}, {o.Box.Y2:F0}] in original-image pixels" +
                (o.Human ? " — drawn by the person: always `keep`; mention any concern in `uncertain`" : "")));

        var viewList = string.Join("\n", views.Select((v, i) => i == 0
            ? $"View 1: the whole image, {v.Width} x {v.Height} px."
            : $"View {i + 1}: an enlarged region, {v.Width} x {v.Height} px."));

        var prompt = $"""
            You are the final quality check on bounding-box labels for camera "{camera}" before they are
            used as training data. Errors here train the model to make mistakes, so be strict.
            Allowed labels: {string.Join(", ", labels)}
            {CameraNotesSection(camera)}{(guidance is null ? "" : $"""

            The person who owns this camera reviewed these labels and wrote the following. Their words
            are authoritative and override your own judgement: apply every correction they ask for,
            using the verdicts and `missing` below. Don't list anything they have already answered in
            `uncertain`, and don't undo their decisions.
            ---
            {guidance}
            ---

            """)}
            {VerifiedSection(verified, bitmap.Width, bitmap.Height, guidance is not null)}
            {viewList}
            Each box is drawn as a thin colored rectangle with its id tag (#n) at its top-left corner.
            The same id is the same box in every view.

            Current boxes:
            {boxList}

            For every box, give exactly one verdict:
            - keep: a real object, correct label, tight box, not a duplicate.
            - delete: not a real instance of an allowed label (shadow, reflection, foliage, a part of
              another object), or a duplicate of another box on the same object.
            - relabel: a real object with the wrong label; give the right one in `label`.
            - refit: the right object and label, but the box is loose, clipped, shifted, or covers more
              than one object. Say exactly what's wrong in `reason`.
            Also set `difficult` for each box per the guidelines.

            Then list every object of an allowed label that has no box in `missing`. Give its `view`
            number and its box in that view's pixel space.

            Finally, list in `uncertain` anything you can't settle from the pixels and a human should
            decide: an identity you can't determine, whether something is one object or two, and so on.
            Give each one's region (view number and box in that view's pixel space) and a short
            description that doesn't mention views or coordinates. List each thing only once.
            Do not guess: an uncertain item belongs in `uncertain`, not as a confident verdict.
            """;

        var schema = Schema(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "boxes", "missing", "uncertain" },
            properties = new
            {
                boxes = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "id", "verdict", "label", "difficult", "reason" },
                        properties = new
                        {
                            id = new { type = "integer" },
                            verdict = new { type = "string", @enum = new[] { "keep", "delete", "relabel", "refit" } },
                            label = new { type = "string", @enum = labels },
                            difficult = new { type = "boolean" },
                            reason = new { type = "string" },
                        },
                    },
                },
                missing = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "view", "label", "x1", "y1", "x2", "y2", "difficult", "reason" },
                        properties = new
                        {
                            view = new { type = "integer" },
                            label = new { type = "string", @enum = labels },
                            x1 = new { type = "integer" },
                            y1 = new { type = "integer" },
                            x2 = new { type = "integer" },
                            y2 = new { type = "integer" },
                            difficult = new { type = "boolean" },
                            reason = new { type = "string" },
                        },
                    },
                },
                uncertain = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "view", "x1", "y1", "x2", "y2", "description" },
                        properties = new
                        {
                            view = new { type = "integer" },
                            x1 = new { type = "integer" },
                            y1 = new { type = "integer" },
                            x2 = new { type = "integer" },
                            y2 = new { type = "integer" },
                            description = new { type = "string" },
                        },
                    },
                },
            },
        });

        ReviewResult review;
        try
        {
            review = await CallAsync<ReviewResult>(views.Select(v => ((string?)null, v.Jpeg)).ToList(), prompt, schema,
                Effort.High, usage, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Add($"review failed: {ex.Message}");
            return (objects, true);
        }

        foreach (var u in review.Uncertain.Where(u => !string.IsNullOrWhiteSpace(u.Description)))
        {
            var region = u.View >= 1 && u.View <= views.Count
                ? views[u.View - 1].ToOriginal(new PixelBox(u.X1, u.Y1, u.X2, u.Y2)).ClampTo(bitmap.Width, bitmap.Height)
                : null;
            // The same doubt often comes back in later rounds; keep one entry per place.
            if (region is not null && uncertain.Any(x => x.Region is not null && x.Region.IoU(region) > 0.3)) continue;
            uncertain.Add(new UncertainRegion(u.Description.Trim(), region));
        }

        var verdicts = review.Boxes.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var kept = new List<LabeledObject>();
        var toRefine = new List<(LabeledObject, string?)>();
        var changes = 0;

        for (var i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            // Not mentioned, or drawn/adjusted by the person: leave exactly as is.
            if (obj.Human || !verdicts.TryGetValue(i + 1, out var v))
            {
                kept.Add(obj);
                continue;
            }

            if (v.Difficult != obj.Difficult && v.Verdict == "keep")
            {
                obj = obj with { Difficult = v.Difficult };
                changes++;
            }

            switch (v.Verdict)
            {
                case "keep":
                    kept.Add(obj);
                    break;
                case "delete":
                    changes++;
                    break;
                case "relabel":
                    changes++;
                    kept.Add(obj with { Label = v.Label, Difficult = v.Difficult, Note = AppendNote(obj.Note, v.Reason) });
                    break;
                case "refit":
                    changes++;
                    toRefine.Add((obj with { Label = v.Label, Difficult = v.Difficult }, v.Reason));
                    break;
            }
        }

        foreach (var m in review.Missing)
        {
            if (m.View < 1 || m.View > views.Count) continue;
            var box = views[m.View - 1].ToOriginal(new PixelBox(m.X1, m.Y1, m.X2, m.Y2)).ClampTo(bitmap.Width, bitmap.Height);
            if (box.Width < 2 || box.Height < 2) continue;
            changes++;
            toRefine.Add((new LabeledObject
            {
                Label = m.Label,
                Box = box,
                Difficult = m.Difficult,
                Confidence = 0.5,
                Note = $"added by review: {m.Reason}",
            }, $"this {m.Label} was missed by earlier passes ({m.Reason})"));
        }

        if (changes == 0) return (objects, true);

        if (toRefine.Count > 0)
            kept.AddRange(await RefineAllAsync(bitmap, toRefine, labels, usage, issues, ct, camera));
        return (RemoveDuplicates(kept), false);
    }

    /// <summary>
    /// Regions worth an enlarged review view: clusters of small objects, where a wrong or duplicate
    /// box is hard to see at full-image scale. At most 6, smallest objects first.
    /// </summary>
    private static List<SKRectI> ZoomRegions(SKBitmap bitmap, List<LabeledObject> objects)
    {
        const double smallEdge = 220;   // objects smaller than this (px, longest side) get a zoom view
        var rects = objects
            .Where(o => Math.Max(o.Box.Width, o.Box.Height) < smallEdge)
            .OrderBy(o => o.Box.Width * o.Box.Height)
            .Select(o =>
            {
                var pad = Math.Max(o.Box.Width, o.Box.Height) * 0.8 + 40;
                return new SKRect((float)(o.Box.X1 - pad), (float)(o.Box.Y1 - pad),
                    (float)(o.Box.X2 + pad), (float)(o.Box.Y2 + pad));
            })
            .ToList();

        // Merge overlapping regions so a cluster of objects shares one view.
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < rects.Count && !merged; i++)
            for (var j = i + 1; j < rects.Count && !merged; j++)
            {
                if (!rects[i].IntersectsWith(rects[j])) continue;
                rects[i] = SKRect.Union(rects[i], rects[j]);
                rects.RemoveAt(j);
                merged = true;
            }
        }

        return rects.Take(6).Select(r =>
        {
            // At least 240 px each way so there is context around the objects.
            var cx = r.MidX;
            var cy = r.MidY;
            var hw = Math.Max(r.Width, 240) / 2;
            var hh = Math.Max(r.Height, 240) / 2;
            return new SKRectI(
                (int)Math.Max(0, cx - hw), (int)Math.Max(0, cy - hh),
                (int)Math.Min(bitmap.Width, cx + hw), (int)Math.Min(bitmap.Height, cy + hh));
        }).ToList();
    }

    private static string? AppendNote(string? note, string? extra) =>
        string.IsNullOrWhiteSpace(extra) ? note : string.IsNullOrWhiteSpace(note) ? extra : $"{note}; {extra}";

    // ---------------- Claude call ----------------

    private async Task<T> CallAsync<T>(IReadOnlyList<(string? Caption, byte[] Jpeg)> images, string prompt,
        Dictionary<string, JsonElement> schema, Effort effort, Usage usage, CancellationToken ct)
    {
        var content = new List<BetaContentBlockParam>();
        foreach (var (caption, jpeg) in images)
        {
            if (images.Count > 1)
                content.Add(new BetaTextBlockParam { Text = caption ?? $"View {content.Count / 2 + 1}:" });
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(jpeg), MediaType = MediaType.ImageJpeg },
            });
        }
        content.Add(new BetaTextBlockParam { Text = prompt });

        await _gate.WaitAsync(ct);
        try
        {
            var response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = 16000,
                // Server-side fallback: if a request is declined by a safety classifier, the API
                // re-serves it on a fallback model chosen by the API, inside the same call.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                System = new List<BetaTextBlockParam>
                {
                    new() { Text = options.Guidelines, CacheControl = new BetaCacheControlEphemeral() },
                },
                OutputConfig = new BetaOutputConfig
                {
                    Effort = effort,
                    Format = new BetaJsonOutputFormat { Schema = schema },
                },
                Messages = [new BetaMessageParam { Role = Role.User, Content = content }],
            }, cancellationToken: ct);

            usage.Add(response.Usage);

            if (response.StopReason == "refusal")
                throw new InvalidOperationException($"Claude declined the request ({response.StopDetails?.Category}).");
            if (response.StopReason == "max_tokens")
                throw new InvalidOperationException("Claude's response was cut off (max_tokens).");

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
            return JsonSerializer.Deserialize<T>(text, AppPaths.Json)
                   ?? throw new InvalidOperationException("Empty response from Claude.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<LabeledObject> RemoveDuplicates(List<LabeledObject> objects)
    {
        var kept = new List<LabeledObject>();
        foreach (var o in objects.OrderByDescending(o => o.Confidence))
            if (!kept.Any(k => k.Label == o.Label && k.Box.IoU(o.Box) > 0.6))
                kept.Add(o);
        // Left-to-right order keeps overlay numbering stable and easy to read.
        return kept.OrderBy(o => o.Box.X1).ToList();
    }

    private static Dictionary<string, JsonElement> Schema(object schema) =>
        JsonSerializer.SerializeToElement(schema).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>Token and dollar totals for one image, summed across its (parallel) API calls.</summary>
    private sealed class Usage(ModelPrice price)
    {
        private readonly object _lock = new();
        public long Input { get; private set; }
        public long Output { get; private set; }
        public int Calls { get; private set; }
        public double CostUsd { get; private set; }

        public void Add(BetaUsage u)
        {
            long cacheRead = u.CacheReadInputTokens ?? 0, cacheWrite = u.CacheCreationInputTokens ?? 0;
            lock (_lock)
            {
                Input += u.InputTokens + cacheRead + cacheWrite;
                Output += u.OutputTokens;
                Calls++;
                CostUsd += price.Cost(u.InputTokens, u.OutputTokens, cacheRead, cacheWrite);
            }
        }
    }

    private sealed record VerifiedContext(List<string> Labels, List<Annotation> Boxes);

    private sealed record DetectResult(List<DetectedObject> Objects, string? Notes);

    private sealed record DetectedObject(string Label, int X1, int Y1, int X2, int Y2, double Confidence,
        bool Difficult, string? Note);

    private sealed record RefineResult(bool Present, string Label, int X1, int Y1, int X2, int Y2, bool Difficult,
        string? Note, [property: JsonPropertyName("split_objects")] List<SplitObject>? SplitObjects);

    private sealed record SplitObject(string Label, int X1, int Y1, int X2, int Y2, bool Difficult);

    private sealed record ReviewResult(List<BoxVerdict> Boxes, List<MissingObject> Missing, List<UncertainItem> Uncertain);

    private sealed record UncertainItem(int View, int X1, int Y1, int X2, int Y2, string Description);

    private sealed record BoxVerdict(int Id, string Verdict, string Label, bool Difficult, string? Reason);

    private sealed record MissingObject(int View, string Label, int X1, int Y1, int X2, int Y2, bool Difficult,
        string? Reason);
}
