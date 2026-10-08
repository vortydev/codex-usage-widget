using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using CodexUsageWidget;

internal static class ThresholdTests
{
    private static int _checks;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Check(WidgetSettings.FromJson("{\"FontSize\":14}").FontSize == 14, "Stored font size");
        Check(WidgetSettings.FromJson("{\"FontSize\":99}").FontSize == 9, "Invalid font size defaults");
        var defaults = new WidgetSettings();
        Check(defaults.CriticalThresholdPercent == 15 && defaults.WarningThresholdPercent == 35, "Defaults");
        foreach (var pair in new[] { ("0", "100"), ("15", "35"), ("25", "60"), (" 10 ", " 40 ") })
            Check(WidgetSettings.TryParseThresholds(pair.Item1, pair.Item2, out _, out _), $"Valid {pair}");
        foreach (var pair in new[] { ("", "35"), ("15", ""), ("abc", "35"), ("15.5", "35"), ("15", "35.5"),
            ("-1", "35"), ("15", "101"), ("35", "35"), ("40", "35"), ("999999999999", "100"), ("0", "0") })
            Check(!WidgetSettings.TryParseThresholds(pair.Item1, pair.Item2, out _, out _), $"Invalid {pair}");

        var red = Color.FromArgb(226, 95, 95);
        var amber = Color.FromArgb(230, 174, 82);
        var teal = Color.FromArgb(42, 185, 160);
        Check(defaults.GetUsageColor(14.999) == red, "Unrounded red boundary");
        Check(defaults.GetUsageColor(15) == amber, "Exact red threshold is amber");
        Check(defaults.GetUsageColor(34.999) == amber, "Unrounded amber boundary");
        Check(defaults.GetUsageColor(35) == teal, "Exact amber threshold is teal");
        defaults.CriticalThresholdPercent = 0;
        defaults.WarningThresholdPercent = 100;
        Check(defaults.GetUsageColor(0) == amber && defaults.GetUsageColor(100) == teal, "Extreme valid pair");

        var old = WidgetSettings.FromJson("{\"AlwaysOnTop\":false,\"RefreshMinutes\":5}");
        Check(!old.AlwaysOnTop && old.RefreshMinutes == 5 && old.CriticalThresholdPercent == 15 && old.WarningThresholdPercent == 35, "Old settings compatibility");
        foreach (var invalid in new[] { "40", "-1", "101", "15.5", "null", "\"invalid\"", "true" })
        {
            var settings = WidgetSettings.FromJson($"{{\"AlwaysOnTop\":false,\"RefreshMinutes\":15,\"CriticalThresholdPercent\":{invalid},\"WarningThresholdPercent\":35}}");
            Check(!settings.AlwaysOnTop && settings.RefreshMinutes == 15 && settings.CriticalThresholdPercent == 15 && settings.WarningThresholdPercent == 35,
                $"Bad stored red preserves other settings: {invalid}");
        }
        foreach (var invalid in new[] { "10", "101", "-1", "null", "35.5", "\"invalid\"" })
        {
            var settings = WidgetSettings.FromJson($"{{\"RefreshMinutes\":0,\"CriticalThresholdPercent\":15,\"WarningThresholdPercent\":{invalid}}}");
            Check(settings.RefreshMinutes == 0 && settings.CriticalThresholdPercent == 15 && settings.WarningThresholdPercent == 35,
                $"Bad stored amber preserves other settings: {invalid}");
        }

        var directory = Path.Combine(Path.GetTempPath(), "codex-widget-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var custom = new WidgetSettings { AlwaysOnTop = false, RefreshMinutes = 0, CriticalThresholdPercent = 25, WarningThresholdPercent = 60 };
            custom.Save(path);
            var loaded = WidgetSettings.Load(path);
            Check(!loaded.AlwaysOnTop && loaded.RefreshMinutes == 0 && loaded.CriticalThresholdPercent == 25 && loaded.WarningThresholdPercent == 60, "Save/reload persistence");
            using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { custom.Save(path); throw new InvalidOperationException("Expected save failure"); }
                catch (IOException) { Check(true, "Save failure surfaced"); }
            }
            Check(WidgetSettings.Load(path).WarningThresholdPercent == 60, "Failed save preserves file");
            Check(WidgetSettings.Load(Path.Combine(directory, "missing.json")).CriticalThresholdPercent == 15, "Missing file defaults");
            File.WriteAllText(path, "not json");
            Check(WidgetSettings.Load(path).WarningThresholdPercent == 35, "Malformed JSON defaults");

            foreach (var scale in new[] { 1f, 1.5f, 2f }) TestLayout(scale, directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"Passed {_checks} checks.");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(button, new object[] { EventArgs.Empty });

