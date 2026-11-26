using System.Diagnostics;
using System.Text;
using FF6PakEditor.Core;
using FF6PakEditor.UI;

namespace FF6PakEditor;

enum LogKind { Info, Success, Warning, Error, Detail }

[System.ComponentModel.DesignerCategory("Code")]
sealed class MainForm : Form
{
    const string AppName = "Fast Showdown VFS Editor";
    const int HexLimit = 256 * 1024;

    readonly AppSettings _settings = AppSettings.Load();
    readonly LuaToolchain _tools = LuaToolchain.Discover();
    readonly string[] _args;
    GameArchive? _archive;
    GamePlatform _newArchivePlatform = GamePlatform.PC;
    bool _busy, _forceClose, _syncingPlatform;
    CancellationTokenSource? _cts;

    MenuStrip _menu = null!;
    ToolStrip _toolbar = null!;
    StatusStrip _statusBar = null!;
    ToolStripComboBox _platformBox = null!;
    ToolStripStatusLabel _statusText = null!, _statusCaret = null!, _statusPlatform = null!, _statusFiles = null!, _statusPending = null!, _statusCancel = null!;
    ToolStripProgressBar _progress = null!;
    ToolStripMenuItem _recentMenu = null!;
    readonly List<ToolStripItem> _needsArchive = new();
    readonly List<ToolStripItem> _needsDocument = new();

    SplitContainer _mainSplit = null!, _rightSplit = null!;
    TextBox _filter = null!;
    TreeView _tree = null!;
    Label _treeHeader = null!;
    ContextMenuStrip _treeMenu = null!;
    TreeNode? _dropNode;
    readonly Dictionary<string, TreeNode> _fileNodes = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer _filterTimer = new() { Interval = 250 };

    TabStrip _docTabs = null!;
    Panel _docHost = null!, _welcome = null!;
    FlowLayoutPanel _welcomeContent = null!;
    readonly List<DocumentView> _docs = new();

    TabStrip _bottomTabs = null!;
    RichTextBox _output = null!;
    DataGridView _grid = null!;
    List<ArchiveItem> _gridRows = new();
    int _gridSortColumn;
    bool _gridSortDescending;

