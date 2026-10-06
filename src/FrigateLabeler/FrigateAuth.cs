using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrigateLabeler;

/// <summary>
/// Frigate+ signs in through an AWS Cognito hosted UI (auth.frigate.video). The web app is a
/// public OAuth client, so a refresh token is enough to mint new access tokens; the API wants
/// the <b>access</b> token as a Bearer header (the id token is rejected with 403).
/// </summary>
public sealed class FrigateAuth(HttpClient http)
{
    public const string TokenEndpoint = "https://auth.frigate.video/oauth2/token";
    public const string ClientId = "6q5d80738bn3jot0osfo7k0u6t";

    private StoredAuth? _auth;

    public static bool HasStoredToken => File.Exists(AppPaths.AuthFile);

    /// <summary>Replaces the stored refresh token (e.g. pasted on the web page) and drops the cached access token.</summary>
    public void Replace(string refreshToken)
    {
        SaveRefreshToken(refreshToken);
        _auth = null;
    }

    public static void SaveRefreshToken(string refreshToken)
    {
        var auth = new StoredAuth { RefreshToken = refreshToken.Trim() };
        AppPaths.WritePrivate(AppPaths.AuthFile, JsonSerializer.Serialize(auth, AppPaths.Json));
    }

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // The review server makes concurrent API calls; only one of them should refresh the token.
        await _refreshLock.WaitAsync(ct);
        try { return await GetAccessTokenLockedAsync(ct); }
        finally { _refreshLock.Release(); }
    }

    private async Task<string> GetAccessTokenLockedAsync(CancellationToken ct)
    {
        _auth ??= Load();
        if (_auth.AccessToken is { } token && _auth.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return token;

        using var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = _auth.RefreshToken,
        }), ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new FrigateAuthException(
                $"Token refresh failed ({(int)resp.StatusCode}): {body}. " +
                "The refresh token may have expired — run `frigate-labeler auth` again.");
        }

        var tokens = (await resp.Content.ReadFromJsonAsync<TokenResponse>(ct))!;
        _auth = _auth with
        {
            AccessToken = tokens.AccessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn),
            // Cognito normally doesn't rotate refresh tokens, but keep a new one if it does.
            RefreshToken = tokens.RefreshToken ?? _auth.RefreshToken,
        };
        AppPaths.WritePrivate(AppPaths.AuthFile, JsonSerializer.Serialize(_auth, AppPaths.Json));
        return tokens.AccessToken;
    }

    private static StoredAuth Load()
    {
        // In Docker there's no terminal for `auth`; a token in the environment seeds the first sign-in.
        if (!HasStoredToken && Environment.GetEnvironmentVariable("FRIGATE_PLUS_REFRESH_TOKEN") is { Length: > 0 } seed)
            SaveRefreshToken(seed);
        if (!HasStoredToken)
            throw new FrigateAuthException("Not signed in to Frigate+. Paste a refresh token on the web page, " +
                                           "or run `frigate-labeler auth`.");
        return JsonSerializer.Deserialize<StoredAuth>(File.ReadAllText(AppPaths.AuthFile), AppPaths.Json)!;
    }

    private sealed record StoredAuth
    {
        public required string RefreshToken { get; init; }
        public string? AccessToken { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

public sealed class FrigateAuthException(string message) : Exception(message);
