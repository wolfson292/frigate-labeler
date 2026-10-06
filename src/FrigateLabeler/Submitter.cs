using System.Text.Json;

namespace FrigateLabeler;

/// <summary>
/// Uploads one result to Frigate+ the way the editor does: Save writes the boxes (locked verified
/// boxes plus this run's), and Verify then marks the labels this run covered as verified.
/// </summary>
public static class Submitter
{
    public static async Task<(bool Ok, string Message)> SubmitAsync(FrigateClient frigate, RunStore store, LabelRun run,
        bool force, bool verify, CancellationToken ct)
    {
        try
        {
            // Don't clobber edits made on the website after we labeled the image.
            var current = await frigate.GetImageDataAsync(run.ImageId, ct);
            var expected = run.Locked.Concat(run.Original.Annotations).ToList();
            if (!force && !SameBoxes(current.Annotations, expected))
                return (false, "The boxes on Frigate+ changed since this image was labeled. " +
                               "Submit with force to overwrite them, or re-label it.");

            await frigate.SaveAnnotationsAsync(run.ImageId, run.ToAnnotations(), current.FalsePositives.Count > 0, ct);
            var saved = run with { SubmittedAt = DateTimeOffset.UtcNow, SubmitError = null };
            store.Save(saved);

            var toVerify = run.LabelsToVerify();
            if (!verify)
                return (true, $"Saved {run.ToAnnotations().Count} box(es) to Frigate+ (not verified).");
            if (toVerify.Count == 0)
                return (true, $"Saved {run.ToAnnotations().Count} box(es); every label was already verified.");

            await frigate.VerifyAsync(run.ImageId, toVerify, ct);
            store.Save(saved with { VerifiedAt = DateTimeOffset.UtcNow });
            return (true, $"Saved {run.ToAnnotations().Count} box(es) and verified {toVerify.Count} label(s).");
        }
        catch (Exception ex) when (ex is HttpRequestException or FrigateAuthException)
        {
            store.Save(run with { SubmitError = ex.Message });
            return (false, ex.Message);
        }
    }

    private static bool SameBoxes(List<Annotation> a, List<Annotation> b)
    {
        static string Key(Annotation x) => $"{x.Label}|{x.X:F4}|{x.Y:F4}|{x.W:F4}|{x.H:F4}|{x.Difficult}";
        return a.Select(Key).Order().SequenceEqual(b.Select(Key).Order());
    }
}
