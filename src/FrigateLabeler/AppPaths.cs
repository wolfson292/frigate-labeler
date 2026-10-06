using System.Text.Json;

namespace FrigateLabeler;

public static class AppPaths
{
    /// <summary>Tokens, the API key and camera notes. FRIGATE_LABELER_CONFIG overrides it (e.g. a Docker volume).</summary>
    public static readonly string ConfigDir = Environment.GetEnvironmentVariable("FRIGATE_LABELER_CONFIG") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "frigate-labeler");

    /// <summary>Default results folder. FRIGATE_LABELER_DATA overrides it.</summary>
    public static string DefaultDataDir =>
        Environment.GetEnvironmentVariable("FRIGATE_LABELER_DATA") is { Length: > 0 } dir ? dir : "runs";

    public static readonly string AuthFile = Path.Combine(ConfigDir, "auth.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Writes a file readable only by the current user (it may hold tokens).</summary>
    public static void WritePrivate(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