    public MainForm(string[] args)
    {
        _args = args;
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = AppName;
        BackColor = C.BG;
        ForeColor = C.FG;
        Font = Theme.Ui(9f);
        Size = new Size(1400, 860);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;
        Icon = MakeAppIcon();

        BuildMenu();
        BuildToolbar();
        BuildStatusBar();
        BuildLayout();

        // Docking runs from the last-added control to the first: the menu (added last) takes the top edge,
        // the toolbar sits under it, the status bar takes the bottom and the split container fills the rest.
        Controls.Add(_mainSplit);
        Controls.Add(_statusBar);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;
        ResumeLayout(false);
        PerformLayout();

        RestoreWindow();
        DragEnter += OnDragEnter;
        DragDrop += OnFormDragDrop;
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); PopulateTree(); RefreshGrid(); };
        BuildRecentMenu();
        RefreshWelcome();
        UpdateUiState();
    }

    // ===================================================================== construction

    static Icon MakeAppIcon()
    {
        using var list = Icons.Create(32);
        using var bmp = new Bitmap(list.Images["archive"]!);
        return Icon.FromHandle(bmp.GetHicon());
    }

    ToolStripMenuItem Item(string text, EventHandler onClick, string? shortcut = null, string? glyph = null, bool archive = false, bool document = false)
    {
        var item = new ToolStripMenuItem(text, glyph != null ? Theme.Glyph(glyph, C.FG_DIM, this.Px(16)) : null, onClick) { ForeColor = C.FG };
        if (shortcut != null) item.ShortcutKeyDisplayString = shortcut;
        if (archive) _needsArchive.Add(item);
        if (document) _needsDocument.Add(item);
        return item;
    }

    static ToolStripMenuItem Menu(string text, params ToolStripItem[] items)
    {
        var menu = new ToolStripMenuItem(text) { ForeColor = C.FG };
        menu.DropDownItems.AddRange(items);
        menu.DropDown.BackColor = C.BG2;
        return menu;
    }

    static ToolStripSeparator Sep() => new();

    void BuildMenu()
    {
        _menu = new MenuStrip { Renderer = new DarkRenderer(), BackColor = C.BG3, ForeColor = C.FG, Padding = new Padding(4, 2, 0, 2), Font = Theme.Ui(9f) };
        _recentMenu = new ToolStripMenuItem("Recent Archives") { ForeColor = C.FG };

        _menu.Items.Add(Menu("&File",
            Item("&Open Archive...", (_, _) => OpenArchiveDialog(), "Ctrl+O", Glyphs.Open),
            _recentMenu,
            Item("&New Archive from Folder...", async (_, _) => await NewFromFolder(), null, Glyphs.NewFolder),
            Item("&Close Archive", async (_, _) => await CloseArchive(), null, null, archive: true),
            Sep(),
            Item("&Save Archive", async (_, _) => await SaveArchive(false), "Ctrl+S", Glyphs.Save, archive: true),
            Item("Save Archive &As...", async (_, _) => await SaveArchive(true), "Ctrl+Shift+S", Glyphs.SaveAs, archive: true),
            Item("Discard All Changes", async (_, _) => await DiscardChanges(), null, Glyphs.Revert, archive: true),
            Sep(),
            Item("Open .lua File from Disk...", (_, _) => OpenLuaFromDisk(), null, Glyphs.Document),
            Item("Save Tab to File...", (_, _) => SaveDocumentToFile(), null, null, document: true),
            Item("Close Tab", (_, _) => CloseActiveDocument(), "Ctrl+W", null, document: true),
            Sep(),
            Item("E&xit", (_, _) => Close(), "Alt+F4")));

        _menu.Items.Add(Menu("&Edit",
            Item("Undo", (_, _) => EditCommand(t => t.Undo(), e => e.Undo()), "Ctrl+Z", Glyphs.Revert),
            Item("Redo", (_, _) => EditCommand(_ => { }, e => e.Redo()), "Ctrl+Y"),
            Sep(),
            Item("Cut", (_, _) => EditCommand(t => t.Cut(), e => e.Cut()), "Ctrl+X"),
            Item("Copy", (_, _) => EditCommand(t => t.Copy(), e => e.Copy()), "Ctrl+C", Glyphs.Copy),
            Item("Paste", (_, _) => EditCommand(t => t.Paste(), e => e.Paste()), "Ctrl+V"),
            Item("Select All", (_, _) => EditCommand(t => t.SelectAll(), e => e.SelectAll()), "Ctrl+A"),
            Sep(),
            Item("Find...", (_, _) => ActiveDoc?.Editor.ShowFind(false), "Ctrl+F", Glyphs.Find, document: true),
            Item("Replace...", (_, _) => ActiveDoc?.Editor.ShowFind(true), "Ctrl+H", null, document: true),
            Item("Find Next", (_, _) => ActiveDoc?.Editor.FindNext(false), "F3", null, document: true),
            Item("Go to Line...", (_, _) => GoToLine(), "Ctrl+G", null, document: true),
            Item("Toggle Comment", (_, _) => ActiveDoc?.Editor.ToggleComment(), "Ctrl+/", null, document: true),
            Sep(),
            Item("Filter Files...", (_, _) => { _filter.Focus(); _filter.SelectAll(); }, "Ctrl+P", Glyphs.Filter)));

        _menu.Items.Add(Menu("&Lua",
            Item("Compile && Apply to Archive", async (_, _) => await ApplyActive(), "F5", Glyphs.Compile, document: true),
            Item("Check Syntax", async (_, _) => await CheckActive(), "F7", Glyphs.Check, document: true),
            Sep(),
            Item("Decompile Selected Script", async (_, _) => { if (SelectedItem is { } i) await OpenItem(i); }, "Enter", Glyphs.Code, archive: true),
            Item("Show Bytecode Listing", async (_, _) => await ShowListingForSelection(), null, null, archive: true),
            Item("Export Decompiled Source...", async (_, _) => { if (SelectedItem is { } i) await ExportSource(i); }, null, null, archive: true),
            Item("Export Script for PC / 3DS...", (_, _) => { if (SelectedItem is { } i) ExportScript(i, GamePlatform.PC); }, null, null, archive: true),
            Item("Export Script for Xbox 360...", (_, _) => { if (SelectedItem is { } i) ExportScript(i, GamePlatform.Xbox360); }, null, null, archive: true),
            Sep(),
            Item("Rewrite goto / Labels for Lua 5.1", (_, _) => FixGotosInActive(), null, Glyphs.Repair, document: true),
            Item("Toolchain Status", (_, _) => LogToolchain(), null, Glyphs.Info)));

        _menu.Items.Add(Menu("&Archive",
            Item("Extract Selected...", async (_, _) => await ExtractSelected(), null, Glyphs.Extract, archive: true),
            Item("Extract All...", async (_, _) => await ExtractFolder(""), null, null, archive: true),
            Sep(),
            Item("Replace Selected...", async (_, _) => await ReplaceSelected(), null, Glyphs.Replace, archive: true),
            Item("Add Files...", async (_, _) => await AddFilesDialog(), null, Glyphs.Add, archive: true),
            Item("Delete Selected", (_, _) => DeleteSelected(), "Del", Glyphs.Delete, archive: true),
            Item("Revert Selected to Original", async (_, _) => await RevertSelected(), null, Glyphs.Revert, archive: true),
            Sep(),
            Item("Copy Path", (_, _) => CopySelectedPath(), null, Glyphs.Copy, archive: true)));

        _menu.Items.Add(Menu("&VFS Index",
            Item("Rebuild Index for Archive on Disk", async (_, _) => await RebuildIndex(), null, Glyphs.Repair, archive: true),
            Item("Verify Index", async (_, _) => await VerifyIndex(), null, Glyphs.Check, archive: true),
            Item("Index Statistics", (_, _) => ShowIndexStats(), null, Glyphs.Info, archive: true),
            Sep(),
            Item("Restore Original Backups (.bak)...", async (_, _) => await RestoreBackups(), null, Glyphs.Revert, archive: true)));

        _menu.Items.Add(Menu("&Help",
            Item("Quick Guide", (_, _) => ShowGuide(), "F1", Glyphs.Info),
            Item("About", (_, _) => ShowAbout())));
    }

    ToolStripButton ToolButton(string text, string glyph, string tip, EventHandler click, bool archive = false, bool document = false)
    {
        var b = new ToolStripButton(text, Theme.Glyph(glyph, C.FG, this.Px(16)), click)
        {
            ToolTipText = tip,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ForeColor = C.FG,
            Padding = new Padding(4, 0, 4, 0),
        };
        if (archive) _needsArchive.Add(b);
        if (document) _needsDocument.Add(b);
        return b;
    }

    void BuildToolbar()
    {
        _toolbar = new ToolStrip
        {
            Renderer = new DarkRenderer(),
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = C.BG3,
            ForeColor = C.FG,
            Padding = new Padding(6, 3, 6, 3),
            ImageScalingSize = new Size(this.Px(16), this.Px(16)),
            Font = Theme.Ui(9f),
        };
        _platformBox = new ToolStripComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            AutoSize = false,
            Width = this.Px(96),
            BackColor = C.BG4,
            ForeColor = C.FG,
            ToolTipText = "Game version: decides the bytecode format scripts are compiled to and the byte order of vfs.bin/vfs.dat",
        };
        _platformBox.Items.AddRange(PlatformOrder.Select(p => (object)p.DisplayName()).ToArray());
        _platformBox.SelectedIndex = 0;
        _platformBox.SelectedIndexChanged += OnPlatformChanged;

        _toolbar.Items.AddRange(
        [
            ToolButton("Open", Glyphs.Open, "Open vfs.zip (PC) or vfs.pak (Xbox 360)  -  Ctrl+O", (_, _) => OpenArchiveDialog()),
            ToolButton("Save", Glyphs.Save, "Save the archive and rebuild vfs.bin / vfs.dat  -  Ctrl+S", async (_, _) => await SaveArchive(false), archive: true),
            new ToolStripSeparator(),
            new ToolStripLabel("Platform") { ForeColor = C.FG_DIM, Margin = new Padding(4, 1, 2, 2) },
            _platformBox,
            new ToolStripSeparator(),
            ToolButton("Extract", Glyphs.Extract, "Extract the selected file or folder", async (_, _) => await ExtractSelected(), archive: true),
            ToolButton("Replace", Glyphs.Replace, "Replace the selected file (a .lua file is compiled automatically for .luac entries)", async (_, _) => await ReplaceSelected(), archive: true),
            ToolButton("Add", Glyphs.Add, "Add files to the selected folder", async (_, _) => await AddFilesDialog(), archive: true),
            ToolButton("Delete", Glyphs.Delete, "Remove the selected file or folder", (_, _) => DeleteSelected(), archive: true),
            new ToolStripSeparator(),
            ToolButton("Compile && Apply", Glyphs.Compile, "Compile the open script back into the archive  -  F5", async (_, _) => await ApplyActive(), document: true),
            ToolButton("Check", Glyphs.Check, "Check the open script for syntax errors  -  F7", async (_, _) => await CheckActive(), document: true),
        ]);
    }

    void BuildStatusBar()
    {
        _statusBar = new StatusStrip { Renderer = new DarkRenderer(), BackColor = C.ACCENT, SizingGrip = false, Font = Theme.Ui(9f), Padding = new Padding(8, 0, 8, 0) };
        _statusText = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.White };
        _progress = new ToolStripProgressBar { Visible = false, Maximum = 1000, Size = new Size(this.Px(180), this.Px(14)) };
        _statusCancel = new ToolStripStatusLabel("Cancel") { IsLink = true, LinkColor = Color.White, ActiveLinkColor = Color.White, Visible = false, Margin = new Padding(8, 0, 8, 0) };
        _statusCancel.Click += (_, _) => _cts?.Cancel();
        _statusCaret = new ToolStripStatusLabel("") { ForeColor = Color.White, Margin = new Padding(12, 0, 12, 0) };
        _statusPending = new ToolStripStatusLabel("") { ForeColor = Color.White, Margin = new Padding(12, 0, 12, 0) };
        _statusFiles = new ToolStripStatusLabel("") { ForeColor = Color.White, Margin = new Padding(12, 0, 12, 0) };
        _statusPlatform = new ToolStripStatusLabel("") { ForeColor = Color.White, Margin = new Padding(12, 0, 0, 0) };
        _statusBar.Items.AddRange([_statusText, _progress, _statusCancel, _statusCaret, _statusPending, _statusFiles, _statusPlatform]);
    }

    void BuildLayout()
    {
        _mainSplit = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5, BackColor = C.BG3, TabStop = false };
        _mainSplit.Panel1.BackColor = C.BG2;
        _mainSplit.Panel2.BackColor = C.BG;

        _treeHeader = new Label
        {
            Dock = DockStyle.Top, Height = 28, Text = "ARCHIVE", ForeColor = C.FG_DIM, BackColor = C.BG2,
            Font = Theme.Ui(8.25f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), UseMnemonic = false,
        };
        var filterRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = C.BG2, Padding = new Padding(8, 3, 8, 7) };
        _filter = new TextBox
        {
            Dock = DockStyle.Fill, BackColor = C.BG, ForeColor = C.FG, BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Filter files  (e.g.  .luac   or   scripts/ui)", Font = Theme.Ui(9.5f),
        };
        _filter.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        _filter.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { _filter.Clear(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter) { _tree.Focus(); e.SuppressKeyPress = true; }
        };
        filterRow.Controls.Add(_filter);

        _tree = new TreeView
        {
            Dock = DockStyle.Fill, BackColor = C.BG2, ForeColor = C.FG, BorderStyle = BorderStyle.None, HideSelection = false,
            FullRowSelect = true, ShowLines = false, ImageList = Icons.Create(this.Px(16)), ItemHeight = this.Px(22),
            Font = Theme.Ui(9f), AllowDrop = true, ShowNodeToolTips = true, Indent = this.Px(16),
        };
        Theme.DarkControl(_tree);
        _tree.NodeMouseDoubleClick += async (_, e) => { if (e.Node.Tag is ArchiveItem item) await OpenItem(item); };
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };
        _tree.AfterSelect += (_, e) => ShowSelectionInfo(e.Node);
        _tree.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && SelectedItem is { } item) { e.SuppressKeyPress = true; await OpenItem(item); }
            else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; DeleteSelected(); }
        };
        _tree.DragEnter += OnDragEnter;
        _tree.DragOver += OnTreeDragOver;
        _tree.DragDrop += OnTreeDragDrop;
        _tree.DragLeave += (_, _) => _dropNode = null;
        _treeMenu = BuildTreeMenu();
        _tree.ContextMenuStrip = _treeMenu;

        _mainSplit.Panel1.Controls.Add(_tree);
        _mainSplit.Panel1.Controls.Add(filterRow);
        _mainSplit.Panel1.Controls.Add(_treeHeader);

        _rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, SplitterWidth = 5, BackColor = C.BG3, TabStop = false };
        _rightSplit.Panel1.BackColor = C.BG;
        _rightSplit.Panel2.BackColor = C.BG;

        _docTabs = new TabStrip { Dock = DockStyle.Top, Height = 32 };
        _docTabs.SelectedChanged += (_, _) => ShowActiveDocument();
        _docTabs.CloseRequested += async (_, tab) => { if (tab.Tag is DocumentView d) await CloseDocument(d); };
        _docHost = new Panel { Dock = DockStyle.Fill, BackColor = C.BG };
        _welcome = new Panel { Dock = DockStyle.Fill, BackColor = C.BG, AutoScroll = true };
        _welcomeContent = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = C.BG };
        _welcome.Controls.Add(_welcomeContent);
        _welcome.Resize += (_, _) => CenterWelcome();
        _docHost.Controls.Add(_welcome);
        _rightSplit.Panel1.Controls.Add(_docHost);
        _rightSplit.Panel1.Controls.Add(_docTabs);

        _output = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f),
            WordWrap = false, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Both, HideSelection = false,
        };
        _output.BackColor = C.BG;
        _output.ForeColor = C.FG;
        Theme.DarkControl(_output);
        _grid = BuildGrid();
        var bottomHost = new Panel { Dock = DockStyle.Fill, BackColor = C.BG };
        bottomHost.Controls.Add(_output);
        bottomHost.Controls.Add(_grid);
        _bottomTabs = new TabStrip { Dock = DockStyle.Top, Height = 28, Closable = false };
        _bottomTabs.Add(new TabStrip.Tab { Text = "Output", Tag = _output });
        _bottomTabs.Add(new TabStrip.Tab { Text = "All Files", Tag = _grid }, select: false);
        _bottomTabs.SelectedChanged += (_, _) =>
        {
            _output.Visible = _bottomTabs.Selected?.Tag == _output;
            _grid.Visible = _bottomTabs.Selected?.Tag == _grid;
        };
        _grid.Visible = false;
        _rightSplit.Panel2.Controls.Add(bottomHost);
        _rightSplit.Panel2.Controls.Add(_bottomTabs);
        _mainSplit.Panel2.Controls.Add(_rightSplit);
    }

    ContextMenuStrip BuildTreeMenu()
    {
        var open = new ToolStripMenuItem("Open / Decompile", null, async (_, _) => { if (SelectedItem is { } i) await OpenItem(i); }) { ForeColor = C.FG, Font = Theme.Ui(9f, FontStyle.Bold) };
        var listing = new ToolStripMenuItem("Show Bytecode Listing", null, async (_, _) => await ShowListingForSelection()) { ForeColor = C.FG };
        var exportSource = new ToolStripMenuItem("Export Decompiled Source...", null, async (_, _) => { if (SelectedItem is { } i) await ExportSource(i); }) { ForeColor = C.FG };
        var exportPc = new ToolStripMenuItem("Export Script for PC / 3DS...", null, (_, _) => { if (SelectedItem is { } i) ExportScript(i, GamePlatform.PC); }) { ForeColor = C.FG };
        var exportXbox = new ToolStripMenuItem("Export Script for Xbox 360...", null, (_, _) => { if (SelectedItem is { } i) ExportScript(i, GamePlatform.Xbox360); }) { ForeColor = C.FG };
        var extract = new ToolStripMenuItem("Extract...", Theme.Glyph(Glyphs.Extract, C.FG_DIM, this.Px(16)), async (_, _) => await ExtractSelected()) { ForeColor = C.FG };
        var replace = new ToolStripMenuItem("Replace...", Theme.Glyph(Glyphs.Replace, C.FG_DIM, this.Px(16)), async (_, _) => await ReplaceSelected()) { ForeColor = C.FG };
        var add = new ToolStripMenuItem("Add Files Here...", Theme.Glyph(Glyphs.Add, C.FG_DIM, this.Px(16)), async (_, _) => await AddFilesDialog()) { ForeColor = C.FG };
        var delete = new ToolStripMenuItem("Delete", Theme.Glyph(Glyphs.Delete, C.FG_DIM, this.Px(16)), (_, _) => DeleteSelected()) { ForeColor = C.FG, ShortcutKeyDisplayString = "Del" };
        var revert = new ToolStripMenuItem("Revert to Original", Theme.Glyph(Glyphs.Revert, C.FG_DIM, this.Px(16)), async (_, _) => await RevertSelected()) { ForeColor = C.FG };
        var copy = new ToolStripMenuItem("Copy Path", Theme.Glyph(Glyphs.Copy, C.FG_DIM, this.Px(16)), (_, _) => CopySelectedPath()) { ForeColor = C.FG };
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = C.BG2, Font = Theme.Ui(9f) };
        menu.Items.AddRange([open, listing, exportSource, exportPc, exportXbox, new ToolStripSeparator(), extract, replace, add, delete, revert, new ToolStripSeparator(), copy]);
        menu.Opening += (_, e) =>
        {
            if (_archive == null || _busy) { e.Cancel = true; return; }
            var item = SelectedItem;
            bool isScript = item != null && item.Extension == ".luac";
            bool isRoot = _tree.SelectedNode?.Tag is string s && s.Length == 0;
            open.Visible = item != null;
            open.Text = isScript ? "Decompile && Edit" : "Open";
            listing.Visible = exportSource.Visible = exportPc.Visible = exportXbox.Visible = isScript;
            replace.Visible = item != null;
            delete.Enabled = !isRoot;
            revert.Enabled = item != null ? item.State != ItemState.Original : _archive.HasChanges;
            extract.Text = item != null ? "Extract..." : isRoot ? "Extract All..." : "Extract Folder...";
        };
        return menu;
    }

    DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, BackgroundColor = C.BG, BorderStyle = BorderStyle.None, GridColor = C.BG3,
            EnableHeadersVisualStyles = false, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = this.Px(26), AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, StandardTab = true,
        };
        grid.RowTemplate.Height = this.Px(22);
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = C.BG, ForeColor = C.FG, SelectionBackColor = C.SELECTION, SelectionForeColor = C.FG, Font = Theme.Ui(9f) };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = C.BG3, ForeColor = C.FG_DIM, SelectionBackColor = C.BG3, SelectionForeColor = C.FG_DIM, Font = Theme.Ui(9f) };
        var right = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight };
        grid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 200, SortMode = DataGridViewColumnSortMode.Programmatic },
            new DataGridViewTextBoxColumn { HeaderText = "Type", Width = this.Px(80), SortMode = DataGridViewColumnSortMode.Programmatic },
            new DataGridViewTextBoxColumn { HeaderText = "Size", Width = this.Px(90), DefaultCellStyle = right, SortMode = DataGridViewColumnSortMode.Programmatic },
            new DataGridViewTextBoxColumn { HeaderText = "Packed", Width = this.Px(90), DefaultCellStyle = right, SortMode = DataGridViewColumnSortMode.Programmatic },
            new DataGridViewTextBoxColumn { HeaderText = "Method", Width = this.Px(70), SortMode = DataGridViewColumnSortMode.Programmatic },
            new DataGridViewTextBoxColumn { HeaderText = "Status", Width = this.Px(90), SortMode = DataGridViewColumnSortMode.Programmatic });
        grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _gridRows.Count) return;
            var item = _gridRows[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => item.Path,
                1 => item.Extension.TrimStart('.'),
                2 => Format.Size(item.Size),
                3 => item.PackedSize is long p ? Format.Size(p) : "-",
                4 => item.Method,
                _ => StateText(item.State),
            };
        };
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _gridRows.Count || e.CellStyle == null) return;
            var state = _gridRows[e.RowIndex].State;
            if (state != ItemState.Original) e.CellStyle.ForeColor = state == ItemState.Added ? C.GREEN : C.ORANGE;
        };
        grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _gridRows.Count) await OpenItem(_gridRows[e.RowIndex]); };
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            _gridSortDescending = _gridSortColumn == e.ColumnIndex && !_gridSortDescending;
            _gridSortColumn = e.ColumnIndex;
            RefreshGrid();
        };
        grid.HandleCreated += (_, _) => { foreach (Control c in grid.Controls) Theme.DarkControl(c); };
        return grid;
    }

    static string StateText(ItemState state) => state switch { ItemState.Modified => "modified", ItemState.Added => "added", _ => "" };

    // ===================================================================== window lifecycle

    void RestoreWindow()
    {
        if (_settings.WindowWidth >= 600 && _settings.WindowHeight >= 400)
        {
            var bounds = new Rectangle(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }
        }
        if (_settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _mainSplit.Panel1MinSize = this.Px(180);
        _mainSplit.SplitterDistance = Math.Clamp(this.Px(_settings.TreeWidth), this.Px(180), Math.Max(this.Px(180), _mainSplit.Width - this.Px(420)));
        _rightSplit.Panel2MinSize = this.Px(60);
        _rightSplit.SplitterDistance = Math.Max(this.Px(160), _rightSplit.Height - this.Px(_settings.OutputHeight));
        Log($"{AppName} ready.");
        LogToolchain(onlyProblems: true);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_args.Length == 0) return;
        string path = _args[0];
        if (!File.Exists(path)) { Log($"Command line: file not found: {path}", LogKind.Error); return; }
        await OpenArchive(path);
        int open = Array.FindIndex(_args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (open >= 0 && open + 1 < _args.Length && _archive?.Find(_args[open + 1]) is { } item)
        {
            SelectNode(item.Path);
            await OpenItem(item);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceClose && (_busy || HasUnsavedWork()))
        {
            e.Cancel = true;
            if (_busy) { Dialogs.Info(this, "Please wait for the current operation to finish."); return; }
            BeginInvoke(new Action(async () =>
            {
                if (await ConfirmDiscard("closing"))
                {
                    _forceClose = true;
                    Close();
                }
            }));
            return;
        }
        SaveWindowSettings();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _archive?.Dispose();
        base.OnFormClosed(e);
    }

    void SaveWindowSettings()
    {
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        (_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight) = (b.X, b.Y, b.Width, b.Height);
        _settings.TreeWidth = (int)Math.Round(_mainSplit.SplitterDistance * 96.0 / DeviceDpi);
        _settings.OutputHeight = (int)Math.Round((_rightSplit.Height - _rightSplit.SplitterDistance) * 96.0 / DeviceDpi);
        _settings.Save();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.O: OpenArchiveDialog(); return true;
            case Keys.Control | Keys.S: _ = SaveArchive(false); return true;
            case Keys.Control | Keys.Shift | Keys.S: _ = SaveArchive(true); return true;
            case Keys.F5: _ = ApplyActive(); return true;
            case Keys.F7: _ = CheckActive(); return true;
            case Keys.F1: ShowGuide(); return true;
            case Keys.Control | Keys.W: CloseActiveDocument(); return true;
            case Keys.Control | Keys.Tab: _docTabs.SelectNext(false); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: _docTabs.SelectNext(true); return true;
            case Keys.Control | Keys.P: _filter.Focus(); _filter.SelectAll(); return true;
            case Keys.Control | Keys.G when ActiveDoc != null: GoToLine(); return true;
            case Keys.Control | Keys.F when ActiveDoc != null && !_filter.Focused: ActiveDoc.Editor.ShowFind(false); return true;
            case Keys.Control | Keys.H when ActiveDoc != null && !_filter.Focused: ActiveDoc.Editor.ShowFind(true); return true;
            case Keys.F3 when ActiveDoc != null: ActiveDoc.Editor.FindNext(false); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    bool HasUnsavedWork() => (_archive?.HasChanges ?? false) || _docs.Any(d => d.Dirty);

    /// <summary>Offers to save pending work. Returns false if the user cancelled.</summary>
    async Task<bool> ConfirmDiscard(string action)
    {
        if (!HasUnsavedWork()) return true;
        int pending = _archive?.PendingChanges ?? 0, edited = _docs.Count(d => d.Dirty);
        var parts = new List<string>();
        if (pending > 0) parts.Add($"{pending} unsaved change{(pending == 1 ? "" : "s")} in {_archive!.DisplayName}");
        if (edited > 0) parts.Add($"{edited} edited tab{(edited == 1 ? "" : "s")} not applied yet");
        var answer = Dialogs.Ask(this, $"There {(pending + edited == 1 ? "is" : "are")} {string.Join(" and ", parts)}.\n\nSave the archive before {action}?",
            "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.No) return true;
        return await SaveArchive(false);
    }

    // ===================================================================== archive open / close / save

    void OpenArchiveDialog()
    {
        if (_busy) return;
        using var dlg = new OpenFileDialog
        {
            Title = "Open game archive",
            Filter = "Game archives (vfs*.zip; vfs*.pak)|*.zip;*.pak|PC archive (*.zip)|*.zip|Xbox 360 archive (*.pak)|*.pak|All files (*.*)|*.*",
        };
        if (_settings.RecentArchives.FirstOrDefault(File.Exists) is { } recent) dlg.InitialDirectory = Path.GetDirectoryName(recent);
        if (dlg.ShowDialog(this) == DialogResult.OK) _ = OpenArchive(dlg.FileName);
    }

    async Task OpenArchive(string path)
    {
        if (_busy || !await ConfirmDiscard("opening another archive")) return;
        CloseAllDocuments();
        _archive?.Dispose();
        _archive = null;
        PopulateTree();
        RefreshGrid();

        GameArchive? opened = null;
        var sw = Stopwatch.StartNew();
        bool ok = await RunBusy("Open archive", async (_, _) => opened = await Task.Run(() => GameArchive.Open(path)), cancellable: false);
        if (!ok || opened == null)
        {
            RefreshWelcome();
            UpdateUiState();
            return;
        }

        _archive = opened;
        _settings.AddRecent(_archive.FilePath!);
        BuildRecentMenu();
        SyncPlatformBox();
        _filter.Text = "";
        _filterTimer.Stop();
        PopulateTree();
        RefreshGrid();
        RefreshWelcome();
        UpdateUiState();

        int scripts = _archive.Items.Count(i => i.Extension == ".luac");
        Log($"Opened {_archive.FilePath}: {_archive.FileCount:N0} files ({scripts} Lua scripts), {Format.Size(new FileInfo(_archive.FilePath!).Length)}, in {sw.ElapsedMilliseconds} ms.", LogKind.Success);
        Log($"Platform: {_archive.Platform.DisplayName()} - {_archive.DetectionReason}. Scripts compile to {_archive.Platform.LuaFormat().Describe()}; the index is {(_archive.Platform.IndexIsBigEndian() ? "big" : "little")}-endian.");
        var (bin, dat) = VfsIndex.CompanionPaths(_archive.FilePath!);
        if (!File.Exists(bin) || !File.Exists(dat))
            Log($"No {Path.GetFileName(bin)} / {Path.GetFileName(dat)} next to the archive - saving will create them.", LogKind.Warning);
        foreach (var w in _archive.Warnings) Log(w, LogKind.Warning);
    }

    async Task CloseArchive()
    {
        if (_archive == null || _busy || !await ConfirmDiscard("closing it")) return;
        CloseAllDocuments();
        _archive.Dispose();
        _archive = null;
        PopulateTree();
        RefreshGrid();
        RefreshWelcome();
        UpdateUiState();
        Log("Archive closed.");
    }

    async Task DiscardChanges()
    {
        if (_archive == null || _busy) return;
        if (!HasUnsavedWork()) { Log("There are no unsaved changes."); return; }
        if (_archive.FilePath == null)
        {
            Dialogs.Info(this, "This archive has never been saved, so there is nothing to go back to.");
            return;
        }
        if (Dialogs.Ask(this, $"Throw away all {_archive.PendingChanges} unsaved change(s) and edits, and reload {_archive.DisplayName} from disk?",
                "Discard changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        string path = _archive.FilePath;
        CloseAllDocuments();
        _archive.Dispose();
        _archive = null;
        await OpenArchive(path);
    }

    async Task NewFromFolder()
    {
        if (_busy || !await ConfirmDiscard("creating a new archive")) return;
        using var dlg = new FolderBrowserDialog { Description = "Choose the folder whose files become the new archive (for example an extracted vfs folder)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var platform = Dialogs.ChoosePlatform(this,
            "Which version of the game is this archive for?\n\nPC: vfs.zip with a little-endian index.\nXbox 360: vfs.pak with a big-endian index.\n3DS: little-endian index, same script format as PC.", _newArchivePlatform);
        if (platform == null) return;

        GameArchive archive;
        try { archive = GameArchive.FromFolder(dlg.SelectedPath, platform.Value); }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException) { ShowError("Could not read the folder", ex); return; }
        CloseAllDocuments();
        _archive?.Dispose();
        _archive = archive;
        SyncPlatformBox();
        Log($"New {platform.Value.DisplayName()} archive from {dlg.SelectedPath}: {archive.FileCount:N0} files. Use File > Save Archive As to write it.", LogKind.Success);

        var mismatched = archive.Items.Where(i => i.Extension == ".luac").Select(i => (Item: i, Data: SafeRead(i)))
            .Where(x => x.Data != null && LuaBytecode.IsBytecode(x.Data) && LuaBytecode.ReadFormat(x.Data) != platform.Value.LuaFormat()).ToList();
        if (mismatched.Count > 0 && Dialogs.Ask(this, $"{mismatched.Count} script(s) in this folder are compiled for the other platform.\n\nConvert them to {platform.Value.DisplayName()} format?",
                "Convert scripts") == DialogResult.Yes)
        {
            foreach (var (item, data) in mismatched) archive.SetData(item.Path, LuaBytecode.Convert(data!, platform.Value.LuaFormat()));
            Log($"Converted {mismatched.Count} script(s) to {platform.Value.DisplayName()} bytecode.", LogKind.Success);
        }
        PopulateTree();
        RefreshGrid();
        RefreshWelcome();
        UpdateUiState();
    }

    byte[]? SafeRead(ArchiveItem item)
    {
        try { return _archive?.ReadData(item); }
        catch (Exception ex) when (ex is ArchiveException or IOException) { return null; }
    }

    /// <summary>Applies edited tabs, then writes the archive and its index. Returns false if nothing was saved.</summary>
    async Task<bool> SaveArchive(bool saveAs)
    {
        if (_archive == null || _busy) return false;
        foreach (var doc in _docs.Where(d => d.Dirty && d.Editable).ToList())
        {
            if (!await ApplyDocument(doc))
            {
                ActivateDocument(doc);
                Log("Save stopped: fix the error in the highlighted tab first (or close it without applying).", LogKind.Error);
                return false;
            }
        }

        string? target = _archive.FilePath;
        if (saveAs || target == null)
        {
            bool zip = _archive.Platform != GamePlatform.Xbox360;
            using var dlg = new SaveFileDialog
            {
                Title = "Save archive as",
                Filter = zip ? $"{_archive.Platform.DisplayName()} archive (*.zip)|*.zip|All files (*.*)|*.*" : "Xbox 360 archive (*.pak)|*.pak|All files (*.*)|*.*",
                FileName = target != null ? Path.GetFileName(target) : zip ? "vfs.zip" : "vfs.pak",
                InitialDirectory = target != null ? Path.GetDirectoryName(target) : null,
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            target = dlg.FileName;
        }
        else if (!_archive.HasChanges)
        {
            Log("Nothing to save - there are no pending changes.");
            return true;
        }

        if (_archive.Platform != _archive.DetectedPlatform &&
            Dialogs.Ask(this, $"This archive was detected as {_archive.DetectedPlatform.DisplayName()}, but the platform is set to {_archive.Platform.DisplayName()}.\n\nThe index will be written in {_archive.Platform.DisplayName()} format. Continue?",
                "Platform mismatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;

        int pending = _archive.PendingChanges;
        SaveResult? result = null;
        var sw = Stopwatch.StartNew();
        bool ok = await RunBusy("Save", async (progress, ct) => result = await Task.Run(() => _archive.Save(target, progress, ct), ct), cancellable: true);
        if (!ok || result == null)
        {
            PopulateTree();
            UpdateUiState();
            return false;
        }

        _settings.AddRecent(result.ArchivePath);
        BuildRecentMenu();
        PopulateTree();
        RefreshGrid();
        RefreshWelcome();
        UpdateUiState();
        Log($"Saved {result.ArchivePath} ({Format.Size(result.ArchiveSize)}) with {pending} change(s) in {sw.Elapsed.TotalSeconds:0.0} s.", LogKind.Success);
        Log($"Index rebuilt: {Path.GetFileName(result.BinPath)} + {Path.GetFileName(result.DatPath)} - {result.Index.FileCount:N0} files, {result.Index.NodeCount:N0} nodes, {(_archive.Platform.IndexIsBigEndian() ? "big" : "little")}-endian. Verified every offset and changed file.", LogKind.Success);
        if (result.Backups.Count > 0)
            Log($"Originals backed up: {string.Join(", ", result.Backups.Select(Path.GetFileName))} (VFS Index > Restore Original Backups puts them back).");
        return true;
    }

    async Task<bool> RunBusy(string what, Func<IProgress<SaveProgress>, CancellationToken, Task> work, bool cancellable)
    {
        if (_busy) return false;
        _busy = true;
        _cts = new CancellationTokenSource();
        SetBusy(true, cancellable);
        var progress = new Progress<SaveProgress>(p =>
        {
            int permille = p.Total > 0 ? (int)Math.Clamp(p.Done * 1000 / p.Total, 0, 1000) : 0;
            _progress.Value = permille;
            _statusText.Text = p.Current != null ? $"{p.Phase} {permille / 10}%  -  {p.Current}" : $"{p.Phase}...";
        });
        _statusText.Text = what + "...";
        try
        {
            await work(progress, _cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log($"{what} cancelled - nothing was changed.", LogKind.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Log($"{what} failed: {ex.Message}", LogKind.Error);
            Dialogs.Error(this, ex.Message, what + " failed");
            return false;
        }
        finally
        {
            _busy = false;
            _cts.Dispose();
            _cts = null;
            SetBusy(false, false);
        }
    }

    void SetBusy(bool busy, bool cancellable)
    {
        UseWaitCursor = busy;
        _menu.Enabled = !busy;
        _toolbar.Enabled = !busy;
        _mainSplit.Enabled = !busy;
        _progress.Value = 0;
        _progress.Visible = busy;
        _statusCancel.Visible = busy && cancellable;
        if (!busy) _statusText.Text = "Ready";
        UpdateUiState();
    }

    // ===================================================================== platform

    static readonly GamePlatform[] PlatformOrder = [GamePlatform.PC, GamePlatform.Xbox360, GamePlatform.Nintendo3DS];

    void SyncPlatformBox()
    {
        _syncingPlatform = true;
        _platformBox.SelectedIndex = Array.IndexOf(PlatformOrder, _archive?.Platform ?? _newArchivePlatform);
        _syncingPlatform = false;
    }

    void OnPlatformChanged(object? sender, EventArgs e)
    {
        if (_syncingPlatform || _platformBox.SelectedIndex < 0) return;
        var chosen = PlatformOrder[_platformBox.SelectedIndex];
        if (_archive == null)
        {
            _newArchivePlatform = chosen;
            UpdateUiState();
            return;
        }
        if (chosen == _archive.Platform) return;
        if (chosen != _archive.DetectedPlatform &&
            Dialogs.Ask(this, $"{_archive.DisplayName} was detected as {_archive.DetectedPlatform.DisplayName()} ({_archive.DetectionReason}).\n\n" +
                $"Switching to {chosen.DisplayName()} makes scripts you compile, and the index written when you save, use the {chosen.DisplayName()} format. " +
                "Scripts already in the archive are not converted.\n\nOnly do this if the detection is wrong. Switch?",
                "Change platform", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            SyncPlatformBox();
            return;
        }
        _archive.Platform = chosen;
        Log($"Platform set to {chosen.DisplayName()}: scripts compile to {chosen.LuaFormat().Describe()}; the index is {(chosen.IndexIsBigEndian() ? "big" : "little")}-endian.",
            chosen == _archive.DetectedPlatform ? LogKind.Info : LogKind.Warning);
        PopulateTree();
        RefreshWelcome();
        UpdateUiState();
    }

    // ===================================================================== tree

    ArchiveItem? SelectedItem => _tree.SelectedNode?.Tag as ArchiveItem;

    string SelectedFolder => _tree.SelectedNode?.Tag switch
    {
        ArchiveItem item => ParentOf(item.Path),
        string folder => folder,
        _ => "",
    };

    static string ParentOf(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    static string Combine(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    IEnumerable<ArchiveItem> ItemsUnder(string folder) =>
        _archive?.Items.Where(i => !i.IsDirectory && (folder.Length == 0 || i.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
        ?? Enumerable.Empty<ArchiveItem>();

    sealed class FolderNode(string name, string path, TreeNode node)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public TreeNode Node { get; } = node;
        public Dictionary<string, FolderNode> Sub { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ArchiveItem> Files { get; } = new();
    }

    void PopulateTree()
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpanded(_tree.Nodes, expanded);
        string? selected = _tree.SelectedNode?.Tag switch { ArchiveItem i => i.Path, string s => "/" + s, _ => null };
        bool firstFill = _tree.Nodes.Count == 0;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        _fileNodes.Clear();
        if (_archive == null)
        {
            _tree.EndUpdate();
            _treeHeader.Text = "ARCHIVE";
            return;
        }

        string[] terms = _filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rootNode = new TreeNode($"{_archive.DisplayName}  ({_archive.Platform.DisplayName()})") { Tag = "", ImageKey = "archive", SelectedImageKey = "archive", ForeColor = C.FG };
        var root = new FolderNode("", "", rootNode);
        int shown = 0;
        foreach (var item in _archive.Items)
        {
            if (item.IsDirectory) continue;
            if (terms.Length > 0 && !terms.All(t => item.Path.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            shown++;
            var folder = root;
            string[] parts = item.Path.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!folder.Sub.TryGetValue(parts[i], out var sub))
                {
                    string path = Combine(folder.Path, parts[i]);
                    sub = new FolderNode(parts[i], path, new TreeNode(parts[i]) { Tag = path, ImageKey = "folder", SelectedImageKey = "folder", ForeColor = C.FG });
                    folder.Sub[parts[i]] = sub;
                }
                folder = sub;
            }
            folder.Files.Add(item);
        }

        void Fill(FolderNode f)
        {
            var children = new List<TreeNode>(f.Sub.Count + f.Files.Count);
            foreach (var sub in f.Sub.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                Fill(sub);
                children.Add(sub.Node);
            }
            foreach (var item in f.Files.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var node = new TreeNode { Tag = item, ImageKey = Icons.KeyFor(item.Path) };
                node.SelectedImageKey = node.ImageKey;
                StyleFileNode(node, item);
                _fileNodes[item.Path] = node;
                children.Add(node);
            }
            f.Node.Nodes.AddRange(children.ToArray());
        }
        Fill(root);
        _tree.Nodes.Add(rootNode);

        rootNode.Expand();
        if (terms.Length > 0 && shown <= 800) rootNode.ExpandAll();
        else if (firstFill || expanded.Count == 0)
        {
            foreach (TreeNode n in rootNode.Nodes)
                if (n.Tag is string) n.Expand();
        }
        else RestoreExpanded(rootNode.Nodes, expanded);

        TreeNode? toSelect = null;
        if (selected != null)
            toSelect = selected.StartsWith('/') ? FindFolderNode(rootNode, selected[1..]) : _fileNodes.GetValueOrDefault(selected);
        _tree.SelectedNode = toSelect ?? rootNode;
        _tree.EndUpdate();
        if (toSelect != null && toSelect != rootNode) toSelect.EnsureVisible();
        else _tree.TopNode = rootNode;

        _treeHeader.Text = terms.Length > 0 ? $"ARCHIVE  -  {shown:N0} OF {_archive.FileCount:N0} FILES MATCH" : $"ARCHIVE  -  {_archive.FileCount:N0} FILES";
    }

    static void CollectExpanded(TreeNodeCollection nodes, HashSet<string> into)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.Tag is string path && n.IsExpanded)
            {
                into.Add(path);
                CollectExpanded(n.Nodes, into);
            }
        }
    }

    static void RestoreExpanded(TreeNodeCollection nodes, HashSet<string> expanded)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.Tag is string path && expanded.Contains(path))
            {
                n.Expand();
                RestoreExpanded(n.Nodes, expanded);
            }
        }
    }

    static TreeNode? FindFolderNode(TreeNode root, string path)
    {
        if (path.Length == 0) return root;
        var node = root;
        foreach (string part in path.Split('/'))
        {
            node = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is string && string.Equals(n.Text, part, StringComparison.OrdinalIgnoreCase));
            if (node == null) return null;
        }
        return node;
    }

    static void StyleFileNode(TreeNode node, ArchiveItem item)
    {
        node.Text = item.State switch
        {
            ItemState.Modified => item.Name + "   ●",
            ItemState.Added => item.Name + "   +",
            _ => item.Name,
        };
        node.ForeColor = item.State switch
        {
            ItemState.Modified => C.ORANGE,
            ItemState.Added => C.GREEN,
            _ => item.Extension == ".luac" ? C.FG : Color.FromArgb(175, 175, 175),
        };
        node.ToolTipText = $"{item.Path}\n{Format.Size(item.Size)}{(item.State != ItemState.Original ? $"  ({StateText(item.State)}, not saved yet)" : "")}";
    }

    void UpdateFileNode(ArchiveItem item)
    {
        if (_fileNodes.TryGetValue(item.Path, out var node)) StyleFileNode(node, item);
        else PopulateTree();
        _grid.Invalidate();
        UpdateUiState();
    }

    void SelectNode(string path)
    {
        if (_fileNodes.TryGetValue(path, out var node))
        {
            _tree.SelectedNode = node;
            node.EnsureVisible();
        }
    }

    void ShowSelectionInfo(TreeNode? node)
    {
        if (_busy) return;
        if (node?.Tag is ArchiveItem item)
            SetStatus($"{item.Path}  -  {Format.Size(item.Size)}{(item.PackedSize is long p ? $" ({Format.Size(p)} packed, {item.Method})" : "")}{(item.State != ItemState.Original ? "  -  " + StateText(item.State) : "")}");
        else if (node?.Tag is string folder && _archive != null)
        {
            var items = ItemsUnder(folder).ToList();
            SetStatus($"{(folder.Length == 0 ? _archive.DisplayName : folder + "/")}  -  {items.Count:N0} files, {Format.Size(items.Sum(i => i.Size))}");
        }
    }

    void RefreshGrid()
    {
        if (_archive == null)
        {
            _gridRows = new();
            _grid.RowCount = 0;
            return;
        }
        string[] terms = _filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = _archive.Items.Where(i => !i.IsDirectory && terms.All(t => i.Path.Contains(t, StringComparison.OrdinalIgnoreCase)));
        rows = _gridSortColumn switch
        {
            1 => rows.OrderBy(i => i.Extension).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase),
            2 => rows.OrderBy(i => i.Size),
            3 => rows.OrderBy(i => i.PackedSize ?? -1),
            4 => rows.OrderBy(i => i.Method),
            5 => rows.OrderBy(i => i.State).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase),
        };
        _gridRows = (_gridSortDescending ? rows.Reverse() : rows).ToList();
        _grid.RowCount = 0;
        _grid.RowCount = _gridRows.Count;
        foreach (DataGridViewColumn col in _grid.Columns)
            col.HeaderCell.SortGlyphDirection = col.Index == _gridSortColumn ? (_gridSortDescending ? SortOrder.Descending : SortOrder.Ascending) : SortOrder.None;
        _grid.Invalidate();
    }

    // ===================================================================== documents

    DocumentView? ActiveDoc => _docTabs.Selected?.Tag as DocumentView;

    DocumentView? FindDocument(string entryPath) =>
        _docs.FirstOrDefault(d => d.Kind != DocumentKind.ExternalScript && string.Equals(d.EntryPath, entryPath, StringComparison.OrdinalIgnoreCase));

    void AddDocument(DocumentView doc)
    {
        doc.Dock = DockStyle.Fill;
        doc.Visible = false;
        _docHost.Controls.Add(doc);
        _docs.Add(doc);
        var tab = new TabStrip.Tab { Text = doc.Title, ToolTip = doc.EntryPath ?? doc.ExternalPath ?? doc.Title, Tag = doc };
        doc.Tab = tab;
        doc.DirtyChanged += (_, _) =>
        {
            tab.Dirty = doc.Dirty;
            _docTabs.Invalidate();
            UpdateUiState();
        };
        doc.Editor.CaretMoved += (_, _) => UpdateCaret();
        _docTabs.Add(tab);
    }

    void ActivateDocument(DocumentView doc)
    {
        if (doc.Tab != null) _docTabs.Selected = doc.Tab;
    }

    void ShowActiveDocument()
    {
        var active = ActiveDoc;
        foreach (var d in _docs) d.Visible = d == active;
        _welcome.Visible = active == null;
        if (active != null)
        {
            active.BringToFront();
            if (!active.Loading) active.Editor.FocusEditor();
            if (active.EntryPath != null && _fileNodes.TryGetValue(active.EntryPath, out var node) && _tree.SelectedNode != node)
            {
                _tree.SelectedNode = node;
                node.EnsureVisible();
            }
        }
        else RefreshWelcome();
        UpdateCaret();
        UpdateUiState();
    }

    void CloseActiveDocument()
    {
        if (ActiveDoc is { } doc) _ = CloseDocument(doc);
    }

    async Task CloseDocument(DocumentView doc)
    {
        if (doc.Dirty)
        {
            var answer = Dialogs.Ask(this, $"Apply your edits to {doc.Title} before closing the tab?\n\nYes = compile/apply into the archive, No = throw the edits away.",
                "Close tab", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes && !await ApplyDocument(doc)) return;
        }
        RemoveDocument(doc);
    }

    void RemoveDocument(DocumentView doc)
    {
        if (doc.Tab != null) _docTabs.Remove(doc.Tab);
        _docs.Remove(doc);
        _docHost.Controls.Remove(doc);
        doc.Dispose();
        ShowActiveDocument();
    }

    void CloseAllDocuments()
    {
        foreach (var doc in _docs.ToList()) RemoveDocument(doc);
    }

    async Task OpenItem(ArchiveItem item)
    {
        if (_archive == null || _busy || item.IsDirectory) return;
        if (FindDocument(item.Path) is { } existing)
        {
            ActivateDocument(existing);
            return;
        }
        byte[] data;
        try { data = await Task.Run(() => _archive.ReadData(item)); }
        catch (Exception ex) when (ex is ArchiveException or IOException) { ShowError($"Could not read {item.Path}", ex); return; }

        if (LuaBytecode.IsBytecode(data)) await OpenScript(item, data);
        else if (TextFormat.LooksLikeText(item.Path, data)) OpenText(item, data);
        else OpenViewer(item.Name, item, HexDump(data, HexLimit), $"Binary file  -  {Format.Size(data.Length)}  -  read-only hex view{(data.Length > HexLimit ? $" of the first {Format.Size(HexLimit)}" : "")}");
    }

    async Task OpenScript(ArchiveItem item, byte[] data)
    {
        LuaFormat format;
        LuaFunction main;
        try { (format, main) = LuaBytecode.Parse(data); }
        catch (LuaBytecodeException ex)
        {
            OpenViewer(item.Name, item, HexDump(data, HexLimit), "Not valid Lua 5.1 bytecode - " + ex.Message);
            return;
        }

        var doc = new DocumentView(DocumentKind.Script, item.Name, item.Path)
        {
            SourceFormat = format,
            ChunkName = LuaBytecode.ReadChunkName(data) ?? "",
            OriginalBytecode = data,
            Loading = true,
        };
        doc.AddAction("Compile & Apply", Glyphs.Compile, "Compile this script and put it back into the archive (F5)", async (_, _) => await ApplyDocument(doc), primary: true);
        doc.AddAction("Check", Glyphs.Check, "Check for syntax errors without changing anything (F7)", async (_, _) => await CheckDocument(doc));
        doc.AddAction("Bytecode", Glyphs.Code, "Show the bytecode listing (luac -l)", async (_, _) => await ShowListing(doc.Title, doc.OriginalBytecode ?? data));
        doc.AddAction("Reload", Glyphs.Refresh, "Discard edits and decompile the archive's copy again", async (_, _) => await ReloadDocument(doc, ask: true));
        doc.SetInfo($"{format.PlatformName} bytecode  -  decompiling...");
        doc.Editor.SetText($"-- Decompiling {item.Path} ...", true);
        doc.Editor.ReadOnly = true;
        AddDocument(doc);
        await DecompileInto(doc, data, IsStripped(main));
    }

    static bool IsStripped(LuaFunction main) => main.Source == null && main.LineInfo.Length == 0;

    async Task DecompileInto(DocumentView doc, byte[] data, bool stripped)
    {
        doc.Loading = true;
        doc.HideMessage();
        string fileName = Path.GetFileNameWithoutExtension(doc.EntryPath ?? doc.Title) + ".lua";
        if (!_tools.CanDecompile)
        {
            doc.Editor.SetText("-- The decompiler is not available.\n-- " + _tools.Describe().Replace("\n", "\n-- "), true);
            doc.ShowMessage(_tools.JavaPath == null ? "Java was not found - install Java 8 or newer to decompile scripts, then restart the editor." : "unluac.jar is missing from the tools folder.", true);
            doc.Loading = false;
            return;
        }
        SetStatus($"Decompiling {doc.EntryPath}...");
        var sw = Stopwatch.StartNew();
        try
        {
            var (source, check, gotoFix) = await Task.Run(() =>
            {
                string s = _tools.Decompile(data);
                var c = _tools.CheckSyntax(s, fileName);
                LuaGotoFixer.Result? fix = null;
                if (!c.Success && LuaGotoFixer.HasGotos(s))
                {
                    fix = LuaGotoFixer.Fix(s);
                    if (fix.Problem == null && fix.GotosFixed > 0 && _tools.CheckSyntax(fix.Source, fileName) is { Success: true } recheck)
                    {
                        s = fix.Source;
                        c = recheck;
                    }
                    else fix = fix with { GotosFixed = 0, Problem = fix.Problem ?? "the rewritten script still does not compile" };
                }
                return (s, c, fix);
            });
            if (doc.IsDisposed) return;
            doc.Editor.SetText(source, true);
            doc.Editor.ReadOnly = false;
            doc.Loading = false;
            doc.Dirty = false;
            var format = doc.SourceFormat!;
            string target = _archive?.Platform.DisplayName() ?? format.PlatformName;
            doc.SetInfo($"Decompiled {format.PlatformName} bytecode" + (stripped ? "  -  no debug info, so local names are generated" : "") + $"  -  F5 compiles for {target}");
            Log($"Decompiled {doc.EntryPath} ({source.Count(c => c == '\n') + 1:N0} lines) in {sw.ElapsedMilliseconds} ms.");
            if (gotoFix is { GotosFixed: > 0 })
            {
                doc.ShowMessage($"Auto-fixed {gotoFix.GotosFixed} 'goto' jump(s) from unluac (Lua 5.1 has no goto) into repeat/break blocks marked --[[goto ...]]. " +
                    "The script compiles; review those spots, since unluac may not have rebuilt them exactly.", false);
                Log($"{doc.EntryPath}: rewrote {gotoFix.GotosFixed} goto jump(s) unluac produced into Lua 5.1 code; the script now compiles.", LogKind.Warning);
            }
            else if (!check.Success)
            {
                string why = gotoFix?.Problem != null ? $" The automatic goto rewrite was not possible: {gotoFix.Problem}." : "";
                doc.ShowMessage($"unluac could not rebuild part of this script, so it will not compile until that part is fixed by hand: {check.Error}.{why}", false, check.Line ?? -1);
                Log($"{doc.EntryPath}: the decompiled source does not compile as-is ({check.Error}).{why}", LogKind.Warning);
            }
            if (doc == ActiveDoc) doc.Editor.FocusEditor();
        }
        catch (Exception ex)
        {
            if (doc.IsDisposed) return;
            doc.Loading = false;
            string listing;
            try { listing = await Task.Run(() => _tools.Disassemble(data)); }
            catch (Exception) { listing = ""; }
            doc.Editor.SetText("-- Decompiling failed:\n-- " + ex.Message.Replace("\n", "\n-- ") + "\n\n" + listing, false);
            doc.Editor.ReadOnly = true;
            doc.ShowMessage("Could not decompile this script - the bytecode listing is shown instead. " + FirstLine(ex.Message), true);
            Log($"Decompiling {doc.EntryPath} failed: {FirstLine(ex.Message)}", LogKind.Error);
        }
        finally
        {
            SetStatus("Ready");
            UpdateUiState();
        }
    }

    void OpenText(ArchiveItem item, byte[] data)
    {
        var (text, encoding) = TextFormat.Decode(data);
        var doc = new DocumentView(DocumentKind.Text, item.Name, item.Path) { Encoding = encoding };
        doc.AddAction("Apply", Glyphs.Check, "Write your edits into the archive (F5)", async (_, _) => await ApplyDocument(doc), primary: true);
        doc.AddAction("Reload", Glyphs.Refresh, "Discard edits and reload from the archive", async (_, _) => await ReloadDocument(doc, ask: true));
        doc.SetInfo($"Text  -  {encoding.Describe()}  -  {Format.Size(data.Length)}  -  F5 applies your edits to the archive");
        doc.Editor.SetText(text, item.Extension == ".lua");
        AddDocument(doc);
    }

    void OpenViewer(string title, ArchiveItem? item, string content, string info)
    {
        var doc = new DocumentView(DocumentKind.Viewer, title, item?.Path);
        if (item != null)
        {
            doc.AddAction("Extract...", Glyphs.Extract, "Save this file to disk", async (_, _) => await ExtractFile(item));
            doc.AddAction("Replace...", Glyphs.Replace, "Replace this file with one from disk", async (_, _) => await ReplaceItem(item));
        }
        doc.SetInfo(info);
        doc.Editor.SetText(content, false);
        doc.Editor.ReadOnly = true;
        AddDocument(doc);
    }

    static string HexDump(byte[] data, int limit)
    {
        int length = Math.Min(data.Length, limit);
        var sb = new StringBuilder(length * 4 + 64);
        for (int i = 0; i < length; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++)
            {
                if (i + j < length) sb.Append(data[i + j].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < length; j++)
            {
                byte b = data[i + j];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }
            sb.Append('\n');
        }
        if (data.Length > limit) sb.Append($"... {data.Length - limit:N0} more bytes (extract the file to see all of it)\n");
        return sb.ToString();
    }

    async Task ReloadDocument(DocumentView doc, bool ask)
    {
        if (_archive == null || doc.EntryPath == null || doc.Loading) return;
        if (ask && doc.Dirty && Dialogs.Ask(this, $"Discard your edits to {doc.Title}?", "Reload", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var item = _archive.Find(doc.EntryPath);
        if (item == null)
        {
            RemoveDocument(doc);
            return;
        }
        byte[] data;
        try { data = await Task.Run(() => _archive.ReadData(item)); }
        catch (Exception ex) when (ex is ArchiveException or IOException) { ShowError($"Could not read {item.Path}", ex); return; }

        switch (doc.Kind)
        {
            case DocumentKind.Script when LuaBytecode.IsBytecode(data):
                var (format, main) = LuaBytecode.Parse(data);
                doc.SourceFormat = format;
                doc.ChunkName = LuaBytecode.ReadChunkName(data) ?? "";
                doc.OriginalBytecode = data;
                doc.Editor.ReadOnly = true;
                await DecompileInto(doc, data, IsStripped(main));
                break;
            case DocumentKind.Text:
                var (text, encoding) = TextFormat.Decode(data);
                doc.Encoding = encoding;
                doc.Loading = true;
                doc.Editor.SetText(text, doc.Editor.IsLua);
                doc.Loading = false;
                doc.Dirty = false;
                break;
            case DocumentKind.Viewer:
                doc.Editor.SetText(HexDump(data, HexLimit), false);
                break;
        }
    }

    /// <summary>Puts a tab's edits into the archive (compiling scripts). Returns false on errors.</summary>
    async Task<bool> ApplyDocument(DocumentView doc)
    {
        if (_archive == null) { Dialogs.Info(this, "Open an archive first."); return false; }
        if (doc.Loading || !doc.Editable || doc.Editor.ReadOnly) return doc.Kind == DocumentKind.Viewer;
        doc.HideMessage();

        if (doc.Kind == DocumentKind.Text)
        {
            var encoding = doc.Encoding ?? TextEncodingInfo.Utf8Lf;
            string text = doc.Editor.Text;
            if (!encoding.CanEncode(text))
            {
                doc.ShowMessage($"The text contains characters that {encoding.Describe()} cannot store.", true);
                return false;
            }
            var item = _archive.SetData(doc.EntryPath!, TextFormat.Encode(text, encoding));
            doc.Dirty = false;
            UpdateFileNode(item);
            Log($"Applied {item.Path} ({Format.Size(item.Size)}). Press Ctrl+S to save the archive.", LogKind.Success);
            return true;
        }

        if (doc.Kind == DocumentKind.Script && !doc.Dirty && _archive.Find(doc.EntryPath!)?.State == ItemState.Original)
        {
            Log($"{doc.Title}: no edits to apply.");
            return true;
        }

        string? entry = doc.EntryPath;
        if (doc.Kind == DocumentKind.ExternalScript && entry == null)
        {
            string guess = _archive.Items.FirstOrDefault(i => i.Extension == ".luac" &&
                string.Equals(Path.GetFileNameWithoutExtension(i.Path), Path.GetFileNameWithoutExtension(doc.Title), StringComparison.OrdinalIgnoreCase))?.Path
                ?? Combine(SelectedFolder, Path.ChangeExtension(doc.Title, ".luac"));
            entry = Dialogs.Input(this, "Compile into archive", "Archive entry to replace or create with the compiled script:", guess);
            if (string.IsNullOrWhiteSpace(entry)) return false;
            entry = GameArchive.Normalize(entry);
        }
        if (entry == null) return false;

        var existing = _archive.Find(entry);
        string? chunk = doc.Kind == DocumentKind.Script ? doc.ChunkName : existing != null ? ChunkNameOf(existing) : null;
        bool strip = doc.Kind == DocumentKind.Script
            ? doc.OriginalBytecode != null && IsStripped(LuaBytecode.Parse(doc.OriginalBytecode).Main)
            : existing != null ? IsStrippedItem(existing) : _archive.Platform != GamePlatform.PC;
        var target = _archive.Platform.LuaFormat();
        string source = doc.Editor.Text;
        string fileName = Path.GetFileNameWithoutExtension(entry) + ".lua";
        SetStatus($"Compiling {entry}...");
        var result = await Task.Run(() => _tools.Compile(source, fileName, target, chunk ?? DefaultChunkName(entry), strip));
        SetStatus("Ready");
        if (!result.Success)
        {
            doc.ShowMessage(result.Error ?? "Compile failed.", true, result.Line ?? -1);
            if (result.Line is int line) doc.Editor.SetErrorLine(line);
            Log($"{entry}: {result.Error}", LogKind.Error);
            return false;
        }

        var applied = _archive.SetData(entry, result.Bytecode!);
        doc.EntryPath = entry;
        doc.Dirty = false;
        if (doc.Tab != null) doc.Tab.ToolTip = entry;
        string note = "";
        if (doc.OriginalBytecode != null && LuaBytecode.SameCode(LuaBytecode.Parse(doc.OriginalBytecode).Main, LuaBytecode.Parse(result.Bytecode!).Main, out _))
            note = " (same code as the original)";
        UpdateFileNode(applied);
        Log($"Compiled {entry} -> {Format.Size(result.Bytecode!.Length)} of {target.PlatformName} bytecode{note}. Press Ctrl+S to save the archive.", LogKind.Success);
        return true;
    }

    string? ChunkNameOf(ArchiveItem item)
    {
        var data = SafeRead(item);
        return data != null && LuaBytecode.IsBytecode(data) ? LuaBytecode.ReadChunkName(data) ?? "" : null;
    }

    bool IsStrippedItem(ArchiveItem item)
    {
        var data = SafeRead(item);
        try { return data != null && LuaBytecode.IsBytecode(data) && IsStripped(LuaBytecode.Parse(data).Main); }
        catch (LuaBytecodeException) { return false; }
    }

    static string DefaultChunkName(string entryPath) => "@" + Path.ChangeExtension(entryPath, ".lua");

    async Task<bool> ApplyActive()
    {
        if (_busy || ActiveDoc is not { } doc) return false;
        if (!doc.Editable || doc.Editor.ReadOnly)
        {
            SetStatus("This tab is read-only.");
            return false;
        }
        return await ApplyDocument(doc);
    }

    async Task CheckActive()
    {
        if (_busy || ActiveDoc is not { } doc) return;
        await CheckDocument(doc);
    }

    async Task CheckDocument(DocumentView doc)
    {
        if (doc.Loading || !(doc.Kind is DocumentKind.Script or DocumentKind.ExternalScript || doc.Editor.IsLua)) return;
        doc.HideMessage();
        string source = doc.Editor.Text, name = Path.GetFileNameWithoutExtension(doc.Title) + ".lua";
        var result = await Task.Run(() => _tools.CheckSyntax(source, name));
        if (result.Success)
        {
            Log($"{doc.Title}: no syntax errors.", LogKind.Success);
            return;
        }
        doc.ShowMessage(result.Error ?? "Syntax error.", true, result.Line ?? -1);
        if (result.Line is int line) doc.Editor.SetErrorLine(line);
        Log($"{doc.Title}: {result.Error}", LogKind.Error);
    }

    void OpenLuaFromDisk()
    {
        using var dlg = new OpenFileDialog { Title = "Open Lua source", Filter = "Lua source (*.lua)|*.lua|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        byte[] data;
        try { data = File.ReadAllBytes(dlg.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("Could not open the file", ex); return; }
        if (LuaBytecode.IsBytecode(data))
        {
            Dialogs.Info(this, "That file is compiled bytecode. Add it to the archive with Archive > Add Files (or Replace), then double-click it to decompile.");
            return;
        }
        var (text, encoding) = TextFormat.Decode(data);
        string file = dlg.FileName;
        var doc = new DocumentView(DocumentKind.ExternalScript, Path.GetFileName(file), null) { ExternalPath = file, Encoding = encoding };
        doc.AddAction("Compile into Archive", Glyphs.Compile, "Compile this file into an archive entry (F5)", async (_, _) => await ApplyDocument(doc), primary: true);
        doc.AddAction("Check", Glyphs.Check, "Check for syntax errors (F7)", async (_, _) => await CheckDocument(doc));
        doc.AddAction("Save", Glyphs.Save, "Save this file on disk", (_, _) => SaveDocumentToFile(doc, file));
        doc.SetInfo($"{file}  -  F5 compiles it into the archive");
        doc.Editor.SetText(text, true);
        AddDocument(doc);
    }

    void SaveDocumentToFile()
    {
        if (ActiveDoc is { } doc) SaveDocumentToFile(doc, null);
    }

    void SaveDocumentToFile(DocumentView doc, string? path)
    {
        if (path == null)
        {
            string name = doc.Kind == DocumentKind.Script ? Path.ChangeExtension(doc.Title, ".lua") : doc.Title;
            using var dlg = new SaveFileDialog { Title = "Save tab to file", FileName = name, Filter = "All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        try
        {
            var encoding = doc.Kind is DocumentKind.Text or DocumentKind.ExternalScript ? doc.Encoding ?? TextEncodingInfo.Utf8Lf : TextEncodingInfo.Utf8Lf;
            File.WriteAllBytes(path, TextFormat.Encode(doc.Editor.Text, encoding));
            if (doc.Kind == DocumentKind.ExternalScript && doc.EntryPath == null && string.Equals(path, doc.ExternalPath, StringComparison.OrdinalIgnoreCase)) doc.Dirty = false;
            Log($"Saved {path}.", LogKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("Could not save the file", ex); }
    }

    async Task ShowListing(string title, byte[] bytecode)
    {
        try
        {
            string listing = await Task.Run(() => _tools.Disassemble(bytecode));
            var format = LuaBytecode.ReadFormat(bytecode);
            OpenViewer(title + " (bytecode)", null, listing, $"Bytecode listing from luac -l -l  -  {format.PlatformName} bytecode, shown in desktop format  -  read-only");
        }
        catch (Exception ex) when (ex is InvalidOperationException or LuaBytecodeException or IOException or TimeoutException) { ShowError("Bytecode listing failed", ex); }
    }

    async Task ShowListingForSelection()
    {
        if (SelectedItem is not { } item || SafeRead(item) is not { } data || !LuaBytecode.IsBytecode(data))
        {
            Dialogs.Info(this, "Select a .luac script in the archive tree first.");
            return;
        }
        await ShowListing(item.Name, data);
    }

    void FixGotosInActive()
    {
        if (ActiveDoc is not { } doc || doc.Loading || doc.Editor.ReadOnly) return;
        string text = doc.Editor.Text;
        if (!LuaGotoFixer.HasGotos(text))
        {
            Log($"{doc.Title}: there are no goto statements or labels to rewrite.");
            return;
        }
        var result = LuaGotoFixer.Fix(text);
        if (result.Problem != null)
        {
            doc.ShowMessage("The goto statements could not be rewritten automatically: " + result.Problem, true);
            Log($"{doc.Title}: goto rewrite failed - {result.Problem}", LogKind.Error);
            return;
        }
        if (result.GotosFixed == 0)
        {
            Log($"{doc.Title}: no goto statements found (unused labels were not changed).");
            return;
        }
        doc.Editor.ReplaceDocument(result.Source);
        doc.HideMessage();
        Log($"{doc.Title}: rewrote {result.GotosFixed} goto jump(s) into Lua 5.1 repeat/break blocks (Ctrl+Z undoes it). Press F7 to check or F5 to compile.", LogKind.Success);
    }

    void GoToLine()
    {
        if (ActiveDoc is not { } doc) return;
        string? text = Dialogs.Input(this, "Go to line", "Line number:", doc.Editor.CaretLine.ToString());
        if (int.TryParse(text, out int line)) doc.Editor.GoToLine(line);
    }

    void EditCommand(Action<TextBoxBase> onTextBox, Action<CodeEditor> onEditor)
    {
        Control? focused = this;
        while (focused is ContainerControl cc && cc.ActiveControl != null) focused = cc.ActiveControl;
        if (focused is TextBoxBase box && box.Parent is not CodeEditor) onTextBox(box);
        else if (ActiveDoc is { } doc) onEditor(doc.Editor);
    }

    void UpdateCaret()
    {
        var doc = ActiveDoc;
        _statusCaret.Text = doc != null && !doc.Loading && doc.Kind != DocumentKind.Viewer ? $"Ln {doc.Editor.CaretLine}, Col {doc.Editor.CaretColumn}" : "";
    }

    // ===================================================================== archive entries

    async Task ExtractSelected()
    {
        if (_archive == null) return;
        if (SelectedItem is { } item) await ExtractFile(item);
        else await ExtractFolder(_tree.SelectedNode?.Tag as string ?? "");
    }

    async Task ExtractFile(ArchiveItem item)
    {
        using var dlg = new SaveFileDialog { Title = "Extract " + item.Path, FileName = item.Name, Filter = "All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            byte[] data = await Task.Run(() => _archive!.ReadData(item));
            await File.WriteAllBytesAsync(dlg.FileName, data);
            Log($"Extracted {item.Path} -> {dlg.FileName}", LogKind.Success);
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException) { ShowError("Extract failed", ex); }
    }

    async Task ExtractFolder(string folder)
    {
        if (_archive == null) return;
        var archive = _archive;
        var items = ItemsUnder(folder).ToList();
        if (items.Count == 0) return;
        using var dlg = new FolderBrowserDialog
        {
            Description = $"Extract {items.Count:N0} files from {(folder.Length == 0 ? archive.DisplayName : folder + "/")} into...",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string dest = Path.GetFullPath(dlg.SelectedPath).TrimEnd(Path.DirectorySeparatorChar);
        int done = 0, skipped = 0;
        bool ok = await RunBusy("Extract", async (progress, ct) => await Task.Run(() =>
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(dest, item.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, archive.ReadData(item));
                done++;
                progress.Report(new SaveProgress("Extracting", done, items.Count, item.Path));
            }
        }, ct), cancellable: true);
        if (ok) Log($"Extracted {done:N0} files to {dest}." + (skipped > 0 ? $" Skipped {skipped} entries with unsafe paths." : ""), LogKind.Success);
    }

    async Task ReplaceSelected()
    {
        if (SelectedItem is { } item) await ReplaceItem(item);
        else Dialogs.Info(this, "Select the file to replace in the archive tree first.");
    }

    async Task ReplaceItem(ArchiveItem item)
    {
        bool script = item.Extension == ".luac";
        using var dlg = new OpenFileDialog
        {
            Title = "Replace " + item.Path,
            Filter = script ? "Lua script or bytecode (*.lua;*.luac)|*.lua;*.luac|All files (*.*)|*.*" : "All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (await ImportFile(dlg.FileName, item.Path)) UpdateFileNode(item);
    }

    async Task<bool> ImportFile(string file, string entryPath)
    {
        if (_archive == null) return false;
        try
        {
            byte[] data = await File.ReadAllBytesAsync(file);
            var existing = _archive.Find(entryPath);
            var (bytes, note) = await Task.Run(() => PrepareImport(entryPath, data, Path.GetFileName(file), existing));
            var item = _archive.SetData(entryPath, bytes);
            Log($"{(existing != null ? "Replaced" : "Added")} {item.Path} ({Format.Size(bytes.Length)}){note}. Press Ctrl+S to save the archive.", LogKind.Success);
            if (FindDocument(item.Path) is { } doc) await ReloadDocument(doc, ask: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or LuaBytecodeException or ArchiveException)
        {
            Log($"Could not import {Path.GetFileName(file)}: {ex.Message}", LogKind.Error);
            Dialogs.Error(this, ex.Message, "Import failed");
            return false;
        }
    }

    /// <summary>Converts bytecode from the other platform and compiles .lua source destined for .luac entries.</summary>
    (byte[] Data, string Note) PrepareImport(string entryPath, byte[] data, string sourceName, ArchiveItem? existing)
    {
        var target = _archive!.Platform.LuaFormat();
        if (LuaBytecode.IsBytecode(data))
        {
            var format = LuaBytecode.ReadFormat(data);
            if (format == target) return (data, "");
            return (LuaBytecode.Convert(data, target), $" - converted from {format.PlatformName} to {target.PlatformName} bytecode");
        }
        if (!entryPath.EndsWith(".luac", StringComparison.OrdinalIgnoreCase)) return (data, "");
        var (text, _) = TextFormat.Decode(data);
        string? chunk = existing != null ? ChunkNameOf(existing) : null;
        bool strip = existing != null ? IsStrippedItem(existing) : _archive.Platform != GamePlatform.PC;
        var result = _tools.Compile(text, Path.GetFileNameWithoutExtension(entryPath) + ".lua", target, chunk ?? DefaultChunkName(entryPath), strip);
        if (!result.Success) throw new InvalidOperationException($"{sourceName} does not compile: {result.Error}");
        return (result.Bytecode!, $" - compiled from {sourceName} for {target.PlatformName}");
    }

    async Task AddFilesDialog()
    {
        if (_archive == null) return;
        string folder = SelectedFolder;
        using var dlg = new OpenFileDialog { Title = $"Add files to {(folder.Length == 0 ? "the archive root" : folder + "/")}", Multiselect = true, Filter = "All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await AddPaths(dlg.FileNames, folder);
    }

    async Task AddPaths(IEnumerable<string> paths, string folder)
    {
        if (_archive == null) return;
        var pairs = new List<(string File, string Entry)>();
        foreach (string p in paths)
        {
            if (Directory.Exists(p))
            {
                string baseName = Path.GetFileName(p.TrimEnd('\\', '/'));
                foreach (string f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    pairs.Add((f, Combine(folder, baseName + "/" + Path.GetRelativePath(p, f).Replace('\\', '/'))));
            }
            else if (File.Exists(p)) pairs.Add((p, Combine(folder, Path.GetFileName(p))));
        }
        if (pairs.Count == 0) return;

        int sources = pairs.Count(x => x.File.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
        if (sources > 0)
        {
            var answer = Dialogs.Ask(this, $"Compile {sources} .lua file(s) into .luac game scripts for {_archive.Platform.DisplayName()}?\n\n" +
                "Yes = add compiled .luac scripts (what the game loads)\nNo = add the .lua files unchanged", "Add Lua files", MessageBoxButtons.YesNoCancel);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes)
                pairs = pairs.Select(x => x.File.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? (x.File, Path.ChangeExtension(x.Entry, ".luac")) : x).ToList();
        }

        var replacing = pairs.Where(x => _archive.Find(x.Entry) != null).Select(x => x.Entry).ToList();
        if (replacing.Count > 0 && Dialogs.Ask(this, $"{replacing.Count} file(s) already exist and will be replaced:\n\n{string.Join("\n", replacing.Take(10))}{(replacing.Count > 10 ? "\n..." : "")}\n\nContinue?",
                "Replace files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        int added = 0;
        foreach (var (file, entry) in pairs)
            if (await ImportFile(file, entry)) added++;
        if (added == 0) return;
        PopulateTree();
        RefreshGrid();
        SelectNode(GameArchive.Normalize(pairs[^1].Entry));
        UpdateUiState();
    }

    void DeleteSelected()
    {
        if (_archive == null || _busy || _tree.SelectedNode == null) return;
        List<ArchiveItem> items;
        string what;
        if (SelectedItem is { } item)
        {
            items = [item];
            what = item.Path;
        }
        else
        {
            string folder = _tree.SelectedNode.Tag as string ?? "";
            if (folder.Length == 0) return;
            items = ItemsUnder(folder).ToList();
            what = $"{items.Count:N0} files in {folder}/";
        }
        if (items.Count == 0) return;
        if (Dialogs.Ask(this, $"Remove {what} from the archive?\n\nNothing changes on disk until you save the archive.", "Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var it in items)
        {
            if (FindDocument(it.Path) is { } doc) RemoveDocument(doc);
            _archive.Delete(it);
        }
        Log($"Removed {what}. Press Ctrl+S to save the archive.", LogKind.Success);
        PopulateTree();
        RefreshGrid();
        UpdateUiState();
    }

    async Task RevertSelected()
    {
        if (_archive == null) return;
        var candidates = SelectedItem is { } selected ? [selected] : ItemsUnder(_tree.SelectedNode?.Tag as string ?? "").ToList();
        var items = candidates.Where(i => i.State != ItemState.Original).ToList();
        if (items.Count == 0)
        {
            Log("Nothing to revert - the selection has no unsaved changes." + (_archive.HasChanges ? " (Deleted files come back with File > Discard All Changes.)" : ""));
            return;
        }
        if (Dialogs.Ask(this, $"Revert {(items.Count == 1 ? items[0].Path : $"{items.Count} files")} to the archive's saved version?", "Revert") != DialogResult.Yes) return;
        foreach (var it in items)
        {
            _archive.Revert(it);
            if (FindDocument(it.Path) is { } doc)
            {
                if (_archive.Find(it.Path) == null) RemoveDocument(doc);
                else await ReloadDocument(doc, ask: false);
            }
        }
        Log($"Reverted {items.Count} file(s).", LogKind.Success);
        PopulateTree();
        RefreshGrid();
        UpdateUiState();
    }

    void CopySelectedPath()
    {
        string? path = SelectedItem?.Path ?? _tree.SelectedNode?.Tag as string;
        if (!string.IsNullOrEmpty(path)) Clipboard.SetText(path);
    }

    void ExportScript(ArchiveItem item, GamePlatform platform)
    {
        var data = SafeRead(item);
        if (data == null || !LuaBytecode.IsBytecode(data)) { Dialogs.Info(this, "Select a .luac script first."); return; }
        using var dlg = new SaveFileDialog { Title = $"Export {item.Name} for {platform.DisplayName()}", FileName = item.Name, Filter = "Lua bytecode (*.luac)|*.luac|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, LuaBytecode.Convert(data, platform.LuaFormat()));
            Log($"Exported {item.Path} as {platform.DisplayName()} bytecode -> {dlg.FileName}", LogKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LuaBytecodeException) { ShowError("Export failed", ex); }
    }

    async Task ExportSource(ArchiveItem item)
    {
        string? source = FindDocument(item.Path) is { Kind: DocumentKind.Script, Loading: false } doc && !doc.Editor.ReadOnly ? doc.Editor.Text : null;
        if (source == null)
        {
            var data = SafeRead(item);
            if (data == null || !LuaBytecode.IsBytecode(data)) { Dialogs.Info(this, "Select a .luac script first."); return; }
            try { source = await Task.Run(() => _tools.Decompile(data)); }
            catch (Exception ex) when (ex is InvalidOperationException or LuaBytecodeException or IOException or TimeoutException) { ShowError("Decompile failed", ex); return; }
        }
        using var dlg = new SaveFileDialog { Title = "Export decompiled source", FileName = Path.ChangeExtension(item.Name, ".lua"), Filter = "Lua source (*.lua)|*.lua|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, TextFormat.Encode(source, TextEncodingInfo.Utf8Lf));
            Log($"Exported decompiled {item.Path} -> {dlg.FileName}", LogKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("Export failed", ex); }
    }

    // ===================================================================== drag and drop

    void OnDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && !_busy ? DragDropEffects.Copy : DragDropEffects.None;

    static bool IsArchiveFile(string path) =>
        File.Exists(path) && (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));

    async void OnFormDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0 || _busy) return;
        if (files.Length == 1 && IsArchiveFile(files[0]) && (_archive == null ||
            Dialogs.Ask(this, $"Open {Path.GetFileName(files[0])} as the archive?\n\n(Drop files onto a folder in the tree to add them instead.)", "Open archive") == DialogResult.Yes))
        {
            await OpenArchive(files[0]);
            return;
        }
        if (_archive != null) await AddPaths(files, "");
    }

    void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (_archive == null || _busy || e.Data?.GetDataPresent(DataFormats.FileDrop) != true)
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        e.Effect = DragDropEffects.Copy;
        var node = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
        if (node != null && node != _dropNode)
        {
            _dropNode = node;
            _tree.SelectedNode = node;
        }
    }

    async void OnTreeDragDrop(object? sender, DragEventArgs e)
    {
        if (_archive == null || e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var node = _dropNode ?? _tree.SelectedNode;
        _dropNode = null;
        if (node?.Tag is ArchiveItem target && files.Length == 1 && File.Exists(files[0]))
        {
            if (Dialogs.Ask(this, $"Replace {target.Path} with {Path.GetFileName(files[0])}?", "Replace file") == DialogResult.Yes && await ImportFile(files[0], target.Path))
                UpdateFileNode(target);
            return;
        }
        string folder = node?.Tag switch { ArchiveItem item => ParentOf(item.Path), string f => f, _ => "" };
        await AddPaths(files, folder);
    }

    // ===================================================================== VFS index

    async Task RebuildIndex()
    {
        if (_archive?.FilePath == null) { Dialogs.Info(this, "Save the archive first."); return; }
        if (_archive.HasChanges) { Dialogs.Info(this, "The archive has unsaved changes - save it instead. Saving always rebuilds the index."); return; }
        var archive = _archive;
        var (bin, dat) = VfsIndex.CompanionPaths(archive.FilePath);
        if (Dialogs.Ask(this, $"Rewrite {Path.GetFileName(bin)} and {Path.GetFileName(dat)} from the files currently in {archive.DisplayName} ({archive.Platform.DisplayName()} format)?\n\n" +
                "Use this after changing the archive with another tool. Existing files are backed up as .bak the first time.", "Rebuild index") != DialogResult.Yes) return;
        IndexBuild? build = null;
        var backups = new List<string>();
        if (!await RunBusy("Rebuild index", async (_, _) => build = await Task.Run(() => archive.RebuildIndex(backups)), cancellable: false) || build == null) return;
        Log($"Rebuilt {Path.GetFileName(bin)} + {Path.GetFileName(dat)}: {build.FileCount:N0} files, {build.NodeCount:N0} nodes, {build.NameTableSize:N0}-byte name table, {(archive.Platform.IndexIsBigEndian() ? "big" : "little")}-endian.", LogKind.Success);
        if (backups.Count > 0) Log("Backed up: " + string.Join(", ", backups.Select(Path.GetFileName)));
    }

    async Task VerifyIndex()
    {
        if (_archive?.FilePath == null) { Dialogs.Info(this, "Save the archive first."); return; }
        var archive = _archive;
        List<string>? report = null;
        if (!await RunBusy("Verify index", async (_, _) => report = await Task.Run(() => archive.VerifyIndexOnDisk()), cancellable: false) || report == null) return;
        for (int i = 0; i < report.Count; i++)
        {
            var kind = i > 0 ? LogKind.Detail : report[0].StartsWith("OK") ? LogKind.Success : report[0].StartsWith("MISMATCH") ? LogKind.Error : LogKind.Warning;
            Log(report[i], kind);
        }
        if (archive.HasChanges) Log("This checks the files on disk; your unsaved changes are not included until you save.");
    }

    void ShowIndexStats()
    {
        if (_archive == null) return;
        var files = _archive.Items.Where(i => !i.IsDirectory).ToList();
        try
        {
            var build = VfsIndex.Build(files.Select(i => new WrittenEntry { Path = i.Path }).ToList(), _archive.Platform.IndexIsBigEndian());
            Log($"Index statistics for {_archive.DisplayName} ({_archive.Platform.DisplayName()}, including unsaved changes):");
            Log($"  files {build.FileCount:N0} of max {VfsIndex.MaxFiles - 1:N0}  -  folders {build.DirectoryCount:N0}  -  tree nodes {build.NodeCount + 1:N0} of max {VfsIndex.MaxNodes:N0}", LogKind.Detail);
            Log($"  name table {build.NameTableSize:N0} of max 65,536 bytes  -  scripts {files.Count(i => i.Extension == ".luac"):N0}  -  {Format.Size(files.Sum(i => i.Size))} uncompressed", LogKind.Detail);
            Log($"  {Path.GetFileNameWithoutExtension(_archive.FilePath ?? "vfs")}.bin will be {build.Bin.Length:N0} bytes, {(_archive.Platform.IndexIsBigEndian() ? "big" : "little")}-endian", LogKind.Detail);
        }
        catch (ArchiveException ex) { Log("The index cannot be built: " + ex.Message, LogKind.Error); }
    }

    async Task RestoreBackups()
    {
        if (_archive?.FilePath == null) return;
        string path = _archive.FilePath;
        var (bin, dat) = VfsIndex.CompanionPaths(path);
        var pairs = new[] { path, bin, dat }.Select(p => (Original: p, Backup: p + ".bak")).Where(x => File.Exists(x.Backup)).ToList();
        if (pairs.Count == 0) { Dialogs.Info(this, "There are no .bak backups next to this archive."); return; }
        if (Dialogs.Ask(this, $"Restore these backups? The current files are overwritten (the .bak files are kept):\n\n{string.Join("\n", pairs.Select(x => Path.GetFileName(x.Backup)))}" +
                (HasUnsavedWork() ? "\n\nYour unsaved changes will be lost." : ""), "Restore backups", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        CloseAllDocuments();
        _archive.Dispose();
        _archive = null;
        PopulateTree();
        bool ok = await RunBusy("Restore backups", async (progress, _) => await Task.Run(() =>
        {
            int n = 0;
            foreach (var (original, backup) in pairs)
            {
                progress.Report(new SaveProgress("Restoring", n++, pairs.Count, Path.GetFileName(original)));
                File.Copy(backup, original, overwrite: true);
            }
        }), cancellable: false);
        if (ok) Log($"Restored {pairs.Count} file(s) from .bak backups.", LogKind.Success);
        await OpenArchive(path);
    }

    // ===================================================================== status, log, welcome

    void UpdateUiState()
    {
        bool hasArchive = _archive != null;
        foreach (var item in _needsArchive) item.Enabled = hasArchive && !_busy;
        var doc = ActiveDoc;
        foreach (var item in _needsDocument) item.Enabled = doc != null && !_busy;

        var platform = _archive?.Platform ?? _newArchivePlatform;
        _statusPlatform.Text = $"{platform.DisplayName()}  -  {(platform.IndexIsBigEndian() ? "big" : "little")}-endian";
        _statusFiles.Text = hasArchive ? $"{_archive!.FileCount:N0} files" : "";
        int pending = _archive?.PendingChanges ?? 0, edited = _docs.Count(d => d.Dirty);
        var parts = new List<string>();
        if (pending > 0) parts.Add($"{pending} unsaved change{(pending == 1 ? "" : "s")}");
        if (edited > 0) parts.Add($"{edited} edited tab{(edited == 1 ? "" : "s")} not applied (F5)");
        _statusPending.Text = !hasArchive ? "" : parts.Count == 0 ? "No unsaved changes" : "● " + string.Join(", ", parts);
        _statusBar.BackColor = _busy ? Color.FromArgb(202, 81, 0) : pending + edited > 0 ? Color.FromArgb(104, 33, 122) : C.ACCENT;

        Text = _archive == null ? AppName : $"{(HasUnsavedWork() ? "● " : "")}{_archive.DisplayName} [{_archive.Platform.DisplayName()}] - {AppName}";
        UpdateCaret();
    }

    void SetStatus(string text)
    {
        if (!_busy) _statusText.Text = text;
    }

    void Log(string message, LogKind kind = LogKind.Info)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message, kind));
            return;
        }
        Color color = kind switch
        {
            LogKind.Success => C.GREEN,
            LogKind.Warning => C.YELLOW,
            LogKind.Error => C.RED,
            LogKind.Detail => C.FG_DIM,
            _ => C.FG,
        };
        if (_output.TextLength > 400_000)
        {
            _output.Select(0, _output.TextLength / 2);
            _output.SelectedText = "";
        }
        _output.Select(_output.TextLength, 0);
        _output.SelectionColor = C.FG_FAINT;
        _output.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  ");
        _output.SelectionColor = color;
        _output.AppendText(message + "\n");
        _output.ScrollToCaret();
        if (kind is LogKind.Error or LogKind.Warning && _bottomTabs.Tabs.Count > 0) _bottomTabs.Selected = _bottomTabs.Tabs[0];
        if (kind != LogKind.Detail) SetStatus(FirstLine(message));
    }

    void LogToolchain(bool onlyProblems = false)
    {
        if (!onlyProblems)
            foreach (string line in _tools.Describe().Split('\n')) Log(line, LogKind.Detail);
        if (!_tools.CanCompile) Log("luac5.1.exe is missing from the tools folder - scripts cannot be compiled.", LogKind.Error);
        if (_tools.UnluacPath == null) Log("unluac.jar is missing from the tools folder - scripts cannot be decompiled.", LogKind.Error);
        else if (_tools.JavaPath == null) Log("Java was not found - install Java 8 or newer (for example Eclipse Temurin) to decompile scripts.", LogKind.Warning);
        else if (onlyProblems) Log($"Lua toolchain ready (Java: {_tools.JavaPath}).", LogKind.Detail);
    }

    void ShowError(string title, Exception ex)
    {
        Log($"{title}: {ex.Message}", LogKind.Error);
        Dialogs.Error(this, ex.Message, title);
    }

    static string FirstLine(string text)
    {
        int nl = text.IndexOf('\n');
        return (nl < 0 ? text : text[..nl]).Trim();
    }

    void BuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        _recentMenu.DropDown.BackColor = C.BG2;
        foreach (string path in _settings.RecentArchives.Where(File.Exists))
        {
            string p = path;
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem(p, null, async (_, _) => await OpenArchive(p)) { ForeColor = C.FG });
        }
        if (_recentMenu.DropDownItems.Count == 0) _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(none yet)") { Enabled = false });
    }

    void RefreshWelcome()
    {
        if (_welcomeContent == null) return;
        _welcomeContent.SuspendLayout();
        foreach (Control c in _welcomeContent.Controls.Cast<Control>().ToList()) c.Dispose();
        _welcomeContent.Controls.Clear();

        Label Line(string text, float size, Color color, FontStyle style = FontStyle.Regular, int top = 0, string family = "Segoe UI") =>
            new() { Text = text, AutoSize = true, MaximumSize = new Size(this.Px(680), 0), ForeColor = color, Font = new Font(family, size, style), Margin = new Padding(0, this.Px(top), 0, 0), UseMnemonic = false };

        Button BigButton(string text, string glyph, EventHandler click, bool primary)
        {
            var b = new Button
            {
                Text = "  " + text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = primary ? C.ACCENT : C.BG4, ForeColor = C.FG,
                Font = Theme.Ui(10f), Padding = new Padding(this.Px(10), this.Px(6), this.Px(14), this.Px(6)), Margin = new Padding(0, 0, this.Px(10), 0), Cursor = Cursors.Hand,
                Image = Theme.Glyph(glyph, C.FG, this.Px(18)), TextImageRelation = TextImageRelation.ImageBeforeText, UseMnemonic = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = primary ? C.ACCENT2 : Color.FromArgb(80, 80, 86);
            b.Click += click;
            return b;
        }

        _welcomeContent.Controls.Add(Line(AppName, 22f, C.FG, FontStyle.Regular, 0, "Segoe UI Light"));
        if (_archive == null)
        {
            _welcomeContent.Controls.Add(Line("Decompile, edit and recompile the Lua scripts inside Fast & Furious: Showdown archives - PC (vfs.zip), Xbox 360 (vfs.pak) and 3DS.", 10.5f, C.FG_DIM, top: 4));
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = C.BG, Margin = new Padding(0, this.Px(22), 0, 0) };
            buttons.Controls.Add(BigButton("Open Archive...", Glyphs.Open, (_, _) => OpenArchiveDialog(), true));
            buttons.Controls.Add(BigButton("New Archive from Folder...", Glyphs.NewFolder, async (_, _) => await NewFromFolder(), false));
            _welcomeContent.Controls.Add(buttons);
            var recent = _settings.RecentArchives.Where(File.Exists).Take(6).ToList();
            if (recent.Count > 0)
            {
                _welcomeContent.Controls.Add(Line("RECENT", 8.5f, C.FG_FAINT, FontStyle.Bold, 26));
                foreach (string path in recent)
                {
                    var link = new LinkLabel
                    {
                        Text = path, AutoSize = true, LinkColor = C.BLUE, ActiveLinkColor = C.FG, Font = Theme.Ui(10f),
                        Margin = new Padding(0, this.Px(6), 0, 0), LinkBehavior = LinkBehavior.HoverUnderline, UseMnemonic = false,
                    };
                    link.LinkClicked += async (_, _) => await OpenArchive(path);
                    _welcomeContent.Controls.Add(link);
                }
            }
            _welcomeContent.Controls.Add(Line("HOW IT WORKS", 8.5f, C.FG_FAINT, FontStyle.Bold, 26));
            _welcomeContent.Controls.Add(Line(
                "1.  Open vfs.zip (PC) or vfs.pak (Xbox 360). The platform is detected from vfs.dat.\n" +
                "2.  Double-click a .luac script to decompile it into editable Lua.\n" +
                "3.  Edit it, then press F5 to compile it back into the archive in the game's format.\n" +
                "4.  Press Ctrl+S to save. The archive, vfs.bin and vfs.dat are rewritten together and verified;\n" +
                "     the originals are kept as .bak files the first time.", 10f, C.FG_DIM, top: 6));
        }
        else
        {
            int scripts = _archive.Items.Count(i => i.Extension == ".luac");
            _welcomeContent.Controls.Add(Line($"{_archive.DisplayName}  -  {_archive.Platform.DisplayName()}  -  {_archive.FileCount:N0} files, {scripts} Lua scripts", 11f, C.FG_DIM, top: 4));
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = C.BG, Margin = new Padding(0, this.Px(22), 0, 0) };
            buttons.Controls.Add(BigButton("Show All Scripts", Glyphs.Code, (_, _) => { _filter.Text = ".luac"; _filter.Focus(); }, true));
            buttons.Controls.Add(BigButton("Open .lua from Disk...", Glyphs.Document, (_, _) => OpenLuaFromDisk(), false));
            _welcomeContent.Controls.Add(buttons);
            _welcomeContent.Controls.Add(Line("TIPS", 8.5f, C.FG_FAINT, FontStyle.Bold, 26));
            _welcomeContent.Controls.Add(Line(
                "Double-click a .luac script in the tree to decompile and edit it.\n" +
                $"F5 compiles the open script into the archive as {_archive.Platform.DisplayName()} bytecode; F7 only checks syntax.\n" +
                "Ctrl+S saves the archive and rebuilds its index. Orange files have unsaved changes, green ones were added.\n" +
                "Drop files from Explorer onto a folder in the tree to add them, or onto a file to replace it.\n" +
                "Ctrl+P filters the tree, Ctrl+F / Ctrl+H find and replace, Ctrl+G goes to a line, Ctrl+/ toggles comments.", 10f, C.FG_DIM, top: 6));
        }
        _welcomeContent.ResumeLayout();
        CenterWelcome();
    }

    void CenterWelcome()
    {
        var size = _welcomeContent.PreferredSize;
        _welcomeContent.Location = new Point(Math.Max(this.Px(24), (_welcome.ClientSize.Width - size.Width) / 2), Math.Max(this.Px(24), (_welcome.ClientSize.Height - size.Height) / 3));
    }

    void ShowGuide() => Dialogs.Info(this,
        "Editing a script\n" +
        "  1. Open vfs.zip (PC) or vfs.pak (Xbox 360). vfs.bin and vfs.dat must sit next to it.\n" +
        "  2. Double-click a .luac file - it is decompiled with unluac into Lua source.\n" +
        "  3. Edit, then press F5 (Compile & Apply). Errors are shown with the line number.\n" +
        "  4. Press Ctrl+S. The archive is rewritten, vfs.bin / vfs.dat are rebuilt for it,\n" +
        "     and everything is verified before the old files are replaced (kept as .bak).\n\n" +
        "Other files\n" +
        "  Text files open as editable text (F5 applies). Binary files can be extracted and replaced.\n" +
        "  Replacing a .luac with a .lua file compiles it; bytecode from the other platform is converted.\n\n" +
        "Platforms\n" +
        "  PC and 3DS scripts are little-endian, Xbox 360 scripts big-endian; all use 32-bit floats.\n" +
        "  Xbox 360 scripts were shipped without debug info, so decompiled local names are generated.\n" +
        "  When unluac can only express a jump as 'goto' (Lua 5.1 has none), the editor rewrites it\n" +
        "  automatically into repeat/break blocks marked --[[goto ...]] - review those spots.\n\n" +
        "Keys: F5 compile/apply, F7 check, Ctrl+S save, Ctrl+W close tab, Ctrl+P filter files,\n" +
        "Ctrl+F find, Ctrl+H replace, Ctrl+G go to line, Ctrl+/ toggle comment, Tab/Shift+Tab indent.",
        "Quick guide");

    void ShowAbout() => Dialogs.Info(this,
        $"{AppName}\n\nEdits Fast & Furious: Showdown archives for PC, Xbox 360 and 3DS.\n\n{_tools.Describe()}\n\n" +
        "Bundled tools: Lua 5.1.5 compiler and unluac 1.2.3 (MIT licensed, see tools\\THIRD-PARTY-NOTICES.txt).",
        "About");
}
