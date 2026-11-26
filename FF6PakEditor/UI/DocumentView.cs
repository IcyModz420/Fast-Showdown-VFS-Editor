using FF6PakEditor.Core;

namespace FF6PakEditor.UI;

enum DocumentKind
{
    /// <summary>Lua bytecode decompiled to source; applying compiles it back.</summary>
    Script,
    /// <summary>Lua source opened from disk; applying compiles it into a chosen entry.</summary>
    ExternalScript,
    /// <summary>Text file inside the archive.</summary>
    Text,
    /// <summary>Read-only view (hex dump, bytecode listing).</summary>
    Viewer,
}

/// <summary>One open document: header with actions, message bar and the editor.</summary>
[System.ComponentModel.DesignerCategory("Code")]
sealed class DocumentView : UserControl
{
    readonly Label _title, _info;
    readonly FlowLayoutPanel _actions;
    readonly Panel _messageBar;
    readonly Label _message;
    readonly LinkLabel _messageLink;
    int _messageLine = -1;
    bool _dirty;

    public DocumentView(DocumentKind kind, string title, string? entryPath)
    {
        Kind = kind;
        Title = title;
        EntryPath = entryPath;
        BackColor = C.BG;

        Editor = new CodeEditor { Dock = DockStyle.Fill };
        Editor.TextEdited += (_, _) => { if (!Loading && !Editor.ReadOnly) Dirty = true; };

        // Built at runtime, so WinForms' form-level DPI scaling never touches these: pixel sizes go through Px().
        _message = new Label { Dock = DockStyle.Fill, ForeColor = C.FG, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = Theme.Ui(9f), UseMnemonic = false };
        _messageLink = new LinkLabel
        {
            Dock = DockStyle.Right, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, LinkColor = C.BLUE, ActiveLinkColor = C.FG,
            Width = this.Px(110), Padding = new Padding(0, 0, this.Px(8), 0), Font = Theme.Ui(9f), Visible = false,
        };
        _messageLink.LinkClicked += (_, _) => { if (_messageLine > 0) Editor.GoToLine(_messageLine, selectLine: true); };
        var closeMessage = new Label { Dock = DockStyle.Right, Text = "✕", ForeColor = C.FG_DIM, Width = this.Px(32), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
        closeMessage.Click += (_, _) => HideMessage();
        _messageBar = new Panel { Dock = DockStyle.Top, Height = this.Px(32), Visible = false, Padding = new Padding(this.Px(10), 0, 0, 0) };
        _messageBar.Controls.Add(_message);
        _messageBar.Controls.Add(_messageLink);
        _messageBar.Controls.Add(closeMessage);

        _title = new Label
        {
            AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = C.FG, Font = Theme.Ui(9.5f, FontStyle.Bold), Text = entryPath ?? title,
            UseMnemonic = false, Margin = new Padding(this.Px(10), 0, this.Px(10), 0),
        };
        _info = new Label { Dock = DockStyle.Fill, ForeColor = C.FG_DIM, Font = Theme.Ui(9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Margin = Padding.Empty };
        _actions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight, BackColor = C.BG2, Margin = new Padding(0, this.Px(4), this.Px(6), this.Px(4)),
        };
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = C.BG2,
            ColumnCount = 3, RowCount = 1, MinimumSize = new Size(0, this.Px(36)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(_title, 0, 0);
        header.Controls.Add(_info, 1, 0);
        header.Controls.Add(_actions, 2, 0);

        Controls.Add(Editor);
        Controls.Add(_messageBar);
        Controls.Add(header);
    }

    public DocumentKind Kind { get; }
    public string Title { get; set; }
    public string? EntryPath { get; set; }
    public string? ExternalPath { get; set; }
    public CodeEditor Editor { get; }
    public TabStrip.Tab? Tab { get; set; }
    public bool Loading { get; set; }
    public LuaFormat? SourceFormat { get; set; }
    public string? ChunkName { get; set; }
    public TextEncodingInfo? Encoding { get; set; }
    public byte[]? OriginalBytecode { get; set; }

    public bool Editable => Kind is DocumentKind.Script or DocumentKind.ExternalScript or DocumentKind.Text;

    public bool Dirty
    {
        get => _dirty;
        set
        {
            if (_dirty == value) return;
            _dirty = value;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? DirtyChanged;

    public void SetHeader(string title, string info)
    {
        _title.Text = title;
        _info.Text = info;
    }

    public void SetInfo(string info) => _info.Text = info;

    public Button AddAction(string text, string glyph, string tooltip, EventHandler click, bool primary = false)
    {
        var b = new Button
        {
            Text = " " + text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? C.ACCENT : C.BG4,
            ForeColor = C.FG,
            Font = Theme.Ui(9f),
            Margin = new Padding(this.Px(3), 0, this.Px(3), 0),
            Padding = new Padding(this.Px(6), this.Px(2), this.Px(8), this.Px(2)),
            Cursor = Cursors.Hand,
            Image = Theme.Glyph(glyph, C.FG, this.Px(16)),
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = primary ? C.ACCENT2 : Color.FromArgb(80, 80, 86);
        new ToolTip().SetToolTip(b, tooltip);
        b.Click += click;
        _actions.Controls.Add(b);
        return b;
    }

    public void ShowMessage(string text, bool error, int line = -1)
    {
        _messageBar.BackColor = error ? C.ERROR_BG : C.WARN_BG;
        _message.Text = (error ? "✖  " : "⚠  ") + text.Replace("\r", "").Replace("\n", "  ");
        new ToolTip().SetToolTip(_message, text);
        _messageLine = line;
        _messageLink.Visible = line > 0;
        _messageLink.Text = $"Go to line {line}";
        _messageBar.Visible = true;
    }

    public void HideMessage()
    {
        _messageBar.Visible = false;
        Editor.ClearErrorLine();
    }
}
