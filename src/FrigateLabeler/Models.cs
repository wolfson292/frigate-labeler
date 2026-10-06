using System.Text.Json.Serialization;

namespace FrigateLabeler;

// ---- Frigate+ API shapes (observed from plus.frigate.video traffic; undocumented) ----

public sealed record ImageListResponse(
    [property: JsonPropertyName("list")] List<ImageSummary> List,
    [property: JsonPropertyName("lastImageId")] string? LastImageId);

public sealed record ImageSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("camera")] string Camera,
    [property: JsonPropertyName("verifiedLabels")] List<string> VerifiedLabels);

public sealed record CameraListResponse(
    [property: JsonPropertyName("list")] List<CameraInfo> List);

public sealed record CameraInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("numImages")] int NumImages,
    [property: JsonPropertyName("labels")] List<string> Labels);

public sealed record LabelsResponse(
    [property: JsonPropertyName("labels")] List<string> Labels);

public sealed record UserProfile(
    [property: JsonPropertyName("id")] string Id);

/// <summary>A box in Frigate+ format: normalized 0..1, (x,y) is the top-left corner.</summary>
public sealed record Annotation
{
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("w")] public double W { get; init; }
    [JsonPropertyName("h")] public double H { get; init; }
    [JsonPropertyName("difficult")] public bool Difficult { get; init; }
}

/// <summary>A detection Frigate submitted along with the image (what the web UI shows pre-filled).</summary>
public sealed record Detection
{
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("score")] public double Score { get; init; }
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("w")] public double W { get; init; }
    [JsonPropertyName("h")] public double H { get; init; }
}

public sealed record ImageData
{
    [JsonPropertyName("annotations")] public List<Annotation> Annotations { get; init; } = [];
    [JsonPropertyName("suggestions")] public List<Detection> Suggestions { get; init; } = [];
    [JsonPropertyName("falsePositives")] public List<Detection> FalsePositives { get; init; } = [];
}

public sealed record SaveImageDataRequest
{
    [JsonPropertyName("annotations")] public required List<Annotation> Annotations { get; init; }
    [JsonPropertyName("reviewedSuggestions")] public List<object> ReviewedSuggestions { get; init; } = [];
}

// ---- Local run results ----

public sealed record LabeledObject
{
    public required string Label { get; init; }
    /// <summary>Pixel box in the original image.</summary>
    public required PixelBox Box { get; init; }
    public bool Difficult { get; init; }
    public double Confidence { get; init; }
    public string? Note { get; init; }
    public bool Refined { get; init; }
}

public sealed record UncertainRegion(string Description, PixelBox? Region);

public sealed record PixelBox(double X1, double Y1, double X2, double Y2)
{
    public double Width => X2 - X1;
    public double Height => Y2 - Y1;

    public PixelBox ClampTo(int w, int h) => new(
        Math.Clamp(Math.Min(X1, X2), 0, w), Math.Clamp(Math.Min(Y1, Y2), 0, h),
        Math.Clamp(Math.Max(X1, X2), 0, w), Math.Clamp(Math.Max(Y1, Y2), 0, h));

    public PixelBox Scale(double sx, double sy) => new(X1 * sx, Y1 * sy, X2 * sx, Y2 * sy);

    /// <summary>Intersection over union with another box (0 = disjoint, 1 = identical).</summary>
    public double IoU(PixelBox o)
    {
        var iw = Math.Max(0, Math.Min(X2, o.X2) - Math.Max(X1, o.X1));
        var ih = Math.Max(0, Math.Min(Y2, o.Y2) - Math.Max(Y1, o.Y1));
        var inter = iw * ih;
        var union = Width * Height + o.Width * o.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

public sealed record LabelRun
{
    public required string ImageId { get; init; }
    public required string Camera { get; init; }
    public required int ImageWidth { get; init; }
    public required int ImageHeight { get; init; }
    /// <summary>The labels this run annotates: enabled on the camera and not yet verified on the image.</summary>
    public required List<string> AllowedLabels { get; init; }
    /// <summary>Labels already verified on the image when it was labeled; their boxes are in <see cref="Locked"/>.</summary>
    public List<string> VerifiedLabels { get; init; } = [];
    /// <summary>Existing boxes for verified labels. Kept exactly as they are and re-sent on submit.</summary>
    public List<Annotation> Locked { get; init; } = [];
    public required ImageData Original { get; init; }
    public required List<LabeledObject> Objects { get; init; }
    public string? Notes { get; init; }
    public string Model { get; init; } = "";
    public int ReviewRounds { get; init; }
    /// <summary>Problems a human should resolve. Any entry means the image is flagged.</summary>
    public List<string> Issues { get; init; } = [];
    /// <summary>Where the review's doubts are, for drawing on the overlay.</summary>
    public List<UncertainRegion> Uncertain { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public bool NeedsHuman => Issues.Count > 0;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmittedAt { get; set; }
    /// <summary>Set when the submit also verified <see cref="AllowedLabels"/> on Frigate+.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }
    public string? SubmitError { get; init; }
    /// <summary>True once a person has edited boxes on the review page.</summary>
    public bool HumanEdited { get; init; }
    /// <summary>Corrections and answers the person gave on the review page, oldest first.</summary>
    public List<string> Feedback { get; init; } = [];
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int ApiCalls { get; set; }
    public double CostUsd { get; set; }

    /// <summary>Labels Verify should mark: the ones this run covered that weren't verified before.</summary>
    public List<string> LabelsToVerify() => AllowedLabels.Except(VerifiedLabels).ToList();

    /// <summary>Everything to save on Frigate+: the locked verified boxes plus this run's boxes.</summary>
    public List<Annotation> ToAnnotations() => Locked.Concat(Objects.Select(o => new Annotation
    {
        Label = o.Label,
        X = o.Box.X1 / ImageWidth,
        Y = o.Box.Y1 / ImageHeight,
        W = o.Box.Width / ImageWidth,
        H = o.Box.Height / ImageHeight,
        Difficult = o.Difficult,
    })).ToList();
}
