using SkiaSharp;

namespace FrigateLabeler;

/// <summary>A JPEG prepared for Claude plus the mapping back to original-image pixels.</summary>
public sealed record PreparedImage(byte[] Jpeg, int Width, int Height, double ScaleToOriginal, double OffsetX, double OffsetY)
{
    /// <summary>Maps a box in this image's pixel space back to the original image.</summary>
    public PixelBox ToOriginal(PixelBox b) => new(
        OffsetX + b.X1 * ScaleToOriginal, OffsetY + b.Y1 * ScaleToOriginal,
        OffsetX + b.X2 * ScaleToOriginal, OffsetY + b.Y2 * ScaleToOriginal);

    /// <summary>Maps a box in original-image pixels into this image's pixel space.</summary>
    public PixelBox FromOriginal(PixelBox b) => new(
        (b.X1 - OffsetX) / ScaleToOriginal, (b.Y1 - OffsetY) / ScaleToOriginal,
        (b.X2 - OffsetX) / ScaleToOriginal, (b.Y2 - OffsetY) / ScaleToOriginal);
}

public static class ImageTools
{
    public static SKBitmap Decode(byte[] bytes) =>
        SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Could not decode image.");

    /// <summary>Downscales (never upscales) so the long edge is at most <paramref name="maxEdge"/>.</summary>
    public static PreparedImage PrepareFull(SKBitmap src, int maxEdge)
    {
        var scale = Math.Min(1.0, (double)maxEdge / Math.Max(src.Width, src.Height));
        return Render(src, new SKRectI(0, 0, src.Width, src.Height), scale);
    }

    /// <summary>
    /// Crops a padded region around <paramref name="box"/> and resizes it so its long edge is
    /// <paramref name="targetEdge"/> (upscaling small objects so their edges are easier to place).
    /// </summary>
    public static PreparedImage PrepareCrop(SKBitmap src, PixelBox box, int targetEdge, int minCropEdge = 96)
    {
        var pad = Math.Max(box.Width, box.Height) * 0.6 + 16;
        var cx = (box.X1 + box.X2) / 2;
        var cy = (box.Y1 + box.Y2) / 2;
        var halfW = Math.Max(box.Width / 2 + pad, minCropEdge / 2.0);
        var halfH = Math.Max(box.Height / 2 + pad, minCropEdge / 2.0);

        var rect = new SKRectI(
            (int)Math.Floor(Math.Max(0, cx - halfW)), (int)Math.Floor(Math.Max(0, cy - halfH)),
            (int)Math.Ceiling(Math.Min(src.Width, cx + halfW)), (int)Math.Ceiling(Math.Min(src.Height, cy + halfH)));

        var scale = Math.Min(4.0, (double)targetEdge / Math.Max(rect.Width, rect.Height));
        return Render(src, rect, scale);
    }

    private static PreparedImage Render(SKBitmap src, SKRectI rect, double scale,
        IReadOnlyList<(int Id, PixelBox Box)>? boxes = null)
    {
        var w = Math.Max(1, (int)Math.Round(rect.Width * scale));
        var h = Math.Max(1, (int)Math.Round(rect.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        using var image = SKImage.FromBitmap(src);
        surface.Canvas.DrawImage(image, SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height),
            SKRect.Create(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        // Use the actual ratio after rounding so mapped coordinates stay exact.
        var prepared = new PreparedImage([], w, h, (double)rect.Width / w, rect.Left, rect.Top);
        if (boxes is not null) DrawIdBoxes(surface.Canvas, prepared, boxes, w, h);
        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Jpeg, 92);
        return prepared with { Jpeg = data.ToArray() };
    }

    /// <summary>
    /// A review view: the region (whole image or a zoomed area) with every box drawn thin and tagged
    /// with its id, so Claude can judge each box against the pixels underneath it.
    /// </summary>
    public static PreparedImage RenderReviewView(SKBitmap src, SKRectI region, int maxEdge,
        IReadOnlyList<(int Id, PixelBox Box)> boxes, double maxScale = 4.0)
    {
        var scale = Math.Min(maxScale, (double)maxEdge / Math.Max(region.Width, region.Height));
        return Render(src, region, scale, boxes);
    }

    private static readonly SKColor[] Palette =
    [
        new(255, 59, 48), new(0, 199, 255), new(255, 204, 0), new(52, 199, 89),
        new(255, 45, 205), new(255, 149, 0), new(90, 120, 255), new(0, 230, 180),
    ];

    private static void DrawIdBoxes(SKCanvas canvas, PreparedImage view, IReadOnlyList<(int Id, PixelBox Box)> boxes,
        int w, int h)
    {
        using var font = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), Math.Clamp(h / 45f, 13f, 22f));
        foreach (var (id, original) in boxes)
        {
            var b = view.FromOriginal(original);
            if (b.X2 < 0 || b.Y2 < 0 || b.X1 > w || b.Y1 > h) continue;   // outside this view
            var color = Palette[(id - 1) % Palette.Length];
            using var stroke = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            canvas.DrawRect(SKRect.Create((float)b.X1, (float)b.Y1, (float)b.Width, (float)b.Height), stroke);

            // Tag sits just outside the top-left corner so it doesn't hide the object's edge.
            var tag = $"#{id}";
            var tw = font.MeasureText(tag) + 6;
            var th = font.Size + 4;
            var tx = (float)Math.Clamp(b.X1, 0, w - tw);
            var ty = (float)(b.Y1 - th >= 0 ? b.Y1 - th : Math.Min(b.Y2, h - th));
            using var bg = new SKPaint { Color = color.WithAlpha(230), Style = SKPaintStyle.Fill };
            canvas.DrawRect(SKRect.Create(tx, ty, tw, th), bg);
            using var fg = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText(tag, tx + 3, ty + font.Size, font, fg);
        }
    }

    /// <summary>Draws labeled boxes over the image for human review.</summary>
    public static byte[] RenderOverlay(SKBitmap src, IEnumerable<(PixelBox Box, string Caption, SKColor Color)> boxes,
        string? banner = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(src.Width, src.Height));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(src, 0, 0);

        var stroke = Math.Max(2f, src.Width / 640f);
        using var font = new SKFont(SKTypeface.Default, Math.Max(14f, src.Width / 80f));
        foreach (var (box, caption, color) in boxes)
        {
            using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true };
            canvas.DrawRect(SKRect.Create((float)box.X1, (float)box.Y1, (float)box.Width, (float)box.Height), paint);

            var textWidth = font.MeasureText(caption);
            var top = (float)Math.Max(0, box.Y1 - font.Size - 4);
            using var bg = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
            canvas.DrawRect(SKRect.Create((float)box.X1, top, textWidth + 8, font.Size + 4), bg);
            using var fg = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText(caption, (float)box.X1 + 4, top + font.Size, font, fg);
        }

        if (banner is not null)
        {
            using var bannerFont = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), Math.Max(16f, src.Width / 60f));
            using var red = new SKPaint { Color = new SKColor(220, 30, 30, 235), Style = SKPaintStyle.Fill };
            canvas.DrawRect(SKRect.Create(0, 0, src.Width, bannerFont.Size + 12), red);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawText(banner, 8, bannerFont.Size + 4, bannerFont, white);
        }

        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }
}
