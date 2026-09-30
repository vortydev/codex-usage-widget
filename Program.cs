using System.Diagnostics;
using System.Drawing;
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
    private readonly FlowLayoutPanel _windows = new();
    private readonly Button _connect = new();
    private readonly Button _refresh = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 60_000 };
    private AppServerClient? _server;
    private bool _connected;

    public UsageForm()
    {
        Text = "Codex Usage";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = false;
        ShowIcon = false;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(25, 27, 31);
        ForeColor = Color.FromArgb(238, 240, 243);
        ClientSize = new Size(360, 360);
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 5,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
        StyleButton(_refresh, primary: false);
        _refresh.Width = 78;
        _refresh.Click += (_, _) => RefreshUsage();
        root.Controls.Add(_refresh, 1, 0);

        _status.Text = "Starting local Codex connection…";
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(175, 181, 191);
        root.Controls.Add(_status, 0, 1);
        root.SetColumnSpan(_status, 2);

        _windows.Dock = DockStyle.Fill;
        _windows.FlowDirection = FlowDirection.TopDown;
        _windows.WrapContents = false;
        _windows.AutoScroll = true;
        _windows.BackColor = BackColor;
        root.Controls.Add(_windows, 0, 2);
        root.SetColumnSpan(_windows, 2);

        _connect.Text = "Connect with ChatGPT";
        StyleButton(_connect, primary: true);
        _connect.Dock = DockStyle.Fill;
        _connect.Visible = false;
        _connect.Click += (_, _) => ConnectWithChatGpt();
        root.Controls.Add(_connect, 0, 3);
        root.SetColumnSpan(_connect, 2);

        var footer = new Label
        {
            Text = "Updates automatically every minute · Always on top",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(125, 132, 143),
            Font = new Font("Segoe UI", 8F),
        };
        root.Controls.Add(footer, 0, 4);
        root.SetColumnSpan(footer, 2);

        Controls.Add(root);
        PlaceAtTopRight();
        Shown += (_, _) => StartServer();
        FormClosed += (_, _) =>
        {
            _poll.Stop();
            _poll.Dispose();
            _server?.Dispose();
        };
        _poll.Tick += (_, _) => RefreshUsage();
        _poll.Start();
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
        foreach (var row in rows.OrderBy(x => x.DurationMinutes == 0 ? int.MaxValue : x.DurationMinutes))
            _windows.Controls.Add(new UsageRow(row));
        _windows.ResumeLayout();
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
        public UsageRow(UsageWindow usage)
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
            };
            var color = usage.Remaining < 15 ? Color.FromArgb(226, 95, 95)
                : usage.Remaining < 35 ? Color.FromArgb(230, 174, 82)
                : Color.FromArgb(42, 185, 160);
            var progress = new RoundedProgressBar
            {
                Location = new Point(12, 35),
                Size = new Size(286, 9),
                Remaining = usage.Remaining,
                FillColor = color,
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
            Controls.Add(progress);
            Controls.Add(reset);
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

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using var trackPath = CreateRoundedPath(track, Height / 2f);
            using var trackBrush = new SolidBrush(BackColor);
            e.Graphics.FillPath(trackBrush, trackPath);

            var fillWidth = (int)Math.Round((Width - 1) * Math.Clamp(Remaining, 0, 100) / 100d);
            if (fillWidth <= 0) return;
            var fill = new Rectangle(0, 0, fillWidth, Height - 1);
            using var fillPath = CreateRoundedPath(fill, Math.Min(Height / 2f, fillWidth / 2f));
            using var fillBrush = new SolidBrush(FillColor);
            e.Graphics.FillPath(fillBrush, fillPath);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedPath(Rectangle bounds, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Max(1, (int)Math.Round(radius * 2));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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
            clientInfo = new { name = "codex_usage_widget", title = "Codex Usage Widget", version = "0.1.0" }
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
