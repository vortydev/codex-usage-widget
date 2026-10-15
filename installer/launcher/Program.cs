using System.Diagnostics;
using CodexUsageWidget;

namespace CodexUsageSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var singleton = new Mutex(false, @"Local\CodexUsageWidgetSetup");
        bool ownsMutex;
        try { ownsMutex = singleton.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) { MessageBox.Show("Setup is already open.", "Codex Usage Setup"); return; }
        try { Application.Run(new SetupForm()); }
        finally { singleton.ReleaseMutex(); }
    }
}

internal sealed class SetupForm : Form
{
    private readonly UpdateChecker _checker;
    private readonly Func<InstalledWidget?> _readInstallation;
    private readonly CancellationTokenSource _closing = new();
    private readonly Label _details = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly RadioButton _install = new SetupRadioButton { Text = "Install / update to latest stable", AutoSize = true, Checked = true, Enabled = false };
    private readonly RadioButton _repair = new SetupRadioButton { Text = "Repair installed version", AutoSize = true, Enabled = false };
    private readonly RadioButton _uninstall = new SetupRadioButton { Text = "Uninstall (keep settings)", AutoSize = true, Enabled = false };
    private readonly CheckBox _desktop = new SetupCheckBox { Text = "Add desktop shortcut", AutoSize = true, Visible = false };
    private readonly CheckBox _launch = new SetupCheckBox { Text = "Start Codex Usage Widget", AutoSize = true, Visible = false };
    private readonly Button _next = new SetupButton { Text = "Next", AutoSize = true, Enabled = false, Primary = true };
    private readonly Button _check = new SetupButton { Text = "Check again", AutoSize = true };
    private readonly TextBox _folder = new() { Text = Installation.Folder, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = SetupTheme.Surface, ForeColor = SetupTheme.Text };
    private readonly Button _browse = new SetupButton { Text = "Browse…", AutoSize = true };
    private readonly Button _showDetails = new SetupButton { Text = "Show details", AutoSize = true };
    private readonly Button _openLog = new SetupButton { Text = "Open log", AutoSize = true, Enabled = false };
    private readonly Queue<string> _activity = new();
    private SetupDetailsForm? _detailsWindow;
    private string? _log;
    private InstalledWidget? _installed;
    private UpdateResult? _latest;
    private bool _busy;
    private bool _finished;
    private CancellationTokenSource? _downloadCancellation;
    private bool _msiRunning;

