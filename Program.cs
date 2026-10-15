using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace CodexUsageWidget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new UsageForm());
    }
}

internal sealed class UsageForm : Form
{
    private readonly Label _status = new();
    private readonly Label _footer = new();
    private readonly FlowLayoutPanel _windows = new BufferedFlowLayoutPanel();
    private readonly Button _connect = new();
    private readonly Button _refresh = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 60_000 };
    private readonly WidgetSettings _settings;
    private readonly string? _settingsPath;
    private readonly UpdateChecker _updateChecker;
    private readonly CancellationTokenSource _closing = new();
    private AppServerClient? _server;
    private bool _connected;
    private int _appliedFontSize = 9;

    public UsageForm(bool startServer = true, string? settingsPath = null, UpdateChecker? updateChecker = null)
    {
        _updateChecker = updateChecker ?? new UpdateChecker();
        _settingsPath = settingsPath;
        _settings = WidgetSettings.Load(settingsPath);
        Text = "Codex Usage";
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        DoubleBuffered = true;
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowIcon = true;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        TopMost = _settings.AlwaysOnTop;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(25, 27, 31);
        ForeColor = Color.FromArgb(238, 240, 243);
        ClientSize = new Size(380, 410);
        Font = new Font("Segoe UI", 9F);

        var tabs = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BackColor,
        };
        tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tabs.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        tabs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var tabHeader = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            BackColor = BackColor,
        };
        tabHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tabHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tabHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        tabHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 3));
        var usageTab = CreateTabButton("Usage");
        var settingsTab = CreateTabButton("Settings");
        var usageIndicator = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var settingsIndicator = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        tabHeader.Controls.Add(usageTab, 0, 0);
        tabHeader.Controls.Add(settingsTab, 1, 0);
        tabHeader.Controls.Add(usageIndicator, 0, 1);
        tabHeader.Controls.Add(settingsIndicator, 1, 1);

        var content = new BufferedPanel { Dock = DockStyle.Fill, BackColor = BackColor, Margin = Padding.Empty };
        var usagePage = new BufferedPanel { Dock = DockStyle.Fill, BackColor = BackColor };
        var settingsPage = new BufferedPanel { Dock = DockStyle.Fill, BackColor = BackColor };
        content.Controls.Add(usagePage);
        content.Controls.Add(settingsPage);
        void SelectTab(bool settings)
        {
            usagePage.Visible = !settings;
            settingsPage.Visible = settings;
            if (settings) settingsPage.BringToFront();
            else usagePage.BringToFront();
            usageTab.BackColor = settings ? BackColor : Color.FromArgb(35, 39, 46);
            settingsTab.BackColor = settings ? Color.FromArgb(35, 39, 46) : BackColor;
            usageTab.ForeColor = settings ? Color.FromArgb(175, 181, 191) : Color.White;
            settingsTab.ForeColor = settings ? Color.White : Color.FromArgb(175, 181, 191);
            usageIndicator.BackColor = settings ? Color.FromArgb(49, 55, 64) : Color.FromArgb(42, 185, 160);
            settingsIndicator.BackColor = settings ? Color.FromArgb(42, 185, 160) : Color.FromArgb(49, 55, 64);
        }
        usageTab.Click += (_, _) => SelectTab(false);
        settingsTab.Click += (_, _) => SelectTab(true);
        SelectTab(false);
        tabs.Controls.Add(tabHeader, 0, 0);
        tabs.Controls.Add(content, 0, 1);

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 7,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Usage remaining",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
        };
        root.Controls.Add(title, 0, 0);

        _refresh.Text = "Refresh";
        StyleButton(_refresh, primary: true);
        _refresh.AutoSize = true;
        _refresh.MinimumSize = new Size(108, 30);
        _refresh.Margin = new Padding(0, 8, 0, 0);
        _refresh.Text = "↻  Refresh";
        _refresh.Click += (_, _) => RefreshUsage();
        root.Controls.Add(_refresh, 0, 3);
        root.SetColumnSpan(_refresh, 2);

        _status.Text = "Starting local Codex connection…";
        _status.AutoSize = true;
        _status.Dock = DockStyle.Fill;
        _status.Margin = new Padding(0, 8, 0, 0);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(175, 181, 191);
        root.Controls.Add(_status, 0, 2);
        root.SetColumnSpan(_status, 2);

        _windows.Dock = DockStyle.Fill;
        _windows.FlowDirection = FlowDirection.TopDown;
        _windows.WrapContents = false;
        _windows.AutoScroll = true;
        _windows.BackColor = BackColor;
        _windows.Margin = Padding.Empty;
        _windows.SizeChanged += (_, _) => ResizeUsageRows();
        root.Controls.Add(_windows, 0, 1);
        root.SetColumnSpan(_windows, 2);

        _connect.Text = "Connect with ChatGPT";
        StyleButton(_connect, primary: true);
        _connect.AutoSize = true;
        _connect.Dock = DockStyle.Fill;
        _connect.Margin = new Padding(0, 8, 0, 0);
        _connect.Visible = false;
        _connect.Click += (_, _) => ConnectWithChatGpt();
        root.Controls.Add(_connect, 0, 4);
        root.SetColumnSpan(_connect, 2);

        var version = typeof(UsageForm).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        _footer.AutoSize = true;
        _footer.Dock = DockStyle.Fill;
        _footer.Margin = new Padding(0, 8, 0, 0);
        _footer.TextAlign = ContentAlignment.MiddleCenter;
        _footer.ForeColor = Color.FromArgb(125, 132, 143);
        _footer.Font = new Font("Segoe UI", 8F);
        UpdateFooter(version);
        root.Controls.Add(_footer, 0, 6);
        root.SetColumnSpan(_footer, 2);

        root.Layout += (_, _) =>
        {
            var cardsHeight = _windows.Controls.Cast<Control>().Sum(card => card.Height + card.Margin.Vertical);
            var reserved = root.Padding.Vertical + root.RowStyles[0].Height +
                _status.PreferredHeight + _status.Margin.Vertical + _refresh.PreferredSize.Height + _refresh.Margin.Vertical +
                (_connect.Visible ? _connect.PreferredSize.Height + _connect.Margin.Vertical : 0) +
                _footer.PreferredHeight + _footer.Margin.Vertical;
            var height = Math.Max(0, Math.Min(cardsHeight, root.ClientSize.Height - reserved));
            if (Math.Abs(root.RowStyles[1].Height - height) > 1) root.RowStyles[1].Height = height;
        };
        _windows.Layout += (_, _) => { ResizeUsageRows(); root.PerformLayout(); };
        _connect.VisibleChanged += (_, _) => root.PerformLayout();
        usagePage.Controls.Add(root);
        BuildSettingsPage(settingsPage, version);
        Controls.Add(tabs);
        ApplyFontSize();
        var settingsLayout = (TableLayoutPanel)settingsPage.Controls[0];
        var preferred = settingsLayout.GetPreferredSize(new Size(ClientSize.Width, 0));
        MinimumSize = SizeFromClientSize(new Size(ClientSize.Width, preferred.Height + 34));
        ClientSize = new Size(ClientSize.Width, Math.Max(preferred.Height + 34, 350));
        PlaceAtTopRight();
        if (startServer) Shown += (_, _) => StartServer();
        FormClosed += (_, _) =>
        {
            _closing.Cancel();
            _updateChecker.Dispose();
            _closing.Dispose();
            _poll.Stop();
            _poll.Dispose();
            _server?.Dispose();
        };
        _poll.Tick += (_, _) => RefreshUsage();
        ApplyRefreshInterval();
    }

    private static Button CreateTabButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            AccessibleRole = AccessibleRole.PageTab,
            AccessibleName = $"{text} tab",
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void BuildSettingsPage(Panel page, string version)
    {
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 13,
            BackColor = BackColor,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 10; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = "Widget settings",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var alwaysOnTop = new CheckBox
        {
            AutoSize = true,
            Text = "Always on top",
            Checked = _settings.AlwaysOnTop,
            Dock = DockStyle.Fill,
            ForeColor = ForeColor,
            BackColor = BackColor,
        };
        layout.Controls.Add(alwaysOnTop, 0, 1);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Automatic refresh",
            Dock = DockStyle.Fill,
            ForeColor = ForeColor,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 2);

        var refreshInterval = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(38, 41, 47),
            ForeColor = ForeColor,
        };
        refreshInterval.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? Color.FromArgb(50, 55, 63) : refreshInterval.BackColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, refreshInterval.Items[e.Index]?.ToString() ?? string.Empty, refreshInterval.Font,
                e.Bounds, refreshInterval.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        refreshInterval.Items.AddRange(new object[] { "Off", "Every minute", "Every 5 minutes", "Every 15 minutes" });
        refreshInterval.SelectedIndex = _settings.RefreshMinutes switch { 0 => 0, 5 => 2, 15 => 3, _ => 1 };
        layout.Controls.Add(refreshInterval, 1, 2);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Colour thresholds · % remaining",
            Dock = DockStyle.Fill,
            ForeColor = ForeColor,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 3);

        TextBox AddThresholdInput(string label, int value, int row)
        {
            layout.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = ForeColor,
            }, 0, row);
            var input = new TextBox
            {
                Text = value.ToString(CultureInfo.InvariantCulture),
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(38, 41, 47),
                ForeColor = ForeColor,
                BorderStyle = BorderStyle.FixedSingle,
                AccessibleName = label,
            };
            layout.Controls.Add(input, 1, row);
            return input;
        }
        var criticalThreshold = AddThresholdInput("Red below (%)", _settings.CriticalThresholdPercent, 4);
        var warningThreshold = AddThresholdInput("Amber below (%)", _settings.WarningThresholdPercent, 5);
        var applyThresholds = new Button { Text = "Apply thresholds", AutoSize = true, MinimumSize = new Size(150, 30) };
        StyleButton(applyThresholds, primary: true);
        layout.Controls.Add(applyThresholds, 1, 6);

        var saveStatus = new Label
        {
            AutoSize = true,
            Text = "Other settings autosave. Thresholds need Apply.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(151, 158, 169),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        layout.Controls.Add(saveStatus, 0, 7);

        applyThresholds.Click += (_, _) =>
        {
            if (!WidgetSettings.TryParseThresholds(criticalThreshold.Text, warningThreshold.Text, out var critical, out var warning))
            {
                saveStatus.Text = "Use whole numbers: 0 ≤ red < amber ≤ 100.\nThresholds unchanged.";
                saveStatus.ForeColor = Color.FromArgb(226, 95, 95);
                return;
            }
            _settings.CriticalThresholdPercent = critical;
            _settings.WarningThresholdPercent = warning;
            foreach (var row in _windows.Controls.OfType<UsageRow>()) row.ApplyThresholds(_settings);
            SaveSettings(saveStatus);
        };

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"v{version}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(125, 132, 143),
            Font = new Font("Segoe UI", 8F),
        }, 0, 12);

        var updateActions = new BufferedFlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, WrapContents = false,
            Margin = Padding.Empty, BackColor = BackColor,
        };
        var checkUpdates = new Button { Text = "Check for updates", AutoSize = true, MinimumSize = new Size(150, 30) };
        StyleButton(checkUpdates, primary: false);
        var downloadUpdate = new LinkLabel
        {
            Text = "Download installer", AutoSize = true, Visible = false,
            LinkColor = Color.FromArgb(42, 185, 160), ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(42, 185, 160), Margin = new Padding(8, 8, 0, 0),
        };
        var updateStatus = new Label
        {
            Text = "Updates are checked only when requested.", Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(151, 158, 169), TextAlign = ContentAlignment.TopLeft,
            AccessibleName = "Update check result",
        };
        Uri? downloadUri = null;
        updateActions.Controls.Add(checkUpdates);
        updateActions.Controls.Add(downloadUpdate);
        layout.Controls.Add(updateActions, 0, 9);
        layout.Controls.Add(updateStatus, 0, 10);
        downloadUpdate.LinkClicked += (_, _) =>
        {
            if (downloadUri is null) return;
            try { Process.Start(new ProcessStartInfo(downloadUri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                updateStatus.Text = "Could not open your browser. Try again.";
                updateStatus.ForeColor = Color.FromArgb(226, 95, 95);
            }
        };
        checkUpdates.Click += async (_, _) =>
        {
            if (!checkUpdates.Enabled) return;
            checkUpdates.Enabled = false;
            downloadUpdate.Visible = false;
            downloadUri = null;
            updateStatus.Text = "Checking for updates…";
            updateStatus.ForeColor = Color.FromArgb(151, 158, 169);
            try
            {
                var result = await _updateChecker.CheckAsync(new Version(version), _closing.Token);
                if (IsDisposed || _closing.IsCancellationRequested) return;
                updateStatus.Text = result.Message;
                updateStatus.ForeColor = result.IsError ? Color.FromArgb(226, 95, 95) : Color.FromArgb(151, 158, 169);
                downloadUri = result.DownloadUri;
                downloadUpdate.Visible = downloadUri is not null;
            }
            catch (OperationCanceledException) { }
            finally { if (!IsDisposed) checkUpdates.Enabled = true; }
        };

        var fontLabel = new Label
        {
            AutoSize = true,
            Text = $"Font size: {_settings.FontSize} pt",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var fontSize = new TrackBar
        {
            Minimum = 9, Maximum = 14, Value = _settings.FontSize,
            TickFrequency = 1, Dock = DockStyle.Fill,
            AccessibleName = "Font size",
        };
        layout.Controls.Add(fontLabel, 0, 8);
        layout.Controls.Add(fontSize, 1, 8);
        fontSize.ValueChanged += (_, _) =>
        {
            _settings.FontSize = fontSize.Value;
            fontLabel.Text = $"Font size: {fontSize.Value} pt";
            ApplyFontSize();
            SaveSettings(saveStatus);
        };

        alwaysOnTop.CheckedChanged += (_, _) =>
        {
            _settings.AlwaysOnTop = alwaysOnTop.Checked;
            TopMost = alwaysOnTop.Checked;
            SaveSettings(saveStatus);
        };
        refreshInterval.SelectedIndexChanged += (_, _) =>
        {
            _settings.RefreshMinutes = refreshInterval.SelectedIndex switch { 0 => 0, 2 => 5, 3 => 15, _ => 1 };
            ApplyRefreshInterval();
            UpdateFooter(version);
            SaveSettings(saveStatus);
        };
        foreach (Control control in layout.Controls)
        {
            if (layout.GetRow(control) is 0 or 3 or 7 or 9 or 10 or 12) layout.SetColumnSpan(control, 2);
            control.Margin = new Padding(3, 4, 3, 4);
        }
        page.Controls.Add(layout);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private void ApplyFontSize()
    {
        if (_appliedFontSize == _settings.FontSize) return;
        var ratio = (float)_settings.FontSize / _appliedFontSize;
        var fonts = Descendants(this).Prepend(this)
            .Select(control => (Control: control, Font: control.Font)).ToArray();
        foreach (var item in fonts) item.Control.SuspendLayout();
        Scale(new SizeF(ratio, ratio));
        foreach (var item in fonts)
            item.Control.Font = new Font(item.Font.FontFamily, item.Font.SizeInPoints * ratio, item.Font.Style);
        _appliedFontSize = _settings.FontSize;
        foreach (var item in fonts.Reverse()) item.Control.ResumeLayout(true);
        ResizeUsageRows();
    }

    private void ApplyRefreshInterval()
    {
        _poll.Stop();
        if (_settings.RefreshMinutes == 0) return;
        _poll.Interval = _settings.RefreshMinutes * 60_000;
        _poll.Start();
    }

    private void UpdateFooter(string version)
    {
        var refreshText = _settings.RefreshMinutes switch
        {
            0 => "Scheduled refresh off",
            1 => "Updates automatically every minute",
            _ => $"Updates automatically every {_settings.RefreshMinutes} minutes",
        };
        _footer.Text = $"{refreshText} · v{version}";
    }

    private void SaveSettings(Label status)
    {
        try
        {
            _settings.Save(_settingsPath);
            status.Text = "Changes saved.";
            status.ForeColor = Color.FromArgb(151, 158, 169);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = "Could not save settings; changes last until close.";
            status.ForeColor = Color.FromArgb(226, 95, 95);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        try
        {
            var dark = 1;
            var result = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            if (result != 0) DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));

            var roundedCorners = 2;
            DwmSetWindowAttribute(Handle, 33, ref roundedCorners, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 86);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(30, 166, 170) : Color.FromArgb(50, 55, 63);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(18, 126, 132) : Color.FromArgb(31, 34, 39);
        button.BackColor = primary ? Color.FromArgb(23, 148, 154) : Color.FromArgb(38, 41, 47);
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
    }

    private void PlaceAtTopRight()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 18, area.Top + 72);
    }

    private void StartServer()
    {
        try
        {
            _server = new AppServerClient(FindCodexExecutable());
            _server.MessageReceived += HandleServerMessage;
            _server.Failed += message => OnUi(() => ShowDisconnected(message));
            _server.Start();
        }
        catch (Exception ex)
        {
            ShowDisconnected(ex.Message);
        }
    }

    private static string FindCodexExecutable()
    {
        var binRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(binRoot))
        {
            var candidate = Directory.EnumerateDirectories(binRoot)
                .Select(path => Path.Combine(path, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (candidate is not null) return candidate;
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var candidate = Path.Combine(entry.Trim(), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not find codex.exe. Install Codex CLI or add it to PATH.");
    }

    private void ConnectWithChatGpt()
    {
        _connect.Enabled = false;
        _status.Text = "Opening ChatGPT sign-in in your browser…";
        try
        {
            (_server ?? throw new InvalidOperationException("Codex connection is unavailable."))
                .StartChatGptLogin();
        }
        catch (Exception ex)
        {
            _connect.Enabled = true;
            _status.Text = "Could not start sign-in.";
            MessageBox.Show(this, ex.Message, "Codex Usage Widget", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HandleServerMessage(JsonElement message)
    {
        if (message.TryGetProperty("id", out var id) && id.TryGetInt32(out var requestId))
        {
            if (requestId == 1)
            {
                _server?.SendNotification("initialized", new { });
                _server?.ReadAccount();
            }
            else if (requestId == 2)
            {
                var account = message.TryGetProperty("result", out var result) && result.TryGetProperty("account", out var accountData)
                    ? accountData
                    : default;
                var hasAccount = account.ValueKind == JsonValueKind.Object &&
                    account.TryGetProperty("type", out var type) &&
                    type.GetString() == "chatgpt";
                OnUi(() =>
                {
                    _connected = hasAccount;
                    _connect.Visible = !hasAccount;
                    _status.Text = hasAccount ? "Connected · ChatGPT plan" : "Connect once to read your plan usage.";
                });
                if (hasAccount) _server?.ReadRateLimits();
            }
            else if (requestId == 3)
            {
                if (message.TryGetProperty("result", out var rateResult))
                    OnUi(() => RenderRateLimits(rateResult));
            }
            else if (requestId == 4)
            {
                var authUrl = message.TryGetProperty("result", out var loginResult) &&
                    loginResult.TryGetProperty("authUrl", out var url) ? url.GetString() : null;
                if (!string.IsNullOrWhiteSpace(authUrl))
                {
                    try { Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true }); }
                    catch (Exception ex) { OnUi(() => MessageBox.Show(this, ex.Message, "Could not open browser", MessageBoxButtons.OK, MessageBoxIcon.Error)); }
                }
                OnUi(() => _connect.Enabled = true);
            }
            return;
        }

        if (message.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString();
            if (method == "account/login/completed" && message.TryGetProperty("params", out var loginParams) &&
                loginParams.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            {
                _server?.ReadAccount();
            }
            else if (method == "account/rateLimits/updated" && message.TryGetProperty("params", out var parameters))
            {
                OnUi(() => RenderRateLimits(parameters));
            }
        }
    }

    private void RefreshUsage()
    {
        if (_server is null) return;
        if (_connected) _server.ReadRateLimits();
    }

    private void RenderRateLimits(JsonElement data)
    {
        var buckets = data.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object
            ? byId
            : data.TryGetProperty("rateLimits", out var single) ? single : default;

        var rows = new List<UsageWindow>();
        if (buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in buckets.EnumerateObject())
            {
                var name = bucket.Value.TryGetProperty("limitName", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString()
                    : null;
                var fallbackName = string.IsNullOrWhiteSpace(name) ? bucket.Name : name!;
                AddWindow(bucket.Value, "primary", fallbackName, rows);
                AddWindow(bucket.Value, "secondary", fallbackName, rows);
            }
        }

        if (rows.Count == 0)
        {
            foreach (Control old in _windows.Controls.Cast<Control>().ToArray()) old.Dispose();
            _status.Text = "Usage data isn’t available yet. Try Refresh.";
            return;
        }

        _windows.SuspendLayout();
        foreach (Control old in _windows.Controls.Cast<Control>().ToArray()) old.Dispose();
        // Rows arrive after WinForms has scaled the form; match its logical layout scale.
        var scale = _windows.Parent is TableLayoutPanel layout ? layout.RowStyles[0].Height / 34f : DeviceDpi / 96f;
        foreach (var row in rows.OrderBy(x => x.DurationMinutes == 0 ? int.MaxValue : x.DurationMinutes))
        {
            var tile = new UsageRow(row, _settings);
            tile.Font = Font;
            if (scale != 1f) tile.Scale(new SizeF(scale, scale));
            foreach (var label in Descendants(tile).OfType<Label>())
                label.Font = new Font(label.Font.FontFamily, label.Font.SizeInPoints * _settings.FontSize / 9f, label.Font.Style);
            _windows.Controls.Add(tile);
        }
        var lastTile = _windows.Controls[_windows.Controls.Count - 1];
        lastTile.Margin = new Padding(lastTile.Margin.Left, lastTile.Margin.Top, lastTile.Margin.Right, 0);
        _windows.ResumeLayout(true);
        _windows.Parent?.PerformLayout();
        ResizeUsageRows();
        _windows.PerformLayout();
        _status.Text = $"Updated {DateTime.Now:t} · {rows.Count} usage window{(rows.Count == 1 ? "" : "s")}";
    }

    private static void AddWindow(JsonElement bucket, string key, string bucketName, List<UsageWindow> rows)
    {
        if (!bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetDouble(out var used)) return;

        var duration = window.TryGetProperty("windowDurationMins", out var durationElement) && durationElement.TryGetInt32(out var mins)
            ? mins : 0;
        DateTimeOffset? resets = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) && resetElement.TryGetInt64(out var unix))
        {
            try { resets = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { }
        }

        var label = duration switch
        {
            300 => "5-hour",
            10080 => "Weekly",
            60 => "1-hour",
            > 0 => FormatDuration(duration),
            _ => bucketName,
        };
        rows.Add(new UsageWindow(label, bucketName, Math.Clamp(100 - used, 0, 100), duration, resets));
    }

    private void ResizeUsageRows()
    {
        foreach (Control row in _windows.Controls)
        {
            var width = Math.Max(1, _windows.ClientSize.Width - row.Margin.Horizontal - _windows.Padding.Horizontal);
            if (row.Width != width) row.Width = width;
        }
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes % 10080 == 0) return $"{minutes / 10080} week" + (minutes == 10080 ? "" : "s");
        if (minutes % 1440 == 0) return $"{minutes / 1440} day" + (minutes == 1440 ? "" : "s");
        if (minutes % 60 == 0) return $"{minutes / 60} hour" + (minutes == 60 ? "" : "s");
        return $"{minutes} min";
    }

    private void ShowDisconnected(string message)
    {
        _connected = false;
        _connect.Visible = true;
        _status.Text = message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
            ? "Connect once to read your plan usage."
            : "Codex connection unavailable.";
    }

    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private sealed class BufferedTableLayoutPanel : TableLayoutPanel
    {
        public BufferedTableLayoutPanel() => DoubleBuffered = true;
    }

    private sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
    {
        public BufferedFlowLayoutPanel() => DoubleBuffered = true;
    }

    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel() => DoubleBuffered = true;
    }

    private sealed record UsageWindow(string Label, string Bucket, double Remaining, int DurationMinutes, DateTimeOffset? ResetsAt);

    private sealed class UsageRow : RoundedCard
    {
        private readonly RoundedProgressBar _progress;

        public UsageRow(UsageWindow usage, WidgetSettings settings)
        {
            Width = 310;
            Height = 78;
            Margin = new Padding(0, 3, 0, 6);
            BackColor = Color.FromArgb(35, 39, 46);
            BorderColor = Color.FromArgb(49, 55, 64);

            var header = new Label
            {
                Text = usage.Label,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                BackColor = Color.Transparent,
                Location = new Point(12, 8),
                AutoSize = true,
            };
            var remaining = new Label
            {
                Font = new Font("Segoe UI", 9F),
                Text = $"{Math.Round(usage.Remaining):0}% left",
                ForeColor = Color.FromArgb(206, 211, 220),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(214, 9),
                Size = new Size(78, 18),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            _progress = new RoundedProgressBar
            {
                Location = new Point(12, 35),
                Size = new Size(286, 9),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Remaining = usage.Remaining,
                FillColor = settings.GetUsageColor(usage.Remaining),
            };
            var resetText = usage.ResetsAt is null
                ? "Reset time unavailable"
                : usage.ResetsAt.Value.Date == DateTimeOffset.Now.Date
                    ? $"Resets {usage.ResetsAt.Value:t}"
                    : $"Resets {usage.ResetsAt.Value:ddd, MMM d}";
            var reset = new Label
            {
                Text = resetText,
                ForeColor = Color.FromArgb(151, 158, 169),
                Font = new Font("Segoe UI", 8F),
                BackColor = Color.Transparent,
                Location = new Point(12, 53),
                AutoSize = true,
            };
            var layout = new BufferedTableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 10),
                ColumnCount = 2, RowCount = 3, BackColor = Color.Transparent,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            header.AutoSize = true;
            header.Dock = DockStyle.Fill;
            remaining.AutoSize = true;
            remaining.Dock = DockStyle.Fill;
            reset.Dock = DockStyle.Fill;
            _progress.Dock = DockStyle.Fill;
            _progress.Margin = new Padding(0, 4, 0, 5);
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(remaining, 1, 0);
            layout.Controls.Add(_progress, 0, 1);
            layout.SetColumnSpan(_progress, 2);
            layout.Controls.Add(reset, 0, 2);
            layout.SetColumnSpan(reset, 2);
            Controls.Add(layout);
            layout.Layout += (_, _) =>
            {
                var height = layout.GetPreferredSize(new Size(Width, 0)).Height;
                if (height > 0 && Height != height) Height = height;
            };
        }

        public void ApplyThresholds(WidgetSettings settings)
        {
            _progress.FillColor = settings.GetUsageColor(_progress.Remaining);
            _progress.Invalidate();
        }
    }

    private class RoundedCard : Panel
    {
        public Color BorderColor { get; set; } = Color.FromArgb(49, 55, 64);

        public RoundedCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = CreateRoundedPath(rect, 12);
            using var fill = new SolidBrush(BackColor);
            using var border = new Pen(BorderColor, 1);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }
    }

    private sealed class RoundedProgressBar : Control
    {
        public double Remaining { get; set; }
        public Color FillColor { get; set; } = Color.FromArgb(42, 185, 160);

        public RoundedProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(64, 70, 79);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Control? background = Parent;
            while (background is not null && background.BackColor.A < 255) background = background.Parent;
            e.Graphics.Clear(background?.BackColor ?? Color.FromArgb(35, 39, 46));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (Width < 2 || Height < 2) return;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using var trackPath = CreateRoundedPath(track, track.Height / 2f);
            using var trackBrush = new SolidBrush(BackColor);
            e.Graphics.FillPath(trackBrush, trackPath);

            var fillWidth = (int)Math.Round((Width - 1) * Math.Clamp(Remaining, 0, 100) / 100d);
            if (fillWidth <= 0) return;
            var fill = new Rectangle(0, 0, fillWidth, Height - 1);
            using var fillPath = CreateRoundedPath(fill, Math.Min(fill.Height / 2f, fill.Width / 2f));
            using var fillBrush = new SolidBrush(FillColor);
            var state = e.Graphics.Save();
            e.Graphics.SetClip(trackPath);
            e.Graphics.FillPath(fillBrush, fillPath);
            e.Graphics.Restore(state);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedPath(Rectangle bounds, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Max(1, Math.Min(Math.Min(bounds.Width, bounds.Height), (int)Math.Round(radius * 2)));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class WidgetSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageWidget", "settings.json");

    public bool AlwaysOnTop { get; set; } = true;
    public int RefreshMinutes { get; set; } = 1;
    public int FontSize { get; set; } = 9;
    public int CriticalThresholdPercent { get; set; } = 15;
    public int WarningThresholdPercent { get; set; } = 35;

    internal static bool ValidThresholds(int critical, int warning) => critical >= 0 && critical < warning && warning <= 100;

    internal static bool TryParseThresholds(string criticalText, string warningText, out int critical, out int warning)
    {
        var criticalValid = int.TryParse(criticalText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out critical);
        var warningValid = int.TryParse(warningText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out warning);
        return criticalValid && warningValid && ValidThresholds(critical, warning);
    }

    internal Color GetUsageColor(double remaining) => remaining < CriticalThresholdPercent ? Color.FromArgb(226, 95, 95)
        : remaining < WarningThresholdPercent ? Color.FromArgb(230, 174, 82)
        : Color.FromArgb(42, 185, 160);

    internal static WidgetSettings FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var settings = new WidgetSettings();
        if (root.ValueKind != JsonValueKind.Object) return settings;
        if (root.TryGetProperty(nameof(AlwaysOnTop), out var top) && top.ValueKind is JsonValueKind.True or JsonValueKind.False)
            settings.AlwaysOnTop = top.GetBoolean();
        if (root.TryGetProperty(nameof(RefreshMinutes), out var refresh) && refresh.ValueKind == JsonValueKind.Number &&
            refresh.TryGetInt32(out var minutes) && minutes is 0 or 1 or 5 or 15)
            settings.RefreshMinutes = minutes;

        if (root.TryGetProperty(nameof(FontSize), out var font) && font.ValueKind == JsonValueKind.Number &&
            font.TryGetInt32(out var points) && points is >= 9 and <= 14)
            settings.FontSize = points;

        var critical = settings.CriticalThresholdPercent;
        var warning = settings.WarningThresholdPercent;
        var validCritical = !root.TryGetProperty(nameof(CriticalThresholdPercent), out var red) ||
            (red.ValueKind == JsonValueKind.Number && red.TryGetInt32(out critical));
        var validWarning = !root.TryGetProperty(nameof(WarningThresholdPercent), out var amber) ||
            (amber.ValueKind == JsonValueKind.Number && amber.TryGetInt32(out warning));
        if (validCritical && validWarning && ValidThresholds(critical, warning))
        {
            settings.CriticalThresholdPercent = critical;
            settings.WarningThresholdPercent = warning;
        }
        return settings;
    }

    public static WidgetSettings Load(string? path = null)
    {
        try
        {
            path ??= SettingsPath;
            if (!File.Exists(path)) return new WidgetSettings();
            return FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WidgetSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class AppServerClient : IDisposable
{
    private readonly Process _process;
    private readonly object _writeLock = new();

    public event Action<JsonElement>? MessageReceived;
    public event Action<string>? Failed;

    public AppServerClient(string codexExe)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = codexExe,
                Arguments = "app-server",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        _process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                MessageReceived?.Invoke(doc.RootElement.Clone());
            }
            catch (JsonException) { }
        };
        _process.ErrorDataReceived += (_, _) => { };
        _process.Exited += (_, _) => Failed?.Invoke("Codex connection stopped.");
    }

    public void Start()
    {
        if (!_process.Start()) throw new InvalidOperationException("Could not start the Codex app-server.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Send("initialize", 1, new
        {
            clientInfo = new
            {
                name = "codex_usage_widget",
                title = "Codex Usage Widget",
                version = typeof(AppServerClient).Assembly.GetName().Version?.ToString(3) ?? "unknown"
            }
        });
    }

    public void ReadAccount() => Send("account/read", 2, new { refreshToken = false });
    public void ReadRateLimits() => Send("account/rateLimits/read", 3, new { });
    public void SendNotification(string method, object parameters) => Write(new { method, @params = parameters });
    public void StartChatGptLogin() => Send("account/login/start", 4, new
    {
        type = "chatgpt",
        useHostedLoginSuccessPage = true,
        appBrand = "codex",
    });

    private void Send(string method, int id, object parameters) => Write(new { method, id, @params = parameters });

    private void Write(object message)
    {
        lock (_writeLock)
        {
            if (_process.HasExited) throw new InvalidOperationException("Codex app-server is not running.");
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            _process.StandardInput.Flush();
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process.Dispose();
    }
}
