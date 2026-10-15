using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;
using CodexUsageWidget;

namespace CodexUsageSetup;

internal sealed record InstalledWidget(Version Version, string ProductCode, string Folder);

internal static class Installation
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexUsageWidget");
    internal static string NormalizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal) ||
            folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || folder.Contains('"'))
            throw new InvalidOperationException("Choose a local, absolute application folder using Browse.");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a dedicated application folder, not a drive root.");
        return normalized;
    }

    internal static InstalledWidget ParseRecord(string text, string code, string folder)
    {
        if (!Version.TryParse(text, out var version) || version.Build < 0 || version.Revision >= 0 ||
            !string.Equals(ProductCode(version), code, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installation record is invalid. Use Windows Settings → Apps to inspect it.");
        return new(version, code, NormalizeFolder(folder));
    }
    internal static string ProductCode(Version version)
    {
        var name = $"vortydev.CodexUsageWidget:win-x64:{version.ToString(3)}".ToLowerInvariant();
        return Guid.Parse(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..32]).ToString("B").ToUpperInvariant();
    }

    internal static InstalledWidget? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\vortydev\CodexUsageWidget\Installer");
        if (key?.GetValue("ProductCode") is not string code) return null;
        if (key.GetValue("Version") is not string text || key.GetValue("InstallFolder") is not string folder)
            throw new InvalidOperationException("The installation record is invalid. Use Windows Settings → Apps to inspect it.");
        return ParseRecord(text, code, folder);
    }

    internal static ProcessStartInfo Command(string operation, string target, string log, string? folder = null)
    {
        if (operation is not ("install" or "repair" or "uninstall" or "shortcut")) throw new ArgumentException("Unknown setup operation.");
        if (operation is "uninstall" or "shortcut" && !Guid.TryParse(target, out _)) throw new ArgumentException("Invalid product code.");
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        { UseShellExecute = false };
        command.ArgumentList.Add(operation == "uninstall" ? "/x" : "/i");
        command.ArgumentList.Add(target);
        if (folder is not null)
        {
            if (operation != "install") throw new ArgumentException("Only installation can supply a destination.");
            command.ArgumentList.Add("INSTALLFOLDER=" + NormalizeFolder(folder));
        }
        if (operation == "shortcut") command.ArgumentList.Add("ADDLOCAL=DesktopShortcut");
        if (operation == "repair")
        {
            command.ArgumentList.Add("REINSTALL=ALL");
            command.ArgumentList.Add("REINSTALLMODE=amus");
        }
        command.ArgumentList.Add("/qb"); // Native progress, cancellation and files-in-use prompts.
        command.ArgumentList.Add("/norestart");
        command.ArgumentList.Add("/L*v!"); // Flush each line so Show details can follow the live log.
        command.ArgumentList.Add(log);
        return command;
    }

    internal static string? Failure(int exitCode) => exitCode switch
    {
        0 or 3010 => null,
        1602 => "Setup was cancelled. No success is being reported.",
        1618 => "Another installation is running. Wait for it to finish, then retry.",
        _ => $"Windows Installer failed (code {exitCode}). See the setup log for details.",
    };

}

internal sealed class PackageDownloader : IDisposable
{
    private readonly HttpClient _client;
    internal PackageDownloader(HttpMessageHandler? handler = null) => _client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(20) };

    internal static bool Allowed(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com");

    internal async Task DownloadAsync(UpdateResult release, string destination, CancellationToken token)
    {
        if (release.DownloadUri is null || release.Sha256 is null || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("This release has no verified installer checksum. Ask the repository owner to upload its MSI release asset.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var uri = release.DownloadUri;
        var created = false;
        if (uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/vortydev/codex-usage-widget/releases/download/", StringComparison.Ordinal) ||
            !uri.AbsolutePath.EndsWith($"/CodexUsageWidget-{release.Version}-win-x64.msi", StringComparison.Ordinal) || uri.Query.Length != 0)
            throw new InvalidOperationException("Installer is not a release asset in the widget repository.");
        try
        {
            for (var hop = 0; hop < 4; hop++)
            {
                if (!Allowed(uri)) throw new InvalidOperationException("The download redirected outside GitHub's release servers.");
                using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location ?? throw new InvalidOperationException("Missing download redirect.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                const long limit = 512L * 1024 * 1024;
                if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("Installer exceeds the download size limit.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                created = true;
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > limit) throw new InvalidOperationException("Installer exceeds the download size limit.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                }
                if (total == 0 || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Installer checksum did not match. Nothing was installed.");
                return;
            }
            throw new InvalidOperationException("Too many download redirects.");
        }
        catch
        {
            // Only the fresh output file for this request is removed.
            if (created && File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    public void Dispose() => _client.Dispose();
}
