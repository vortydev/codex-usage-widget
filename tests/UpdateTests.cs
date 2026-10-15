using System.Net;
using System.Net.Http;
using System.Text.Json;
using CodexUsageWidget;

internal static class UpdateTests
{
    internal sealed class Handler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string ExpectedUri { get; set; } = "https://api.github.com/repos/vortydev/codex-usage-widget/releases/latest";
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
            = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release("9.9.9")) });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri?.AbsoluteUri != ExpectedUri ||
                request.Headers.Authorization is not null || request.Headers.UserAgent.Count == 0)
                throw new InvalidOperationException("Unexpected update request or credentials");
            return Respond(request, cancellationToken);
        }
    }

    internal static string Release(string version, string? url = null) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version,
        draft = false,
        prerelease = false,
        assets = new[] { new { name = $"CodexUsageWidget-{version}-win-x64.msi", browser_download_url = url ?? $"https://github.com/vortydev/codex-usage-widget/releases/download/v{version}/CodexUsageWidget-{version}-win-x64.msi" } },
    });

    internal static void Run(Action<bool, string> check)
    {
        UpdateResult Read(string body, HttpStatusCode status = HttpStatusCode.OK, Version? current = null)
        {
            using var checker = new UpdateChecker(new Handler
            {
                Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }),
            });
            return checker.CheckAsync(current ?? new Version(0, 2, 0, 0)).GetAwaiter().GetResult();
        }

        var available = Read(Release("0.3.0"));
        check(available.DownloadUri is not null && available.Version == "0.3.0" && !available.IsError, "New stable MSI available");
        check(Read(Release("0.2.0")).Message.Contains("up to date"), "Ignore assembly revision when comparing");
        check(Read(Release("0.1.5")).Message.Contains("published version is v0.1.5"), "Newer local build");
        check(Read("", HttpStatusCode.NotFound).Message == "No releases published yet.", "No published releases");
        var missing = Read("{\"tag_name\":\"v0.3.0\",\"draft\":false,\"prerelease\":false,\"assets\":[]}");
        check(missing.DownloadUri is null && !missing.IsError && missing.Message.Contains("not available yet"), "Release without MSI");
        foreach (var body in new[] { "not json", "[]", "null", "{}", Release("0.3.0-beta"), Release("0.3"), Release("0.3.0.0"),
            Release("999999999999999.0.0"), Release("0.3.0").Replace("\"draft\":false", "\"draft\":true"),
            Release("0.3.0").Replace("\"prerelease\":false", "\"prerelease\":true"),
            "{\"tag_name\":\"v0.3.0\",\"draft\":false,\"prerelease\":false,\"assets\":{}}" })
            check(Read(body).IsError, "Malformed or non-stable release rejected");
        foreach (var url in new[] { "http://github.com/vortydev/codex-usage-widget/releases/download/v0.3.0/CodexUsageWidget-0.3.0-win-x64.msi",
            "https://example.com/installer.msi", "file:///C:/installer.msi", "https://github.com/other/repo/releases/download/v0.3.0/installer.msi",
            "https://user@github.com/vortydev/codex-usage-widget/releases/download/v0.3.0/CodexUsageWidget-0.3.0-win-x64.msi",
            "https://github.com:444/vortydev/codex-usage-widget/releases/download/v0.3.0/CodexUsageWidget-0.3.0-win-x64.msi",
            "https://github.com/vortydev/codex-usage-widget/releases/download/v0.3.0/CodexUsageWidget-0.3.0-win-x64.msi?redirect=1" })
        {
            var result = Read(Release("0.3.0", url));
            check(result.IsError && result.DownloadUri is null, "Untrusted download URL rejected");
        }
        check(Read(Release("0.3.0").Replace("win-x64.msi\"", "win-arm64.msi\"")).DownloadUri is null, "Wrong architecture excluded");
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.Redirect })
            check(Read("", status).IsError, "HTTP failure or redirect reported");
        using (var checker = new UpdateChecker(new Handler { Respond = (_, _) => throw new HttpRequestException("offline") }))
            check(checker.CheckAsync(new Version(0, 2, 0)).GetAwaiter().GetResult().Message.Contains("connection"), "Offline failure reported");
        using (var checker = new UpdateChecker(new Handler { Respond = (_, _) => throw new TaskCanceledException("timeout") }))
            check(checker.CheckAsync(new Version(0, 2, 0)).GetAwaiter().GetResult().Message.Contains("timed out"), "Timeout reported");
        using (var cancellation = new CancellationTokenSource())
        using (var checker = new UpdateChecker(new Handler { Respond = (_, token) => Task.FromCanceled<HttpResponseMessage>(token) }))
        {
            cancellation.Cancel();
            try { checker.CheckAsync(new Version(0, 2, 0), cancellation.Token).GetAwaiter().GetResult(); check(false, "Cancellation expected"); }
            catch (OperationCanceledException) { check(true, "Caller cancellation propagated"); }
        }
    }
}
