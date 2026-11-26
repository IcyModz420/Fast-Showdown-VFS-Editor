using FF6PakEditor.Core;

namespace FF6PakEditor.UI;

static class Dialogs
{
    sealed class DarkDialog : Form
    {
        public DarkDialog(string title, int width)
        {
            Text = title;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = C.BG2;
            ForeColor = C.FG;
            Font = Theme.Ui(9.5f);
            ClientSize = new Size(width, 100);
            Padding = new Padding(16);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }
    }

    static Button DialogButton(string text, DialogResult result, bool primary)
    {
        var b = new Button
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            MinimumSize = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? C.ACCENT : C.BG4,
            ForeColor = C.FG,
            Margin = new Padding(6, 0, 0, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    /// <summary>Single-line text prompt. Returns null when cancelled.</summary>
    public static string? Input(IWin32Window owner, string title, string prompt, string initial)
    {
        using var f = new DarkDialog(title, 520);
        var label = new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(488, 0), Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8), UseMnemonic = false };
        var box = new TextBox { Text = initial, Dock = DockStyle.Top, BackColor = C.BG, ForeColor = C.FG, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10.5f) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 14, 0, 0) };
        var ok = DialogButton("OK", DialogResult.OK, true);
        var cancel = DialogButton("Cancel", DialogResult.Cancel, false);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        f.Controls.Add(buttons);
        f.Controls.Add(box);
        f.Controls.Add(label);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.AutoSize = true;
        f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        f.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }

    /// <summary>Asks which game an archive is for. Returns null when cancelled.</summary>
    public static GamePlatform? ChoosePlatform(IWin32Window owner, string message, GamePlatform suggested)
    {
        using var f = new DarkDialog("Choose platform", 460);
        var label = new Label { Text = message, AutoSize = true, MaximumSize = new Size(428, 0), Dock = DockStyle.Top, UseMnemonic = false };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 18, 0, 0) };
        var cancel = DialogButton("Cancel", DialogResult.Cancel, false);
        var threeDs = DialogButton("3DS", DialogResult.Retry, suggested == GamePlatform.Nintendo3DS);
        var xbox = DialogButton("Xbox 360", DialogResult.No, suggested == GamePlatform.Xbox360);
        var pc = DialogButton("PC", DialogResult.Yes, suggested == GamePlatform.PC);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(threeDs);
        buttons.Controls.Add(xbox);
        buttons.Controls.Add(pc);
        f.Controls.Add(buttons);
        f.Controls.Add(label);
        f.CancelButton = cancel;
        f.AutoSize = true;
        f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        return f.ShowDialog(owner) switch
        {
            DialogResult.Yes => GamePlatform.PC,
            DialogResult.No => GamePlatform.Xbox360,
            DialogResult.Retry => GamePlatform.Nintendo3DS,
            _ => null,
        };
    }

    public static DialogResult Ask(IWin32Window owner, string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.YesNo, MessageBoxIcon icon = MessageBoxIcon.Question) =>
        MessageBox.Show(owner, text, caption, buttons, icon);

    public static void Error(IWin32Window owner, string text, string caption = "Fast Showdown VFS Editor") =>
        MessageBox.Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static void Info(IWin32Window owner, string text, string caption = "Fast Showdown VFS Editor") =>
        MessageBox.Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
}
