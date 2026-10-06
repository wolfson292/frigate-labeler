using SkiaSharp;

namespace FrigateLabeler;

/// <summary>The .jpg next to each result: boxes drawn on the image for a quick look in Finder.</summary>
public static class Overlay
{
    public static byte[] Render(byte[] jpeg, LabelRun run)
    {
        using var bitmap = ImageTools.Decode(jpeg);
        var frigateBoxes = run.Original.FalsePositives.Select(d => (
            new PixelBox(d.X * run.ImageWidth, d.Y * run.ImageHeight,
                (d.X + d.W) * run.ImageWidth, (d.Y + d.H) * run.ImageHeight),
            $"frigate:{d.Label}", new SKColor(160, 160, 160)));
        var claudeBoxes = run.Objects.Select((o, i) => (
            o.Box, $"{i + 1} {o.Label}{(o.Difficult ? " (d)" : "")}",
            o.Refined || run.HumanEdited ? new SKColor(80, 220, 100) : new SKColor(255, 190, 0)));
        // Number doubts the same way the console and review page do (position in the full list).
        var doubtBoxes = run.Uncertain.Select((u, i) => (u.Region, Tag: $"?{i + 1}"))
            .Where(u => u.Region is not null)
            .Select(u => (u.Region!, u.Tag, new SKColor(255, 40, 40)));
        return ImageTools.RenderOverlay(bitmap, frigateBoxes.Concat(claudeBoxes).Concat(doubtBoxes),
            run.NeedsHuman ? "NEEDS HUMAN REVIEW — open the review page" : null);
    }
}