    internal SetupForm(UpdateChecker? checker = null, Func<InstalledWidget?>? readInstallation = null)
    {
        _checker = checker ?? new();
        _readInstallation = readInstallation ?? Installation.Read;
        Text = "Codex Usage Setup";
        Font = new Font("Segoe UI", 10);
        BackColor = SystemInformation.HighContrast ? SystemColors.Control : SetupTheme.Background;
        ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : SetupTheme.Text;
        _details.ForeColor = _status.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : SetupTheme.Muted;
        if (SystemInformation.HighContrast) { _folder.BackColor = SystemColors.Window; _folder.ForeColor = SystemColors.WindowText; }
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 580);
        MinimumSize = SizeFromClientSize(ClientSize);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++) layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Codex Usage Widget", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), AutoSize = true }, 0, 0);
        layout.Controls.Add(_details, 0, 1);
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        options.Controls.AddRange(new Control[] { _install, _repair, _uninstall, _desktop, _launch });
        layout.Controls.Add(options, 0, 2);
        var location = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        location.ColumnStyles.Add(new(SizeType.Percent, 100));
        location.ColumnStyles.Add(new(SizeType.AutoSize));
        var locationLabel = new Label { Text = "Application folder (choose a dedicated, user-writable folder)", AutoSize = true };
        location.Controls.Add(locationLabel, 0, 0);
        location.SetColumnSpan(locationLabel, 2);
        var pathField = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, BackColor = _folder.BackColor, Padding = new Padding(10, 11, 10, 8), MinimumSize = new Size(0, 42) };
        _folder.Dock = DockStyle.Top;
        pathField.Controls.Add(_folder);
        location.Controls.Add(pathField, 0, 1);
        location.Controls.Add(_browse, 1, 1);
        layout.Controls.Add(location, 0, 3);
        layout.Controls.Add(new Label { Text = "Settings are preserved. Close the widget before continuing.\nUpdates and repairs keep the existing application folder.", AutoSize = true, Dock = DockStyle.Fill }, 0, 4);
        layout.Controls.Add(_status, 0, 5);
        var diagnostics = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        diagnostics.Controls.AddRange(new Control[] { _showDetails, _openLog });
        layout.Controls.Add(diagnostics, 0, 6);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new SetupButton { Text = "Close", AutoSize = true };
        buttons.Controls.AddRange(new Control[] { cancel, _next, _check });
        layout.Controls.Add(buttons, 0, 8);
        foreach (Control control in layout.Controls) control.Margin = new Padding(0, 0, 0, 14);
        Controls.Add(layout);
        _check.Click += async (_, _) => await CheckAsync();
        _next.Click += async (_, _) => { if (_finished) await FinishAsync(); else await ApplyAsync(); };
        _browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Choose the dedicated Codex Usage Widget application folder",
                UseDescriptionForTitle = true, AutoUpgradeEnabled = true, ShowNewFolderButton = true, SelectedPath = _folder.Text,
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try { _folder.Text = Installation.NormalizeFolder(picker.SelectedPath); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message); }
        };
        _showDetails.Click += (_, _) =>
        {
            if (_detailsWindow is null || _detailsWindow.IsDisposed)
                _detailsWindow = new(() => string.Join(Environment.NewLine, _activity), () => _log);
            if (!_detailsWindow.Visible) _detailsWindow.Show(this);
            else _detailsWindow.Activate();
        };
        _openLog.Click += (_, _) =>
        {
            try
            {
                if (_log is null || !File.Exists(_log)) throw new InvalidOperationException("No Windows Installer log has been created yet.");
                var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { UseShellExecute = false };
                command.ArgumentList.Add(_log);
                Process.Start(command);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { SetStatus("Could not open the log: " + ex.Message); }
        };
        cancel.Click += (_, _) => Close();
        Shown += async (_, _) => await CheckAsync();
        Shown += (_, _) => { _folder.SelectionStart = _folder.SelectionLength = 0; _next.Select(); };
        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                if (_msiRunning) SetStatus("Wait for Windows Installer to finish or cancel it in its progress window.");
                else { _downloadCancellation?.Cancel(); SetStatus("Cancelling the download…"); }
            }
            else _closing.Cancel();
        };
        FormClosed += (_, _) => { _detailsWindow?.Dispose(); _checker.Dispose(); _closing.Dispose(); };
    }

    internal void SetStatus(string message)
    {
        _status.Text = message;
        _activity.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (_activity.Count > 64) _activity.Dequeue();
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _check.Enabled = _next.Enabled = false;
        _install.Enabled = _repair.Enabled = _uninstall.Enabled = false;
        _browse.Enabled = false;
        SetStatus("Checking latest stable GitHub release…");
        try
        {
            _installed = _readInstallation();
            if (_installed is not null) _folder.Text = _installed.Folder;
            _latest = await _checker.FindInstallerAsync(cancellationToken: _closing.Token);
            if (IsDisposed || _closing.IsCancellationRequested) return;
            _details.Text = $"Installed: {(_installed is null ? "not installed" : "v" + _installed.Version)}\nAvailable: {(_latest.Version is null ? "unavailable" : "v" + _latest.Version)}\nFor the current Windows user only.";
            _install.Enabled = _latest.DownloadUri is not null && _latest.Sha256 is not null &&
                (_installed is null || Version.Parse(_latest.Version!) > _installed.Version);
            _repair.Enabled = _uninstall.Enabled = _installed is not null;
            if (!_install.Enabled && _repair.Enabled) _repair.Checked = true;
            _next.Enabled = _install.Enabled || _repair.Enabled;
            _browse.Enabled = _installed is null;
            SetStatus(_latest.IsError || _latest.DownloadUri is null ? _latest.Message :
                _latest.Sha256 is null ? "Release is missing its installer digest. Installation is disabled." :
                _install.Enabled ? "Choose an action, then Next. Downloads are verified before installation." : "You're up to date. Repair and uninstall are available.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { if (!IsDisposed) SetStatus(ex.Message); }
        finally { if (!IsDisposed) _check.Enabled = true; }
    }

    private async Task ApplyAsync()
    {
        if (_busy || !_next.Enabled) return;
        var operation = _uninstall.Checked ? "uninstall" : _repair.Checked ? "repair" : "install";
        if (operation == "uninstall" && MessageBox.Show(this, "Uninstall Codex Usage Widget? Your settings will be kept.", "Confirm uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var destination = _installed?.Folder ?? _folder.Text;
        if (operation == "install" && MessageBox.Show(this, $"Install/update Codex Usage Widget in:\n\n{destination}\n\nApplication files in this folder may be replaced. Continue?", "Confirm application folder", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _busy = true;
        _next.Enabled = _check.Enabled = _install.Enabled = _repair.Enabled = _uninstall.Enabled = _browse.Enabled = false;
        string? temporary = null;
        string? expectedDigest = null;
        try
        {
            if (_readInstallation() != _installed) throw new InvalidOperationException("The installation changed. Check again before continuing.");
            destination = Installation.NormalizeFolder(destination);
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "SetupLogs");
            Directory.CreateDirectory(logDirectory);
            var log = Path.Combine(logDirectory, $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            _log = log;
            _openLog.Enabled = false;
            SetStatus($"Action: {operation}; application folder: {destination}");
            string target;
            Version? expected = _installed?.Version;
            if (operation == "uninstall") target = _installed!.ProductCode;
            else
            {
                SetStatus("Finding and downloading the installer…");
                var release = operation == "repair" ? await _checker.FindInstallerAsync(_installed!.Version, _closing.Token) : _latest!;
                if (release.DownloadUri is null || release.Version is null) throw new InvalidOperationException(release.Message);
                expected = Version.Parse(release.Version);
                if (operation == "repair" && expected != _installed!.Version) throw new InvalidOperationException("Repair requires the exact installed release.");
                temporary = Directory.CreateTempSubdirectory("CodexUsageSetup-").FullName;
                target = Path.Combine(temporary, "installer.msi");
                using var downloader = new PackageDownloader();
                using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                _downloadCancellation = downloadCancellation;
                await downloader.DownloadAsync(release, target, downloadCancellation.Token);
                _downloadCancellation = null;
                expectedDigest = release.Sha256;
                SetStatus("Download completed; installer SHA-256 verified.");
            }
            SetStatus("Windows Installer is working. Show details follows its live log; follow any files-in-use prompts.");
            // Hold the downloaded package read-only until MSI finishes to prevent modification.
            using var packageLock = operation == "uninstall" ? null : File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (packageLock is not null && !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(packageLock)), expectedDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded package changed. Nothing was installed.");
            _msiRunning = true;
            using var process = Process.Start(Installation.Command(operation, target, log, operation == "install" ? destination : null)) ?? throw new InvalidOperationException("Could not start Windows Installer.");
            await process.WaitForExitAsync(); // Do not abandon or kill an active transaction when closing.
            SetStatus($"Windows Installer exit code: {process.ExitCode}. Log: {log}");
            if (Installation.Failure(process.ExitCode) is string error) throw new InvalidOperationException($"{error}\nLog: {log}");
            var actual = _readInstallation();
            if (operation == "uninstall" ? actual is not null : actual?.Version != expected || !string.Equals(actual?.Folder, destination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Windows Installer returned success, but the installation record did not match. Log: {log}");
            ShowCompletion(actual?.Version, operation == "uninstall", process.ExitCode == 3010);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { SetStatus(ex is OperationCanceledException ? "Download cancelled or timed out. Nothing was installed." : ex.Message); }
        finally
        {
            _downloadCancellation = null;
            _msiRunning = false;
            if (temporary is not null)
            {
                try { File.Delete(Path.Combine(temporary, "installer.msi")); Directory.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Leave only this temporary package for later cleanup. */ }
            }
            _busy = false;
            _openLog.Enabled = _log is not null && File.Exists(_log);
            _next.Enabled = true;
            if (!_finished) { _check.Enabled = true; _install.Enabled = _repair.Enabled = _uninstall.Enabled = false; _next.Enabled = false; }
        }
    }

    internal void ShowCompletion(Version? version, bool removed, bool restart)
    {
        _finished = true;
        _install.Visible = _repair.Visible = _uninstall.Visible = _check.Visible = false;
        _browse.Visible = _folder.Parent!.Parent!.Visible = false;
        _desktop.Visible = _launch.Visible = !removed;
        _launch.Checked = false;
        _details.Text = removed ? "Uninstalled. Your saved settings were kept." : $"Ready: Codex Usage Widget v{version}";
        SetStatus(restart ? "Completed. Windows requests a restart; start the app after restarting." : "Completed successfully. Choose any finish options below.");
        _launch.Enabled = !restart;
        _next.Text = "Finish";
    }

    private async Task FinishAsync()
    {
        if (_busy) return;
        _busy = true;
        _next.Enabled = false;
        try
        {
            if (_desktop.Visible && _desktop.Checked)
            {
                var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Codex Usage Widget.lnk");
                if (File.Exists(shortcut)) SetStatus("Existing desktop shortcut left unchanged.");
                else
                {
                    var installed = _readInstallation() ?? throw new InvalidOperationException("The widget is no longer installed.");
                    var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "SetupLogs", $"shortcut-{Guid.NewGuid():N}.log");
                    _log = log;
                    _msiRunning = true;
                    using var process = Process.Start(Installation.Command("shortcut", installed.ProductCode, log)) ?? throw new InvalidOperationException("Could not add the shortcut.");
                    await process.WaitForExitAsync();
                    if (Installation.Failure(process.ExitCode) is string error) throw new InvalidOperationException($"{error}\nLog: {log}");
                    if (!File.Exists(shortcut)) throw new InvalidOperationException($"Windows Installer did not create the desktop shortcut. Log: {log}");
                    if (process.ExitCode == 3010)
                    {
                        _launch.Enabled = false;
                        SetStatus("Shortcut added. Windows requests a restart; start the app after restarting.");
                        return;
                    }
                }
            }
            if (_launch.Visible && _launch.Enabled && _launch.Checked)
            {
                var installed = _readInstallation() ?? throw new InvalidOperationException("The widget is no longer installed.");
                Process.Start(new ProcessStartInfo(Path.Combine(installed.Folder, "CodexUsageWidget.exe")) { UseShellExecute = true });
            }
            _busy = false;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { SetStatus("Installation completed, but the finish option failed: " + ex.Message); }
        finally { _msiRunning = _busy = false; if (!IsDisposed) { _next.Enabled = true; _openLog.Enabled = _log is not null && File.Exists(_log); } }
    }
}
