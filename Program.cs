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
    private readonly FlowLayoutPanel _windows = new();
    private readonly Button _connect = new();
    private readonly Button _refresh = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 60_000 };
    private readonly WidgetSettings _settings;
    private readonly string? _settingsPath;
    private AppServerClient? _server;
    private bool _connected;

    public UsageForm(bool startServer = true, string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        _settings = WidgetSettings.Load(settingsPath);
        Text = "Codex Usage";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MinimumSize = new Size(380, 420);
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = false;
        ShowIcon = true;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        TopMost = _settings.AlwaysOnTop;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(25, 27, 31);
        ForeColor = Color.FromArgb(238, 240, 243);
        ClientSize = new Size(360, 410);
        Font = new Font("Segoe UI", 9F);

        var tabs = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BackColor,
        };
        tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tabs.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        tabs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var tabHeader = new TableLayoutPanel
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
        var usageIndicator = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var settingsIndicator = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        tabHeader.Controls.Add(usageTab, 0, 0);
        tabHeader.Controls.Add(settingsTab, 1, 0);
        tabHeader.Controls.Add(usageIndicator, 0, 1);
        tabHeader.Controls.Add(settingsIndicator, 1, 1);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Margin = Padding.Empty };
        var usagePage = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
        var settingsPage = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
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

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 6,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

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
        _refresh.Size = new Size(108, 30);
        _refresh.Text = "↻  Refresh";
        _refresh.Click += (_, _) => RefreshUsage();
        root.Controls.Add(_refresh, 0, 3);
        root.SetColumnSpan(_refresh, 2);

        _status.Text = "Starting local Codex connection…";
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(175, 181, 191);
        root.Controls.Add(_status, 0, 2);
        root.SetColumnSpan(_status, 2);

        _windows.Dock = DockStyle.Fill;
        _windows.FlowDirection = FlowDirection.TopDown;
        _windows.WrapContents = false;
        _windows.AutoScroll = false;
        _windows.BackColor = BackColor;
        _windows.SizeChanged += (_, _) => ResizeUsageRows();
        root.Controls.Add(_windows, 0, 1);
        root.SetColumnSpan(_windows, 2);

        _connect.Text = "Connect with ChatGPT";
        StyleButton(_connect, primary: true);
        _connect.Dock = DockStyle.Fill;
        _connect.Visible = false;
        _connect.Click += (_, _) => ConnectWithChatGpt();
        root.Controls.Add(_connect, 0, 4);
        root.SetColumnSpan(_connect, 2);

        var version = typeof(UsageForm).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        _footer.Dock = DockStyle.Fill;
        _footer.TextAlign = ContentAlignment.MiddleCenter;
        _footer.ForeColor = Color.FromArgb(125, 132, 143);
        _footer.Font = new Font("Segoe UI", 8F);
        UpdateFooter(version);
        root.Controls.Add(_footer, 0, 5);
        root.SetColumnSpan(_footer, 2);

        usagePage.Controls.Add(root);
        BuildSettingsPage(settingsPage, version);
        Controls.Add(tabs);
        PlaceAtTopRight();
        if (startServer) Shown += (_, _) => StartServer();
        FormClosed += (_, _) =>
        {
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
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 11,
            BackColor = BackColor,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 34, 32, 24, 32, 24, 32, 32, 36, 42 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        layout.Controls.Add(new Label
        {
            Text = "Widget settings",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var alwaysOnTop = new CheckBox
        {
            Text = "Always on top",
            Checked = _settings.AlwaysOnTop,
            Dock = DockStyle.Fill,
            ForeColor = ForeColor,
            BackColor = BackColor,
        };
        layout.Controls.Add(alwaysOnTop, 0, 1);

        layout.Controls.Add(new Label
        {
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
            Width = 180,
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
        layout.Controls.Add(refreshInterval, 0, 3);

        layout.Controls.Add(new Label
        {
            Text = "Colour thresholds · % remaining",
            Dock = DockStyle.Fill,
            ForeColor = ForeColor,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 4);

        TextBox AddThresholdInput(string label, int value, int row)
        {
            var inputRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ColumnCount = 2,
                RowCount = 1,
            };
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            inputRow.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ForeColor,
            }, 0, 0);
            var input = new TextBox
            {
                Text = value.ToString(CultureInfo.InvariantCulture),
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(38, 41, 47),
                ForeColor = ForeColor,
                BorderStyle = BorderStyle.FixedSingle,
                AccessibleName = label,
            };
            inputRow.Controls.Add(input, 1, 0);
            layout.Controls.Add(inputRow, 0, row);
            return input;
        }
        var criticalThreshold = AddThresholdInput("Red below (%)", _settings.CriticalThresholdPercent, 5);
        var warningThreshold = AddThresholdInput("Amber below (%)", _settings.WarningThresholdPercent, 6);
        var applyThresholds = new Button { Text = "Apply thresholds", Size = new Size(150, 30) };
        StyleButton(applyThresholds, primary: true);
        layout.Controls.Add(applyThresholds, 0, 7);

        var saveStatus = new Label
        {
            Text = "Other settings autosave. Thresholds need Apply.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(151, 158, 169),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        layout.Controls.Add(saveStatus, 0, 8);

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
            Text = $"v{version}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(125, 132, 143),
            Font = new Font("Segoe UI", 8F),
        }, 0, 10);

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
        page.Controls.Add(layout);
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
            _windows.Controls.Clear();
            _status.Text = "Usage data isn’t available yet. Try Refresh.";
            return;
        }

        _windows.SuspendLayout();
        _windows.Controls.Clear();
        // Rows arrive after WinForms has scaled the form; match its logical layout scale.
        var scale = _windows.Parent is TableLayoutPanel layout ? layout.RowStyles[0].Height / 34f : DeviceDpi / 96f;
        foreach (var row in rows.OrderBy(x => x.DurationMinutes == 0 ? int.MaxValue : x.DurationMinutes))
        {
            var tile = new UsageRow(row, _settings);
            if (scale != 1f) tile.Scale(new SizeF(scale, scale));
            _windows.Controls.Add(tile);
        }
        _windows.ResumeLayout();
        ResizeUsageRows();
        ClientSize = new Size(ClientSize.Width, (int)Math.Ceiling(Math.Max(410, 236 + rows.Count * 87) * scale));
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
            row.Width = Math.Max(1, _windows.ClientSize.Width - row.Margin.Horizontal);
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
            Controls.Add(header);
            Controls.Add(remaining);
            Controls.Add(_progress);
            Controls.Add(reset);
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
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using var trackPath = CreateRoundedPath(track, track.Height / 2f);
            using var trackBrush = new SolidBrush(BackColor);
            e.Graphics.FillPath(trackBrush, trackPath);

            var fillWidth = (int)Math.Round((Width - 1) * Math.Clamp(Remaining, 0, 100) / 100d);
            if (fillWidth <= 0) return;
            var fill = new Rectangle(0, 0, fillWidth, Height - 1);
            using var fillPath = CreateRoundedPath(fill, Math.Min(fill.Height / 2f, fill.Width / 2f));
            using var fillBrush = new SolidBrush(FillColor);
            e.Graphics.FillPath(fillBrush, fillPath);
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
