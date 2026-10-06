using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;

namespace FrigateLabeler;

/// <summary>User settings in ~/.config/frigate-labeler/config.json.</summary>
public sealed record AppConfig
{
    public const string ApiKeyPlaceholder = "sk-ant-REPLACE-WITH-YOUR-KEY";

    public static readonly string FilePath = Path.Combine(AppPaths.ConfigDir, "config.json");

    [JsonPropertyName("anthropicApiKey")]
    public string? AnthropicApiKey { get; init; }

    public static AppConfig Load() =>
        File.Exists(FilePath)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), AppPaths.Json) ?? new()
            : new();

    /// <summary>Creates config.json with a placeholder key if it doesn't exist yet.</summary>
    public static bool CreateTemplate()
    {
        if (File.Exists(FilePath)) return false;
        AppPaths.WritePrivate(FilePath,
            JsonSerializer.Serialize(new AppConfig { AnthropicApiKey = ApiKeyPlaceholder }, AppPaths.Json));
        return true;
    }

    /// <summary>
    /// ANTHROPIC_API_KEY from the environment wins; otherwise the key from config.json; otherwise the
    /// SDK's own credential lookup (e.g. an `ant auth login` profile).
    /// </summary>
    public AnthropicClient CreateAnthropicClient()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return new AnthropicClient();

        if (AnthropicApiKey == ApiKeyPlaceholder)
            throw new ConfigException($"Replace the placeholder API key in {FilePath} with your Anthropic API key.");

        return string.IsNullOrWhiteSpace(AnthropicApiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = AnthropicApiKey.Trim() };
    }

    public string DescribeApiKeySource() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? "ANTHROPIC_API_KEY environment variable"
        : AnthropicApiKey == ApiKeyPlaceholder ? "placeholder (not replaced yet)"
        : !string.IsNullOrWhiteSpace(AnthropicApiKey) ? FilePath
        : "none found";
}

public sealed class ConfigException(string message) : Exception(message);
