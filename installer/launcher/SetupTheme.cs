using System.Drawing.Drawing2D;

namespace CodexUsageSetup;

internal static class SetupTheme
{
    internal static readonly Color Background = Color.FromArgb(25, 28, 31);
    internal static readonly Color Surface = Color.FromArgb(38, 43, 48);
    internal static readonly Color Text = Color.FromArgb(235, 239, 243);
    internal static readonly Color Muted = Color.FromArgb(169, 179, 190);
    internal static readonly Color Border = Color.FromArgb(76, 88, 100);
    internal static readonly Color Accent = Color.FromArgb(21, 143, 153);
    internal static readonly Color PrimaryFill = Color.FromArgb(16, 108, 117);
    internal static readonly Color PrimaryHover = Color.FromArgb(18, 120, 130);

    internal static Size ChoiceSize(Control control)
    {
        var text = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var glyph = Math.Max(14, control.Font.Height - 2);
        return new(text.Width + glyph + 10 + control.Padding.Horizontal, Math.Max(text.Height, glyph) + 4 + control.Padding.Vertical);
    }

    internal static void PaintChoice(PaintEventArgs e, Control control, bool selected, bool radio, bool showKeyboardCues)
    {
        var color = control.Enabled ? Text : Muted;
        var size = Math.Max(14, control.Font.Height - 2);
        var glyph = new Rectangle(control.Padding.Left + 1, (control.Height - size) / 2, size, size);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var edge = new Pen(control.Enabled && selected ? Accent : Border, 1.5f);
        using var fill = new SolidBrush(Surface);
        if (radio)
        {
            e.Graphics.FillEllipse(fill, glyph);
            e.Graphics.DrawEllipse(edge, glyph);
            if (selected)
            {
                glyph.Inflate(-4, -4);
                using var dot = new SolidBrush(control.Enabled ? Accent : Muted);
                e.Graphics.FillEllipse(dot, glyph);
            }
        }
        else
        {
            e.Graphics.FillRectangle(fill, glyph);
            e.Graphics.DrawRectangle(edge, glyph);
            if (selected)
            {
                using var tick = new Pen(control.Enabled ? Text : Muted, 2);
                e.Graphics.DrawLines(tick, new[] { new Point(glyph.Left + 3, glyph.Top + size / 2), new Point(glyph.Left + size / 2 - 1, glyph.Bottom - 4), new Point(glyph.Right - 3, glyph.Top + 4) });
            }
        }
        var textBounds = new Rectangle(control.Padding.Left + size + 9, 0, Math.Max(0, control.Width - size - 9 - control.Padding.Horizontal), control.Height);
        TextRenderer.DrawText(e.Graphics, control.Text, control.Font, textBounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform |
            (showKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
        if (control.Focused && control.Enabled) ControlPaint.DrawFocusRectangle(e.Graphics, textBounds, color, control.BackColor);
    }
}

// Retain Button/RadioButton/CheckBox keyboard, grouping and accessibility semantics.
// Only their dark-mode painting is replaced; Windows high-contrast mode stays native.
internal sealed class SetupButton : Button
{
    private bool _hovered;
    private bool _pressed;
    internal bool Primary { get; set; }

    internal SetupButton()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        BackColor = SystemInformation.HighContrast ? SystemColors.Control : SetupTheme.Surface;
        ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : SetupTheme.Text;
        Padding = new Padding(12, 6, 12, 6);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { _pressed = _hovered = false; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        var fill = !Enabled ? SetupTheme.Surface : Primary ? SetupTheme.PrimaryFill : SetupTheme.Surface;
        if (Enabled && (_hovered || _pressed)) fill = Primary ? SetupTheme.PrimaryHover : Color.FromArgb(51, 58, 65);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        // Clear only this control's invalidated area, never the shared paint surface.
        using (var background = new SolidBrush(Parent?.BackColor ?? SetupTheme.Background))
            e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var radius = Math.Max(4, DeviceDpi / 24);
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, radius * 2, radius * 2, 180, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Top, radius * 2, radius * 2, 270, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(fill);
        using var border = new Pen(Enabled && Primary ? SetupTheme.Accent : SetupTheme.Border);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(border, path);
        var color = Enabled ? SetupTheme.Text : SetupTheme.Muted;
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform |
            (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
        if (Focused && ShowFocusCues) { bounds.Inflate(-5, -5); ControlPaint.DrawFocusRectangle(e.Graphics, bounds, color, fill); }
    }
}

internal sealed class SetupRadioButton : RadioButton
{
    internal SetupRadioButton()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        BackColor = SystemInformation.HighContrast ? SystemColors.Control : SetupTheme.Background;
        ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : SetupTheme.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    public override Size GetPreferredSize(Size proposedSize) => SystemInformation.HighContrast ? base.GetPreferredSize(proposedSize) : SetupTheme.ChoiceSize(this);
    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) base.OnPaint(e);
        else SetupTheme.PaintChoice(e, this, Checked, radio: true, ShowKeyboardCues);
    }
}

internal sealed class SetupCheckBox : CheckBox
{
    internal SetupCheckBox()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        BackColor = SystemInformation.HighContrast ? SystemColors.Control : SetupTheme.Background;
        ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : SetupTheme.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    public override Size GetPreferredSize(Size proposedSize) => SystemInformation.HighContrast ? base.GetPreferredSize(proposedSize) : SetupTheme.ChoiceSize(this);
    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) base.OnPaint(e);
        else SetupTheme.PaintChoice(e, this, Checked, radio: false, ShowKeyboardCues);
    }
}
