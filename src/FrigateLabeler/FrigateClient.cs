using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FrigateLabeler;

/// <summary>Client for the (undocumented) Frigate+ web API at api.frigate.video/v1.</summary>
public sealed class FrigateClient : IDisposable
{
    private const string ApiBase = "https://api.frigate.video/v1/";
    private const string ImageBase = "https://images.frigate.video/";

    private readonly HttpClient _http;
    private readonly FrigateAuth _auth;
    private string? _userId;

    public FrigateClient()
    {
        // Shared cookie jar: if the API hands out CDN cookies for images.frigate.video, we keep them.
        _http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("frigate-labeler/0.1");
        _auth = new FrigateAuth(_http);
    }

    public void ReplaceRefreshToken(string refreshToken) => _auth.Replace(refreshToken);

    /// <summary>Checks the Frigate+ sign-in; returns null when it works, otherwise the problem.</summary>
    public async Task<string?> CheckAuthAsync(CancellationToken ct = default)
    {
        try { await _auth.GetAccessTokenAsync(ct); return null; }
        catch (FrigateAuthException ex) { return ex.Message; }
        catch (HttpRequestException ex) { return "Couldn't reach Frigate+: " + ex.Message; }
    }

    public Task<UserProfile> GetProfileAsync(CancellationToken ct = default) =>
        GetJsonAsync<UserProfile>("user/profile", ct);

    public async Task<List<CameraInfo>> GetCamerasAsync(CancellationToken ct = default) =>
        (await GetJsonAsync<CameraListResponse>("camera/list", ct)).List;

    public async Task<List<string>> GetCameraLabelsAsync(string camera, CancellationToken ct = default) =>
        (await GetJsonAsync<LabelsResponse>($"camera/{Uri.EscapeDataString(camera)}/labels", ct)).Labels;

    public Task<ImageData> GetImageDataAsync(string imageId, CancellationToken ct = default) =>
        GetJsonAsync<ImageData>($"image/{imageId}/data", ct);

    public Task<ImageSummary> GetImageAsync(string imageId, CancellationToken ct = default) =>
        GetJsonAsync<ImageSummary>($"image/{imageId}", ct);

    /// <summary>
    /// Pages through images. <paramref name="filter"/> mirrors the web app's tabs:
    /// New = never reviewed (verified=none); Unverified = has at least one unverified label.
    /// </summary>
    public async IAsyncEnumerable<ImageSummary> ListImagesAsync(
        string? camera, ImageFilter filter, int pageSize = 100,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var labels = filter == ImageFilter.Unverified && camera is not null ? await GetCameraLabelsAsync(camera, ct) : null;
        var seen = new HashSet<string>();
        string? lastImage = null;
        while (true)
        {
            // Pages don't come back at the requested size (filters are applied after paging),
            // so keep going until a page is empty or the cursor stops moving.
            var page = await ListImagesPageAsync(camera, filter, lastImage, pageSize, labels, ct);
            foreach (var img in page.List)
                if (seen.Add(img.Id)) yield return img;
            if (page.List.Count == 0 || page.LastImageId is null || page.LastImageId == lastImage) yield break;
            lastImage = page.LastImageId;
        }
    }

    /// <summary>Saves annotations exactly as the web editor's "Save" button does (does not verify).</summary>
    public async Task SaveAnnotationsAsync(string imageId, List<Annotation> annotations, bool hadFalsePositives,
        CancellationToken ct = default)
    {
        // The web editor clears the Frigate-submitted detections before saving the reviewed set.
        if (hadFalsePositives)
            (await SendAsync(HttpMethod.Delete, $"image/{imageId}/false_positive", null, ct)).Dispose();

        (await SendAsync(HttpMethod.Put, $"image/{imageId}/data",
            () => JsonContent.Create(new SaveImageDataRequest { Annotations = annotations }), ct)).Dispose();
    }

    /// <summary>
    /// Image JPEGs are served by CloudFront and authorized with signed cookies
    /// (CloudFront-Policy/-Signature/-Key-Pair-Id, Domain=.frigate.video) that the API sets on
    /// GET user/profile. The shared cookie jar carries them; if they've expired we fetch the
    /// profile again to renew them and retry once.
    /// </summary>
    /// <summary>Marks labels as verified on an image (the editor's Verify &amp; Save does this after saving).</summary>
    public async Task VerifyAsync(string imageId, IEnumerable<string> labels, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Put, $"image/{imageId}/verify",
            () => JsonContent.Create(new { labels = labels.ToList() }), ct)).Dispose();

    /// <summary>One page of the image list, for browsing.</summary>
    public async Task<ImageListResponse> ListImagesPageAsync(string? camera, ImageFilter filter, string? lastImage,
        int pageSize, List<string>? cameraLabels, CancellationToken ct = default)
    {
        var q = new List<string> { $"limit={pageSize}" };
        if (camera is not null) q.Add($"camera={Uri.EscapeDataString(camera)}");
        if (filter == ImageFilter.New) q.Add("verified=none");
        if (filter == ImageFilter.Unverified)
        {
            cameraLabels ??= camera is null ? throw new ArgumentException("The unverified filter needs a camera")
                : await GetCameraLabelsAsync(camera, ct);
            q.Add($"unverified={Uri.EscapeDataString(string.Join(',', cameraLabels))}");
        }
        if (lastImage is not null) q.Add($"lastImage={lastImage}");
        return await GetJsonAsync<ImageListResponse>($"image/list?{string.Join('&', q)}", ct);
    }

    public Task<byte[]> DownloadThumbnailAsync(string imageId, CancellationToken ct = default) =>
        DownloadAsync(imageId, "-thumb.jpg", ct);

    public Task<byte[]> DownloadImageAsync(string imageId, CancellationToken ct = default) =>
        DownloadAsync(imageId, ".jpg", ct);

    private async Task<byte[]> DownloadAsync(string imageId, string suffix, CancellationToken ct)
    {
        var url = (await ImageUrlAsync(imageId, ct))[..^4] + suffix;
        for (var attempt = 1; ; attempt++)
        {
            using var resp = await _http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync(ct);

            if (resp.StatusCode == HttpStatusCode.Forbidden && attempt == 1)
            {
                await RenewImageCookiesAsync(ct);
                continue;
            }
            throw new HttpRequestException($"Could not download image {imageId} ({(int)resp.StatusCode}).");
        }
    }

    private async Task RenewImageCookiesAsync(CancellationToken ct) => _userId = (await GetProfileAsync(ct)).Id;

    private async Task<string> ImageUrlAsync(string imageId, CancellationToken ct)
    {
        if (_userId is null) await RenewImageCookiesAsync(ct);
        return $"{ImageBase}{_userId}/{imageId}.jpg";
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, path, null, ct);
        return (await resp.Content.ReadFromJsonAsync<T>(AppPaths.Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Func<HttpContent>? content,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, ApiBase + path) { Content = content?.Invoke() };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetAccessTokenAsync(ct));
            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return resp;

            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            if ((status == 429 || status >= 500) && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }
            throw new HttpRequestException($"{method} {path} failed ({status}): {body}");
        }
    }

    public void Dispose() => _http.Dispose();
}

public enum ImageFilter { New, Unverified, All }
