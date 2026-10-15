using System.Text;

namespace CodexUsageSetup;

internal static class SetupLog
{
    internal static string ReadTail(string? path)
    {
        if (path is null || !File.Exists(path)) return "Windows Installer log will appear here when installation starts.";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var first = stream.ReadByte();
            var second = stream.ReadByte();
            var unicode = first == 0xff && second == 0xfe;
            const int limit = 128 * 1024;
            var offset = Math.Max(0, stream.Length - limit);
            if (unicode && offset % 2 != 0) offset++;
            stream.Position = offset;
            using var reader = new StreamReader(stream, unicode ? Encoding.Unicode : Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[limit];
            // Bound the file snapshot even if the installer keeps appending while it is read.
            return new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "Cannot read the setup log yet: " + ex.Message; }
    }
}

internal sealed class SetupDetailsForm : Form
{
    private readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    internal SetupDetailsForm(Func<string> activity, Func<string?> log)
    {
        Text = "Codex Usage Setup — details";
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(420, 260);
        StartPosition = FormStartPosition.CenterParent;
        _text.Font = new Font("Consolas", 10);
        _text.BackColor = SystemInformation.HighContrast ? SystemColors.Window : SetupTheme.Background;
        _text.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : SetupTheme.Text;
        _text.BorderStyle = BorderStyle.None;
        Controls.Add(_text);
        void RefreshLog()
        {
            var value = activity() + "\r\n\r\nLog: " + (log() ?? "not started") + "\r\n\r\n" + SetupLog.ReadTail(log());
            if (_text.Text == value) return;
            _text.Text = value;
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        }
        Shown += (_, _) => { RefreshLog(); _timer.Start(); };
        _timer.Tick += (_, _) => RefreshLog();
        FormClosed += (_, _) => _timer.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
