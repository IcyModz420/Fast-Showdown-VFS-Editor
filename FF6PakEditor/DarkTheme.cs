using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace FF6PakEditor;

static class C
{
    public static readonly Color BG = Color.FromArgb(30, 30, 30);
    public static readonly Color BG2 = Color.FromArgb(37, 37, 38);
    public static readonly Color BG3 = Color.FromArgb(45, 45, 48);
    public static readonly Color BG4 = Color.FromArgb(62, 62, 66);
    public static readonly Color BORDER = Color.FromArgb(67, 67, 70);
    public static readonly Color FG = Color.FromArgb(220, 220, 220);
    public static readonly Color FG_DIM = Color.FromArgb(150, 150, 150);
    public static readonly Color FG_FAINT = Color.FromArgb(100, 100, 100);
    public static readonly Color ACCENT = Color.FromArgb(0, 122, 204);
    public static readonly Color ACCENT2 = Color.FromArgb(28, 151, 234);
    public static readonly Color SELECTION = Color.FromArgb(38, 79, 120);
    public static readonly Color GREEN = Color.FromArgb(110, 200, 120);
    public static readonly Color TEAL = Color.FromArgb(78, 201, 176);
    public static readonly Color YELLOW = Color.FromArgb(220, 200, 120);
    public static readonly Color ORANGE = Color.FromArgb(232, 150, 70);
    public static readonly Color RED = Color.FromArgb(240, 95, 95);
    public static readonly Color BLUE = Color.FromArgb(120, 180, 250);
    public static readonly Color ERROR_BG = Color.FromArgb(90, 32, 32);
    public static readonly Color WARN_BG = Color.FromArgb(80, 66, 26);

    public static readonly Color COMMENT = Color.FromArgb(106, 153, 85);
    public static readonly Color STRING = Color.FromArgb(206, 145, 120);
    public static readonly Color NUMBER = Color.FromArgb(181, 206, 168);
    public static readonly Color KEYWORD = Color.FromArgb(86, 156, 214);
    public static readonly Color BUILTIN = Color.FromArgb(220, 220, 170);
}

sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color MenuBorder => C.BORDER;
    public override Color MenuItemBorder => C.BG4;
    public override Color MenuItemSelected => C.BG4;
    public override Color MenuStripGradientBegin => C.BG3;
    public override Color MenuStripGradientEnd => C.BG3;
    public override Color ToolStripDropDownBackground => C.BG2;
    public override Color ImageMarginGradientBegin => C.BG2;
    public override Color ImageMarginGradientMiddle => C.BG2;
    public override Color ImageMarginGradientEnd => C.BG2;
    public override Color ToolStripGradientBegin => C.BG3;
    public override Color ToolStripGradientMiddle => C.BG3;
    public override Color ToolStripGradientEnd => C.BG3;
    public override Color ToolStripBorder => C.BG3;
    public override Color SeparatorDark => C.BORDER;
    public override Color SeparatorLight => C.BORDER;
    public override Color StatusStripGradientBegin => C.ACCENT;
    public override Color StatusStripGradientEnd => C.ACCENT;
    public override Color ButtonSelectedHighlight => C.BG4;
    public override Color ButtonSelectedBorder => C.BG4;
    public override Color ButtonPressedHighlight => C.ACCENT;
    public override Color ButtonCheckedHighlight => C.BG4;
    public override Color CheckBackground => C.BG4;
    public override Color CheckSelectedBackground => C.ACCENT;
}

sealed class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() : base(new DarkColorTable()) => RoundedEdges = false;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        Color back = e.ToolStrip switch
        {
            ToolStripDropDown => C.BG2,
            StatusStrip => e.ToolStrip.BackColor,
            _ => C.BG3,
        };
        using var b = new SolidBrush(back);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown) return;
        using var p = new Pen(C.BORDER);
        e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;
        var r = new Rectangle(Point.Empty, e.Item.Size);
        if (e.Item.IsOnDropDown) r.Inflate(-3, 0);
        using var b = new SolidBrush(e.Item.Pressed && !e.Item.IsOnDropDown ? C.BG2 : C.BG4);
        e.Graphics.FillRectangle(b, r);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) => PaintHover(e);
    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => PaintHover(e);
    protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e) => PaintHover(e);

    static void PaintHover(ToolStripItemRenderEventArgs e)
    {
        bool isChecked = e.Item is ToolStripButton { Checked: true };
        if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed || isChecked)) return;
        using var b = new SolidBrush(e.Item.Pressed ? C.ACCENT : C.BG4);
        e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (!e.Item.Enabled) e.TextColor = C.FG_FAINT;
        else if (e.Item.ForeColor == SystemColors.ControlText || e.Item.ForeColor == Control.DefaultForeColor) e.TextColor = C.FG;
        else e.TextColor = e.Item.ForeColor;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? C.FG_FAINT : C.FG_DIM;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var r = e.Item.ContentRectangle;
        using var p = new Pen(C.BORDER);
        if (e.Vertical)
        {
            int x = r.Left + r.Width / 2;
            e.Graphics.DrawLine(p, x, r.Top + 4, x, r.Bottom - 4);
        }
        else
        {
            int y = r.Top + r.Height / 2;
            e.Graphics.DrawLine(p, r.Left + 30, y, r.Right - 6, y);
        }
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(C.BG2);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        r.Inflate(2, 2);
        using var b = new SolidBrush(C.BG4);
        e.Graphics.FillRectangle(b, r);
        using var p = new Pen(C.FG, 2f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(p, new Point[] { new(r.Left + 4, r.Top + r.Height / 2), new(r.Left + r.Width / 2 - 1, r.Bottom - 5), new(r.Right - 4, r.Top + 4) });
    }
}

static class Theme
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    static readonly string? IconFont = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }.FirstOrDefault(IsInstalled);

    static bool IsInstalled(string family)
    {
        using var font = new Font(family, 10f);
        return string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase);
    }

    public static void DarkTitleBar(Form form)
    {
        int on = 1;
        if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
    }

    /// <summary>Dark native scrollbars / tree glyphs (Windows 10 1809+; harmless elsewhere).</summary>
    public static void DarkControl(Control control)
    {
        void Apply() => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        if (control.IsHandleCreated) Apply();
        control.HandleCreated += (_, _) => Apply();
    }

    public static Font Ui(float size = 9f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    /// <summary>Renders a Segoe icon-font glyph (e.g. "") as a bitmap, or null when the font is unavailable.</summary>
    public static Bitmap? Glyph(string glyph, Color color, int px)
    {
        if (IconFont == null) return null;
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(IconFont, px * 0.78f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 1, px, px), format);
        return bmp;
    }

    public static int Px(this Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96.0);
}

static class Glyphs
{
    public const string Open = "", Save = "", SaveAs = "", Add = "", Delete = "",
        Extract = "", Replace = "", Compile = "", Check = "", Refresh = "",
        Find = "", Code = "", Document = "", Folder = "", Revert = "", Copy = "",
        Filter = "", Info = "", Warning = "", Close = "", Repair = "", NewFolder = "";
}
