using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CodexUsageWidget;

internal sealed record UpdateResult(string Message, string? Version = null, Uri? DownloadUri = null, bool IsError = false, string? Sha256 = null);

internal sealed class UpdateChecker : IDisposable
{
    private const string Repository = "vortydev/codex-usage-widget";
    private readonly HttpClient _client;

    public UpdateChecker(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        var version = typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"CodexUsageWidget/{version}");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public Task<UpdateResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default) =>
        FetchAsync("latest", currentVersion, cancellationToken);

    public Task<UpdateResult> FindInstallerAsync(Version? exactVersion = null, CancellationToken cancellationToken = default) =>
        FetchAsync(exactVersion is null ? "latest" : $"tags/v{exactVersion.ToString(3)}", null, cancellationToken);

    private async Task<UpdateResult> FetchAsync(string endpoint, Version? currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync($"https://api.github.com/repos/{Repository}/releases/{endpoint}", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(endpoint == "latest" ? "No releases published yet." : "The installed version's release is unavailable.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new("GitHub could not serve this check. Try again later.", IsError: true);
            if (!response.IsSuccessStatusCode)
                return new($"Could not check updates (HTTP {(int)response.StatusCode}). Try again later.", IsError: true);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return ReadRelease(document.RootElement, currentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new("Update check timed out. Try again.", IsError: true);
        }
        catch (HttpRequestException)
        {
            return new("Could not reach GitHub. Check your connection and try again.", IsError: true);
        }
        catch (JsonException)
        {
            return new("GitHub returned invalid release information.", IsError: true);
        }
    }

    private static UpdateResult ReadRelease(JsonElement release, Version? currentVersion)
    {
        var invalid = new UpdateResult("GitHub returned invalid release information.", IsError: true);
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !TryString(release, "tag_name", out var tag)) return invalid;
        var numbers = tag.StartsWith('v') ? tag[1..] : tag;
        var parts = numbers.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(c => c is < '0' or > '9')) ||
            !System.Version.TryParse(numbers, out var latest)) return invalid;
        if (currentVersion is not null)
        {
            currentVersion = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
            if (latest == currentVersion) return new($"You're up to date (v{latest}).");
            if (latest < currentVersion) return new($"You're running v{currentVersion}; the published version is v{latest}.");
        }

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return invalid;
        var name = $"CodexUsageWidget-{latest}-win-x64.msi";
        foreach (var asset in assets.EnumerateArray())
        {
            if (!TryString(asset, "name", out var assetName) || assetName != name) continue;
            if (!TryString(asset, "browser_download_url", out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host != "github.com" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.AbsolutePath != $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}") return invalid;
            string? sha256 = null;
            if (TryString(asset, "digest", out var digest) && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
                digest.Length == 71 && digest[7..].All(Uri.IsHexDigit)) sha256 = digest[7..].ToLowerInvariant();
            return new($"v{latest} is available. Download and run its installer.", latest.ToString(3), uri, Sha256: sha256);
        }
        return new($"v{latest} is published, but its x64 installer is not available yet.");
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var field) ||
            field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString() ?? string.Empty;
        return value.Length != 0;
    }

    public void Dispose() => _client.Dispose();
}
