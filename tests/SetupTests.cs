using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Forms;
using CodexUsageSetup;
using CodexUsageWidget;

internal static class SetupTests
{
    private sealed class DownloadHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    internal static void Run(Action<bool, string> check)
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 }; // Never executed; only download/hash logic is exercised.
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var url = new Uri("https://github.com/vortydev/codex-usage-widget/releases/download/v0.2.0/CodexUsageWidget-0.2.0-win-x64.msi");
        var release = new UpdateResult("Available", "0.2.0", url, Sha256: digest);
        using (var checker = new UpdateChecker(new UpdateTests.Handler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(UpdateTests.Release("0.2.0").Replace("\"assets\":[{", $"\"assets\":[{{\"digest\":\"sha256:{digest}\",")) }),
        }))
        {
            var found = checker.FindInstallerAsync().GetAwaiter().GetResult();
            check(found.DownloadUri == url && found.Sha256 == digest, "Launcher obtains equal-version package and digest");
        }
        using (var checker = new UpdateChecker(new UpdateTests.Handler
        {
            ExpectedUri = "https://api.github.com/repos/vortydev/codex-usage-widget/releases/tags/v0.2.0",
        }))
            check(checker.FindInstallerAsync(new Version(0, 2, 0)).GetAwaiter().GetResult().DownloadUri is not null, "Repair requests exact installed release tag");

        var directory = Directory.CreateTempSubdirectory("setup-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "test.msi");
            var log = Path.Combine(directory, "test.log");
            check(SetupLog.ReadTail(null).Contains("will appear"), "Details are available before MSI logging starts");
            File.WriteAllText(log, "Unicode log: café\r\nlast action", System.Text.Encoding.Unicode);
            check(SetupLog.ReadTail(log).Contains("café") && SetupLog.ReadTail(log).EndsWith("last action"), "Native UTF-16 MSI logs are readable");
            File.WriteAllText(log, new string('x', 300000) + "last action", System.Text.Encoding.UTF8);
            check(SetupLog.ReadTail(log).Length <= 128 * 1024 && SetupLog.ReadTail(log).EndsWith("last action"), "Log view is bounded and follows the latest activity");
            using (var locked = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
                check(SetupLog.ReadTail(log).StartsWith("Cannot read"), "Locked log is an inline diagnostic, not a crash");
            using (var downloader = new PackageDownloader(new DownloadHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
            {
                downloader.DownloadAsync(release, path, CancellationToken.None).GetAwaiter().GetResult();
                check(File.ReadAllBytes(path).SequenceEqual(bytes), "Verified package bytes downloaded");
                try { downloader.DownloadAsync(release, path, CancellationToken.None).GetAwaiter().GetResult(); check(false, "Existing destination should fail"); }
                catch (IOException) { check(File.ReadAllBytes(path).SequenceEqual(bytes), "Existing output is never overwritten or deleted"); }
            }
            File.Delete(path);
            using (var cancellation = new CancellationTokenSource())
            using (var downloader = new PackageDownloader(new DownloadHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
            {
                cancellation.Cancel();
                try { downloader.DownloadAsync(release, path, cancellation.Token).GetAwaiter().GetResult(); check(false, "Cancelled download should fail"); }
                catch (OperationCanceledException) { check(!File.Exists(path), "Cancellation leaves no installer to execute"); }
            }
            using (var downloader = new PackageDownloader(new DownloadHandler(_ => throw new HttpRequestException("offline"))))
            {
                try { downloader.DownloadAsync(release, path, CancellationToken.None).GetAwaiter().GetResult(); check(false, "Offline download should fail"); }
                catch (HttpRequestException) { check(!File.Exists(path), "Network failure leaves no installer to execute"); }
            }
            using (var downloader = new PackageDownloader(new DownloadHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
            {
                try { downloader.DownloadAsync(release with { Sha256 = new string('0', 64) }, path, CancellationToken.None).GetAwaiter().GetResult(); check(false, "Bad digest should fail"); }
                catch (InvalidOperationException) { check(!File.Exists(path), "Hash mismatch removes only partial output"); }
                try { downloader.DownloadAsync(release with { Sha256 = null }, path, CancellationToken.None).GetAwaiter().GetResult(); check(false, "Missing digest should fail"); }
                catch (InvalidOperationException) { check(!File.Exists(path), "Missing digest blocks download"); }
            }
            using (var downloader = new PackageDownloader(new DownloadHandler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/installer.msi") } })))
            {
                try { downloader.DownloadAsync(release, path, CancellationToken.None).GetAwaiter().GetResult(); check(false, "Hostile redirect should fail"); }
                catch (InvalidOperationException) { check(!File.Exists(path), "Untrusted redirect rejected"); }
            }
            using (var downloader = new PackageDownloader(new DownloadHandler(_ => throw new InvalidOperationException("Should not make a request"))))
            {
                try { downloader.DownloadAsync(release with { DownloadUri = new Uri("https://github.com/other/repo/releases/download/v0.2.0/CodexUsageWidget-0.2.0-win-x64.msi") }, path, CancellationToken.None).GetAwaiter().GetResult(); check(false, "Wrong repository should fail"); }
                catch (InvalidOperationException ex) { check(ex.Message.Contains("widget repository") && !File.Exists(path), "Wrong repository blocked before network request"); }
            }
            using (var downloader = new PackageDownloader(new DownloadHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) })))
            {
                try { downloader.DownloadAsync(release, path, CancellationToken.None).GetAwaiter().GetResult(); check(false, "Empty download should fail"); }
                catch (InvalidOperationException) { check(!File.Exists(path), "Empty package rejected and removed"); }
            }
            var calls = 0;
            using (var downloader = new PackageDownloader(new DownloadHandler(_ => ++calls == 1 ?
                new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/test?token=1") } } :
                new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
            {
                downloader.DownloadAsync(release, path, CancellationToken.None).GetAwaiter().GetResult();
                check(calls == 2 && File.Exists(path), "Signed GitHub asset redirect supported");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        var code = Installation.ProductCode(new Version(0, 2, 0));
        check(code == Installation.ProductCode(new Version(0, 2, 0)) && code != Installation.ProductCode(new Version(0, 2, 1)), "Product identity stable per version");
        var repair = Installation.Command("repair", @"C:\temp space\test.msi", @"C:\temp space\test.log");
        check(repair.ArgumentList.Contains("REINSTALL=ALL") && repair.ArgumentList.Contains("REINSTALLMODE=amus") && repair.ArgumentList.Contains("/norestart"), "Native repair arguments and no restart");
        check(Installation.Command("uninstall", code, "test.log").ArgumentList.First() == "/x", "Uninstall uses registered product code");
        check(Installation.Command("shortcut", code, "test.log").ArgumentList.Contains("ADDLOCAL=DesktopShortcut"), "Desktop shortcut is MSI-managed");
        var customFolder = @"C:\Users\Example\Apps\Codex Usage Widget";
        check(Installation.Command("install", @"C:\temp\test.msi", "test.log", customFolder).ArgumentList.Contains("INSTALLFOLDER=" + customFolder), "Selected folder is passed as one MSI argument");
        check(repair.ArgumentList.Contains("/L*v!"), "MSI log flushes for live details");
        var legacy = Installation.ParseRecord("0.2.0", code, Installation.Folder + "\\");
        check(legacy.Folder == Installation.Folder, "Existing default-folder records remain valid");
        var custom = Installation.ParseRecord("0.2.0", code, customFolder + "\\");
        check(custom.Folder == customFolder && Path.Combine(custom.Folder, "CodexUsageWidget.exe").StartsWith(customFolder), "Maintenance and app launch use the registered custom folder");
        foreach (var invalid in new[] { "", "relative", @"C:\", @"\\server\share\app", "C:\\App\"bad" })
        {
            try { Installation.NormalizeFolder(invalid); check(false, "Invalid location should fail"); }
            catch (InvalidOperationException) { check(true, "Unsafe or non-local location rejected"); }
        }
        try { Installation.Command("repair", "test.msi", "test.log", customFolder); check(false, "Repair must not relocate"); }
        catch (ArgumentException) { check(true, "Repair cannot override its application folder"); }
        foreach (var status in new[] { 0, 3010 }) check(Installation.Failure(status) is null, "Successful MSI exit recognised");
        foreach (var status in new[] { 1602, 1618, 1603 }) check(Installation.Failure(status) is not null, "Failed/cancelled MSI never reported successful");
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        {
            CheckLayout(scale, check, digest, installed: false);
            CheckLayout(scale, check, digest, installed: true);
            CheckLayout(scale, check, digest, installed: true, unavailable: true);
        }
        double Luminance(Color color)
        {
            double Linear(byte component) { var value = component / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        double Contrast(Color text, Color surface) => (Luminance(text) + .05) / (Luminance(surface) + .05);
        check(Contrast(SetupTheme.Muted, SetupTheme.Surface) >= 4.5 && Contrast(SetupTheme.Muted, SetupTheme.Background) >= 4.5, "Disabled text and status have readable dark-theme contrast");
        check(Contrast(SetupTheme.Text, SetupTheme.PrimaryFill) >= 4.5 && Contrast(SetupTheme.Text, SetupTheme.PrimaryHover) >= 4.5, "Primary button retains readable text when hovered");
        CheckPartialPainting(check);
    }

    private static void CheckPartialPainting(Action<bool, string> check)
    {
        if (SystemInformation.HighContrast) return;
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        foreach (var origin in new[] { new Point(40, 40), new Point(-20, 40) })
        foreach (var control in new Control[]
        {
            new SetupButton { Text = "Button repaint", Size = new(180, 32) },
            new SetupRadioButton { Text = "Radio repaint", Size = new(180, 32), Checked = true },
            new SetupCheckBox { Text = "Checkbox repaint", Size = new(180, 32), Checked = true },
        })
        using (control)
        {
            control.Scale(new SizeF(scale, scale));
            // A transparent child causes WinForms to repaint its parent using a
            // translated, clipped Graphics. Ordinary DrawToBitmap misses this path.
            var clip = new Rectangle(origin.X + 30, origin.Y + 4, control.Width - 45, control.Height - 8);
            using var bitmap = new Bitmap(control.Width + 80, control.Height + 80);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Fuchsia);
                graphics.SetClip(clip);
                graphics.TranslateTransform(origin.X, origin.Y);
                var paint = control.GetType().GetMethod("OnPaint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                using var args = new PaintEventArgs(graphics, new Rectangle(30, 4, control.Width - 45, control.Height - 8));
                paint.Invoke(control, new object[] { args });
            }
            var outsideChanged = false;
            var insideChanged = false;
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).ToArgb() != Color.Fuchsia.ToArgb())
                {
                    if (clip.Contains(x, y)) insideChanged = true;
                    else outsideChanged = true;
                }
            check(!outsideChanged, $"{control.GetType().Name} preserves translated repaint clipping at {scale}, offset {origin.X}");
            check(insideChanged, $"{control.GetType().Name} paints inside the translated clip at {scale}, offset {origin.X}");
        }
    }

    private static IEnumerable<Control> Controls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Controls(child)) yield return nested;
        }
    }

    private static void CheckLayout(float scale, Action<bool, string> check, string digest, bool installed, bool unavailable = false)
    {
        using var form = new SetupForm(new UpdateChecker(new UpdateTests.Handler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(unavailable ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent(UpdateTests.Release("255.255.65535").Replace("\"assets\":[{", $"\"assets\":[{{\"digest\":\"sha256:{digest}\",")) }),
        }), () => installed ? Installation.ParseRecord("0.2.0", Installation.ProductCode(new Version(0, 2, 0)), @"C:\Users\Example\Apps\Codex Usage Widget") : null);
        form.ShowInTaskbar = false;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Application.DoEvents(); // Deliver Shown, including its asynchronous release check.
        var again = Controls(form).OfType<Button>().Single(x => x.Text == "Check again");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!again.Enabled && DateTime.UtcNow < deadline) Application.DoEvents();
        if (scale != 1) form.Scale(new SizeF(scale, scale));
        form.PerformLayout();
        check(again.Enabled && Controls(form).OfType<RadioButton>().Count() == 3, "Setup lists three maintenance actions");
        var details = Controls(form).OfType<Label>().Single(x => x.Text.StartsWith("Installed:"));
        check(details.Height >= details.PreferredHeight, "Installed and available versions are visible");
        check(Controls(form).OfType<Button>().Single(x => x.Text == "Next").Enabled, "Available maintenance action enables Next");
        check(Controls(form).OfType<TextBox>().Single().ReadOnly && Controls(form).OfType<Button>().Single(x => x.Text == "Browse…").Enabled == !installed,
            $"Native Browse enabled for new installs only at {scale}");
        if (installed) check(Controls(form).OfType<TextBox>().Single().Text == @"C:\Users\Example\Apps\Codex Usage Widget", "Update preserves registered custom location");
        if (!SystemInformation.HighContrast)
        {
            var field = Controls(form).OfType<TextBox>().Single();
            check(field.BackColor == SetupTheme.Surface && field.ForeColor == SetupTheme.Text && field.BorderStyle == BorderStyle.None && field.SelectionLength == 0, "Application path stays dark without automatic full selection");
            foreach (var disabled in Controls(form).Where(x => x is Button or RadioButton && !x.Enabled))
            {
                using var painted = new Bitmap(disabled.Width, disabled.Height);
                disabled.DrawToBitmap(painted, new(Point.Empty, painted.Size));
                var readable = 0;
                for (var y = 0; y < painted.Height; y++)
                    for (var x = disabled is RadioButton ? 25 : 0; x < painted.Width; x++)
                    {
                        var pixel = painted.GetPixel(x, y);
                        if (pixel.R > 120 && pixel.G > 130 && pixel.B > 140) readable++;
                    }
                check(readable > 20, "Disabled control text is visibly painted: " + disabled.Text);
            }
        }
        if (unavailable)
            check(!Controls(form).OfType<RadioButton>().Single(x => x.Text.StartsWith("Install")).Enabled &&
                Controls(form).OfType<RadioButton>().Single(x => x.Text.StartsWith("Repair")).Checked, "Unavailable-release state keeps disabled install and selected repair distinct");
        foreach (var control in Controls(form).Where(x => x.Visible))
            check(control.Right <= control.Parent!.ClientSize.Width && control.Bottom <= control.Parent.ClientSize.Height, $"Setup control fits at {scale}: {control.Text}");
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"setup-{scale}-{installed}-{unavailable}.png")); }
        Controls(form).OfType<Button>().Single(x => x.Text == "Show details").PerformClick();
        Application.DoEvents();
        var viewer = form.OwnedForms.OfType<SetupDetailsForm>().Single();
        check(viewer.Visible && viewer.Controls.OfType<TextBox>().Single().Text.Contains("Checking latest stable"), "Details window shows real launcher activity before installation");
        viewer.Close();
        form.SetStatus("Windows Installer failed (code 1603). See the setup log for details.\nLog: C:\\Users\\Example\\AppData\\Local\\CodexUsageWidget\\SetupLogs\\setup-20261011-200000-00000000000000000000000000000000.log");
        form.PerformLayout();
        foreach (var control in Controls(form).Where(x => x.Visible))
            check(control.Right <= control.Parent!.ClientSize.Width && control.Bottom <= control.Parent.ClientSize.Height, $"Failure details fit at {scale}: {control.Text}");
        form.ShowCompletion(new Version(0, 2, 0), removed: false, restart: false);
        check(Controls(form).OfType<CheckBox>().All(x => x.Visible && !x.Checked), "Finish options visible and opt-in");
        foreach (var control in Controls(form).Where(x => x.Visible))
            check(control.Right <= control.Parent!.ClientSize.Width && control.Bottom <= control.Parent.ClientSize.Height, $"Finish control fits at {scale}: {control.Text}");
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"setup-finish-{scale}.png")); }
        form.ShowCompletion(new Version(0, 2, 0), removed: false, restart: true);
        check(!Controls(form).OfType<CheckBox>().Single(x => x.Text.StartsWith("Start")).Enabled, "Restart required disables app launch");
        form.ShowCompletion(null, removed: true, restart: false);
        check(Controls(form).OfType<CheckBox>().All(x => !x.Visible), "Uninstall finish has no launch or shortcut options");
        form.Close();
    }
}