    private static void TestLayout(float scale, string directory)
    {
        var settingsPath = Path.Combine(directory, $"ui-settings-{scale}.json");
        using var form = new UsageForm(startServer: false, settingsPath: settingsPath);
        form.ShowInTaskbar = false;
        form.TopMost = false;
        form.Location = new Point(-20000, -20000);
        form.Size = form.MinimumSize;
        form.Show(); // Off-screen render; no account connection or polling request.
        var settings = Descendants(form).OfType<Button>().Single(x => x.Text == "Settings");
        Click(settings);
        if (scale != 1f) form.Scale(new SizeF(scale, scale));
        form.PerformLayout();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"settings-{scale}.png"));
        var inputs = Descendants(form).OfType<TextBox>().ToList();
        Check(inputs.Count == 2, $"Two inputs at {scale}");
        Check(inputs.All(x => x.Visible), $"Inputs visible at {scale}");
        var apply = Descendants(form).OfType<Button>().Single(x => x.Text == "Apply thresholds");
        Check(apply.Height >= 25 * scale, $"Apply not clipped at {scale}");
        foreach (var control in inputs.Cast<Control>().Append(apply))
            Check(control.Bottom <= control.Parent!.ClientSize.Height && control.Right <= control.Parent.ClientSize.Width, $"Control bounds at {scale}");
        Check(form.FormBorderStyle == FormBorderStyle.Sizable, "Resizable window");
        var combo = Descendants(form).OfType<ComboBox>().Single();
        Check(inputs.All(input => input.Left == combo.Left && input.Width == combo.Width), "Settings inputs share grid column");
        Check(apply.Left == combo.Left, "Apply aligns with controls");
        var grid = (TableLayoutPanel)combo.Parent!;
        var checkbox = Descendants(form).OfType<CheckBox>().Single();
        Check(grid.GetColumnSpan(checkbox) == 1, "Checkbox occupies one cell");
        inputs[0].Text = "40";
        inputs[1].Text = "35";
        Click(apply);
        Check(Descendants(form).OfType<Label>().Any(x => x.Text.Contains("Thresholds unchanged")), $"Inline validation at {scale}");
        var field = typeof(UsageForm).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var state = (WidgetSettings)field.GetValue(form)!;
        var originalCritical = state.CriticalThresholdPercent;
        var originalWarning = state.WarningThresholdPercent;
        inputs[0].Text = "";
        Click(apply);
        Check(state.CriticalThresholdPercent == originalCritical && state.WarningThresholdPercent == originalWarning, "Invalid drafts don't apply");

        using var data = System.Text.Json.JsonDocument.Parse("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":80,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":50,\"windowDurationMins\":10080}}}}");
        typeof(UsageForm).GetMethod("RenderRateLimits", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { data.RootElement });
        var status = (Label)typeof(UsageForm).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var timestamp = status.Text;
        var windowSize = form.Size;
        typeof(UsageForm).GetMethod("RenderRateLimits", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { data.RootElement });
        Check(form.Size == windowSize, "Refresh preserves window size");
        var rows = (FlowLayoutPanel)typeof(UsageForm).GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var row = rows.Controls[0];
        inputs[0].Text = "25";
        inputs[1].Text = "60";
        Click(apply);
        var persisted = WidgetSettings.Load(settingsPath);
        Check(persisted.CriticalThresholdPercent == 25 && persisted.WarningThresholdPercent == 60, "Apply persists both thresholds");
        var progress = Descendants(row).Single(x => x.GetType().Name == "RoundedProgressBar");
        var colour = (Color)progress.GetType().GetProperty("FillColor")!.GetValue(progress)!;
        Check(colour == Color.FromArgb(226, 95, 95) && status.Text == timestamp, "Immediate recolour retains timestamp");
        var secondProgress = Descendants(rows.Controls[1]).Single(x => x.GetType().Name == "RoundedProgressBar");
        Check((Color)secondProgress.GetType().GetProperty("FillColor")!.GetValue(secondProgress)! == Color.FromArgb(230, 174, 82), "Apply recolours all rows");
        using (var locked = File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            inputs[0].Text = "30";
            inputs[1].Text = "65";
            Click(apply);
            Check(Descendants(form).OfType<Label>().Any(x => x.Text == "Could not save settings; changes last until close."), "Inline save failure");
        }
        Check(WidgetSettings.Load(settingsPath).CriticalThresholdPercent == 25 && state.CriticalThresholdPercent == 30, "Failed save is session-only");
        Check(typeof(UsageForm).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form) is null, "Recolour needs no server");
        Click(Descendants(form).OfType<Button>().Single(x => x.Text == "Usage"));
        form.PerformLayout();
        foreach (Control tile in rows.Controls)
            Check(tile.Right <= rows.ClientSize.Width && tile.Bottom <= rows.ClientSize.Height, $"Usage tile bounds at {scale}");
        var slider = Descendants(form).OfType<TrackBar>().Single();
        slider.Value = 14;
        form.PerformLayout();
        Check(WidgetSettings.Load(settingsPath).FontSize == 14, "Font slider persists");
        Check(Math.Abs(form.Font.SizeInPoints - 14) < 0.1, "Font slider updates text");
        foreach (var tile in rows.Controls.Cast<Control>())
        {
            var labels = Descendants(tile).OfType<Label>().ToArray();
            Check(labels.All(label => label.Height >= TextRenderer.MeasureText(label.Text, label.Font).Height), $"Card text fits at maximum font size {scale}");
        }
        Click(settings);
        using (var largeSettings = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(largeSettings, new Rectangle(Point.Empty, largeSettings.Size));
            largeSettings.Save(Path.Combine(AppContext.BaseDirectory, $"settings-large-{scale}.png"));
        }
        Click(Descendants(form).OfType<Button>().Single(x => x.Text == "Usage"));
        foreach (Control tile in rows.Controls)
        {
            var table = tile.Controls.OfType<TableLayoutPanel>().Single();
            Check(table.Padding.Top == table.Padding.Bottom, "Balanced card padding");
            var textBottom = table.Controls.OfType<Label>().Max(label => label.Bottom);
            Check(Math.Abs(tile.Height - textBottom - table.Padding.Bottom - table.Controls.OfType<Label>().Last().Margin.Bottom) <= 2,
                "Card height follows content");
        }
        slider.Value = 9;
        Check(Math.Abs(form.Font.SizeInPoints - 9) < 0.1, "Font slider restores default");
        form.PerformLayout();
        var refresh = Descendants(form).OfType<Button>().Single(button => button.Text.Contains("Refresh"));
        Check(refresh.Top > status.Bottom && refresh.Top - status.Bottom <= 10 * scale, "Status to refresh gap");
        var root = (TableLayoutPanel)rows.Parent!;
        Check(root.GetRowHeights()[4] == 0, "Hidden connection reserves no row");
        var lastCard = rows.Controls[rows.Controls.Count - 1];
        Check(status.Top - (rows.Top + lastCard.Bottom) <= 16 * scale, $"Status stays near cards: scale={scale}, status={status.Top}, rows={rows.Top}/{rows.Height}, last={lastCard.Bottom}, row heights={string.Join(",", root.GetRowHeights())}");
        var gauge = Descendants(rows.Controls[0]).Single(control => control.GetType().Name == "RoundedProgressBar");
        foreach (var value in new[] { 0d, 1d, 50d, 99d, 100d })
        {
            gauge.GetType().GetProperty("Remaining")!.SetValue(gauge, value);
            using var gaugeBitmap = new Bitmap(gauge.Width, gauge.Height);
            gauge.DrawToBitmap(gaugeBitmap, new Rectangle(Point.Empty, gaugeBitmap.Size));
            Check(gaugeBitmap.GetPixel(0, 0).ToArgb() == rows.Controls[0].BackColor.ToArgb(), $"Gauge corner matches card at {value}%");
            gaugeBitmap.Save(Path.Combine(AppContext.BaseDirectory, $"gauge-{scale}-{value}.png"));
        }
        gauge.GetType().GetProperty("Remaining")!.SetValue(gauge, 20d);
        var gaugeSize = gauge.Size;
        gauge.Dock = DockStyle.None;
        gauge.Size = new Size(1, 1);
        using (var tiny = new Bitmap(1, 1)) gauge.DrawToBitmap(tiny, new Rectangle(0, 0, 1, 1));
        Check(true, "Tiny gauge paints safely");
        gauge.Dock = DockStyle.Fill;
        gauge.Parent!.PerformLayout();
        for (var step = 0; step < 20; step++)
        {
            form.Width += step % 2 == 0 ? 10 : -10;
            form.PerformLayout();
            Check(rows.Controls.Cast<Control>().All(tile => tile.Right <= rows.ClientSize.Width), "Resize keeps cards inside viewport");
        }
        Check(!rows.HorizontalScroll.Visible, "No horizontal card scrollbar");
        using var usageBitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(usageBitmap, new Rectangle(Point.Empty, usageBitmap.Size));
        usageBitmap.Save(Path.Combine(AppContext.BaseDirectory, $"usage-{scale}.png"));
        using var many = System.Text.Json.JsonDocument.Parse("{\"rateLimitsByLimitId\":{" + string.Join(",", Enumerable.Range(0, 8).Select(index => $"\"bucket{index}\":{{\"primary\":{{\"usedPercent\":50,\"windowDurationMins\":300}}}}")) + "}}");
        typeof(UsageForm).GetMethod("RenderRateLimits", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { many.RootElement });
        form.PerformLayout();
        Check(rows.VerticalScroll.Visible && !rows.HorizontalScroll.Visible, $"Many cards scroll vertically only: vertical={rows.VerticalScroll.Visible}, horizontal={rows.HorizontalScroll.Visible}, viewport={rows.Size}, count={rows.Controls.Count}, last={rows.Controls[rows.Controls.Count-1].Bounds}");
        Check(form.Size == windowSize, "Many cards preserve window size");
    }
}
