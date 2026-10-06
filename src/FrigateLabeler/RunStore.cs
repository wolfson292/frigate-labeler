using System.Text.Json;

namespace FrigateLabeler;

/// <summary>
/// The local results folder: runs/&lt;camera&gt;/&lt;imageId&gt;.json (the labels),
/// .jpg (overlay for quick viewing) and .orig.jpg (the untouched image, for the review page).
/// </summary>
public sealed class RunStore(string root)
{
    private readonly object _lock = new();

    public string Root { get; } = Path.GetFullPath(root);

    public string JsonPath(string camera, string imageId) => Path.Combine(Root, camera, imageId + ".json");
    public string OverlayPath(string camera, string imageId) => Path.Combine(Root, camera, imageId + ".jpg");
    public string OriginalPath(string camera, string imageId) => Path.Combine(Root, camera, imageId + ".orig.jpg");

    public bool Exists(string camera, string imageId) => File.Exists(JsonPath(camera, imageId));

    public IEnumerable<string> AllJsonFiles() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories)
            : [];

    public List<LabelRun> LoadAll() =>
        AllJsonFiles().Select(TryLoad).OfType<LabelRun>().ToList();

    public LabelRun? Find(string imageId) =>
        AllJsonFiles().Where(p => Path.GetFileNameWithoutExtension(p) == imageId).Select(TryLoad).FirstOrDefault();

    public void Save(LabelRun run, byte[]? originalJpeg = null)
    {
        lock (_lock)
        {
            var json = JsonPath(run.Camera, run.ImageId);
            Directory.CreateDirectory(Path.GetDirectoryName(json)!);
            if (originalJpeg is not null) File.WriteAllBytes(OriginalPath(run.Camera, run.ImageId), originalJpeg);
            File.WriteAllText(json, JsonSerializer.Serialize(run, AppPaths.Json));

            var original = originalJpeg ?? (File.Exists(OriginalPath(run.Camera, run.ImageId))
                ? File.ReadAllBytes(OriginalPath(run.Camera, run.ImageId))
                : null);
            if (original is not null)
                File.WriteAllBytes(OverlayPath(run.Camera, run.ImageId), Overlay.Render(original, run));
        }
    }

    private static LabelRun? TryLoad(string path)
    {
        try { return JsonSerializer.Deserialize<LabelRun>(File.ReadAllText(path), AppPaths.Json); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}

/// <summary>
/// Free-text notes about a camera ("the white thing by the far house is a parked golf cart; don't
/// label it"). They are passed to Claude in every prompt for that camera as instructions from the
/// owner, so recurring ambiguities get settled once instead of on every image.
/// </summary>
public static class CameraNotes
{
    public static readonly string Dir = Path.Combine(AppPaths.ConfigDir, "cameras");

    public static string PathFor(string camera) =>
        Path.Combine(Dir, string.Concat(camera.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".md");

    public static string? Load(string camera)
    {
        var path = PathFor(camera);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        return text.Length == 0 ? null : text;
    }

    public static void Save(string camera, string text)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(camera), text.Trim() + "\n");
    }

    public static void Append(string camera, string line)
    {
        Directory.CreateDirectory(Dir);
        var existing = Load(camera);
        File.WriteAllText(PathFor(camera), (existing is null ? "" : existing + "\n") + "- " + line.Trim() + "\n");
    }
}
