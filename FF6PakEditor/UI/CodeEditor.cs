using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FF6PakEditor.UI;

/// <summary>RichTextBox-based code editor with line numbers, Lua highlighting and find/replace.</summary>
[System.ComponentModel.DesignerCategory("Code")]
sealed class CodeEditor : UserControl
{
    const string FontName = "Consolas";
    const float FontSize = 10.5f;
    const int MaxUndo = 2000;
    // Per-token recoloring through TOM costs a few ms per token in big documents, so larger spans are
    // re-streamed as RTF (same text, new colors) in chunks of about this many characters per timer tick.
    const int TomRunLimit = 8, ImmediateChars = 40_000, CharsPerPass = 20_000, FullReloadChars = 150_000;

    static readonly Color[] Palette = [C.FG, C.KEYWORD, C.BUILTIN, C.NUMBER, C.STRING, C.COMMENT];

    readonly EditorBox _box;
    readonly LineGutter _gutter;
    readonly FindBar _find;
    readonly System.Windows.Forms.Timer _highlightTimer = new() { Interval = 120 };
    readonly System.Windows.Forms.Timer _passTimer = new() { Interval = 15 };
    ITextDocument? _tom;
    bool _lua = true, _loading, _replaying, _trailingNewline;
    string _lastText = "";
    List<Run> _lastRuns = new();
    int _passChar = -1;

    // RichEdit's own undo would also record every recoloring, so it is switched off and text edits are tracked here.
    sealed record Edit(int Start, string Removed, string Inserted, long Ticks);
    readonly List<Edit> _undo = new(), _redo = new();
    string _shadow = "";

    public event EventHandler? TextEdited;
    public event EventHandler? CaretMoved;

    public CodeEditor()
    {
        BackColor = C.BG;
        _box = new EditorBox(this);
        _gutter = new LineGutter(_box, FontName, FontSize) { Dock = DockStyle.Left };
        _find = new FindBar(this) { Dock = DockStyle.Top, Visible = false };
        Controls.Add(_box);
        Controls.Add(_gutter);
        Controls.Add(_find);
        _box.ContextMenuStrip = BuildContextMenu();

        _box.TextChanged += (_, _) =>
        {
            _gutter.Invalidate();
            if (_loading || !RecordChange()) return;
            _highlightTimer.Stop();
            _highlightTimer.Start();
            TextEdited?.Invoke(this, EventArgs.Empty);
        };
        _box.SelectionChanged += (_, _) => { _gutter.Invalidate(); CaretMoved?.Invoke(this, EventArgs.Empty); };
        _box.VScroll += (_, _) => _gutter.Invalidate();
        _box.Resize += (_, _) => _gutter.Invalidate();
        _box.HandleCreated += (_, _) => AttachTom();
        _box.HandleDestroyed += (_, _) => ReleaseTom();
        _highlightTimer.Tick += (_, _) => UpdateHighlight();
        _passTimer.Tick += (_, _) => ContinuePass();
    }

    public bool ReadOnly
    {
        get => _box.ReadOnly;
        set { _box.ReadOnly = value; _box.BackColor = C.BG; }
    }

    /// <summary>The document text. RichEdit drops a final line break, so it is added back when the loaded text had one.</summary>
    public override string Text
    {
        get
        {
            string text = _box.Text;
            return _trailingNewline && !text.EndsWith('\n') ? text + "\n" : text;
        }
#pragma warning disable CS8765
        set => SetText(value ?? "", _lua);
#pragma warning restore CS8765
    }

    public bool IsLua => _lua;
    public int CaretLine => _box.GetLineFromCharIndex(_box.SelectionStart) + 1;
    public int CaretColumn => _box.SelectionStart - _box.GetFirstCharIndexOfCurrentLine() + 1;
    public bool FindVisible => _find.Visible;
    public bool CanUndo => _undo.Count > 0 && !ReadOnly;
    public bool CanRedo => _redo.Count > 0 && !ReadOnly;

    public void SetText(string text, bool lua)
    {
        _highlightTimer.Stop();
        _passTimer.Stop();
        _passChar = -1;
        _loading = true;
        try
        {
            _lua = lua;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            _trailingNewline = text.EndsWith('\n');
            var runs = LuaLexer.Lex(text, lua);
            _box.Rtf = LuaLexer.BuildRtf(text, runs, Palette, FontName, FontSize);
            _lastText = _box.Text;
            _lastRuns = _lastText == text ? runs : LuaLexer.Lex(_lastText, lua);
            _box.Font = new Font(FontName, FontSize);
            _box.Select(0, 0);
            _shadow = _lastText;
            _undo.Clear();
            _redo.Clear();
        }
        finally { _loading = false; }
        _gutter.ErrorLine = -1;
        _gutter.Invalidate();
    }

    public void FocusEditor() => _box.Focus();
    public void Cut() { if (!ReadOnly) _box.Cut(); else _box.Copy(); }
    public void Copy() => _box.Copy();
    public void Paste() => _box.PastePlain();
    public void SelectAll() => _box.SelectAll();

    /// <summary>Replaces the selection without going through TextBoxBase.SelectedText (which resets the text limit).</summary>
    internal void ReplaceSelection(string text) => Native.SendMessage(_box.Handle, Native.EM_REPLACESEL, IntPtr.Zero, text);

    /// <summary>
    /// Replaces the document as one undoable edit. Only the span that actually differs is replaced,
    /// so the rest keeps its highlighting (a full replace would give everything one color).
    /// </summary>
    public void ReplaceDocument(string text)
    {
        text = text.Replace("\r\n", "\n");
        string old = _box.Text;
        int prefix = 0, max = Math.Min(old.Length, text.Length);
        while (prefix < max && old[prefix] == text[prefix]) prefix++;
        int suffix = 0, maxSuffix = max - prefix;
        while (suffix < maxSuffix && old[old.Length - 1 - suffix] == text[text.Length - 1 - suffix]) suffix++;
        if (prefix == old.Length && prefix == text.Length) return;

        var scroll = Native.GetScrollPos(_box.Handle);
        int caret = _box.SelectionStart;
        _box.Select(prefix, old.Length - prefix - suffix);
        ReplaceSelection(text.Substring(prefix, text.Length - prefix - suffix));
        _box.Select(Math.Min(caret, _box.TextLength), 0);
        Native.SetScrollPos(_box.Handle, scroll);
    }

    // ---------------------------------------------------------------- undo / redo

    /// <summary>Diffs the text against the previous version and records the change. False if nothing changed.</summary>
    bool RecordChange()
    {
        string text = _box.Text;
        string old = _shadow;
        if (text.Length == old.Length && string.Equals(text, old, StringComparison.Ordinal)) return false;
        int prefix = 0, maxPrefix = Math.Min(text.Length, old.Length);
        while (prefix < maxPrefix && text[prefix] == old[prefix]) prefix++;
        int suffix = 0, maxSuffix = maxPrefix - prefix;
        while (suffix < maxSuffix && text[text.Length - 1 - suffix] == old[old.Length - 1 - suffix]) suffix++;
        string removed = old.Substring(prefix, old.Length - prefix - suffix);
        string inserted = text.Substring(prefix, text.Length - prefix - suffix);
        _shadow = text;
        if (_replaying) return true;

        _redo.Clear();
        long now = Environment.TickCount64;
        var last = _undo.Count > 0 ? _undo[^1] : null;
        bool recent = last != null && now - last.Ticks < 1500;
        if (recent && removed.Length == 0 && last!.Removed.Length == 0 && prefix == last.Start + last.Inserted.Length &&
            inserted.Length <= 2 && !inserted.Contains('\n'))
            _undo[^1] = last with { Inserted = last.Inserted + inserted, Ticks = now };
        else if (recent && inserted.Length == 0 && last!.Inserted.Length == 0 && removed.Length <= 2 && prefix + removed.Length == last.Start)
            _undo[^1] = last with { Start = prefix, Removed = removed + last.Removed, Ticks = now };
        else if (recent && inserted.Length == 0 && last!.Inserted.Length == 0 && removed.Length <= 2 && prefix == last.Start)
            _undo[^1] = last with { Removed = last.Removed + removed, Ticks = now };
        else
        {
            _undo.Add(new Edit(prefix, removed, inserted, now));
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }
        return true;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Replay(e.Start, e.Inserted.Length, e.Removed);
        _redo.Add(e);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Replay(e.Start, e.Removed.Length, e.Inserted);
        _undo.Add(e with { Ticks = 0 });
    }

    void Replay(int start, int length, string text)
    {
        _replaying = true;
        try
        {
            _box.Select(start, length);
            ReplaceSelection(text);
            _box.Select(start + text.Length, 0);
            _box.ScrollToCaret();
        }
        finally { _replaying = false; }
    }

    ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = C.BG2, Font = Theme.Ui(9f) };
        ToolStripMenuItem Item(string text, string keys, Action action) =>
            new(text, null, (_, _) => action()) { ForeColor = C.FG, ShortcutKeyDisplayString = keys };
        var undo = Item("Undo", "Ctrl+Z", Undo);
        var redo = Item("Redo", "Ctrl+Y", Redo);
        var cut = Item("Cut", "Ctrl+X", Cut);
        var paste = Item("Paste", "Ctrl+V", Paste);
        var comment = Item("Toggle Comment", "Ctrl+/", ToggleComment);
        menu.Items.AddRange([undo, redo, new ToolStripSeparator(), cut, Item("Copy", "Ctrl+C", Copy), paste,
            Item("Select All", "Ctrl+A", SelectAll), new ToolStripSeparator(), Item("Find...", "Ctrl+F", () => ShowFind(false)),
            Item("Replace...", "Ctrl+H", () => ShowFind(true)), comment]);
        menu.Opening += (_, _) =>
        {
            undo.Enabled = CanUndo;
            redo.Enabled = CanRedo;
            cut.Enabled = paste.Enabled = !ReadOnly;
            comment.Enabled = !ReadOnly && _lua;
        };
        return menu;
    }

    public void GoToLine(int line, bool selectLine = false)
    {
        int count = _box.GetLineFromCharIndex(_box.TextLength) + 1;
        line = Math.Clamp(line, 1, count);
        int start = _box.GetFirstCharIndexFromLine(line - 1);
        if (start < 0) return;
        int end = line < count ? _box.GetFirstCharIndexFromLine(line) - 1 : _box.TextLength;
        _box.Select(start, selectLine ? Math.Max(0, end - start) : 0);
        CenterLine(line - 1);
        _box.Focus();
    }

    void CenterLine(int zeroBasedLine)
    {
        int first = (int)Native.SendMessage(_box.Handle, Native.EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        int lineHeight = Math.Max(1, (int)Math.Round(_box.Font.GetHeight() * _box.ZoomFactor));
        int visible = Math.Max(1, _box.ClientSize.Height / lineHeight);
        int target = Math.Max(0, zeroBasedLine - visible / 3);
        Native.SendMessage(_box.Handle, Native.EM_LINESCROLL, IntPtr.Zero, target - first);
        _gutter.Invalidate();
    }

    public void SetErrorLine(int line)
    {
        _gutter.ErrorLine = line;
        _gutter.Invalidate();
        if (line > 0) GoToLine(line, selectLine: true);
    }

    public void ClearErrorLine()
    {
        if (_gutter.ErrorLine < 0) return;
        _gutter.ErrorLine = -1;
        _gutter.Invalidate();
    }

    // ---------------------------------------------------------------- highlighting

    void AttachTom()
    {
        ReleaseTom();
        Native.SendMessage(_box.Handle, Native.EM_SETUNDOLIMIT, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Native.SendMessage(_box.Handle, Native.EM_GETOLEINTERFACE, IntPtr.Zero, out IntPtr ole);
            if (ole == IntPtr.Zero) return;
            try { _tom = (ITextDocument)Marshal.GetObjectForIUnknown(ole); }
            finally { Marshal.Release(ole); }
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException) { _tom = null; }
    }

    void ReleaseTom()
    {
        if (_tom != null && Marshal.IsComObject(_tom)) Marshal.ReleaseComObject(_tom);
        _tom = null;
    }

    void UpdateHighlight()
    {
        _highlightTimer.Stop();
        string text = _box.Text;
        if (text == _lastText) return;
        var runs = LuaLexer.Lex(text, _lua);

        int prefix = 0, maxPrefix = Math.Min(text.Length, _lastText.Length);
        while (prefix < maxPrefix && text[prefix] == _lastText[prefix]) prefix++;
        int suffix = 0, maxSuffix = Math.Min(text.Length, _lastText.Length) - prefix;
        while (suffix < maxSuffix && text[text.Length - 1 - suffix] == _lastText[_lastText.Length - 1 - suffix]) suffix++;
        int delta = text.Length - _lastText.Length;
        int changedEnd = text.Length - suffix;

        int from = RunIndexAt(runs, Math.Max(0, prefix - 1));
        int to = runs.Count;
        for (int k = from; k < runs.Count; k++)
        {
            var r = runs[k];
            if (r.Start < changedEnd || r.Start <= prefix) continue;
            int old = RunStartingAt(_lastRuns, r.Start - delta);
            if (old >= 0 && _lastRuns[old].Length == r.Length && _lastRuns[old].Kind == r.Kind) { to = k; break; }
        }

        if (_passChar >= 0 && _passChar > prefix) _passChar = Math.Max(prefix, _passChar + delta);
        _lastText = text;
        _lastRuns = runs;
        if (runs.Count == 0) return;

        int spanEnd = to >= runs.Count ? text.Length : runs[to].Start;
        int span = spanEnd - runs[from].Start;
        if (to - from <= TomRunLimit || span <= ImmediateChars) Colorize(from, to);
        else if (span >= FullReloadChars && span >= text.Length / 3) FullRecolor();
        else
        {
            ColorizeVisible();
            int start = runs[Math.Min(from, runs.Count - 1)].Start;
            _passChar = _passChar < 0 ? start : Math.Min(_passChar, start);
            _passTimer.Start();
        }
    }

    /// <summary>One RTF reload of the whole document; faster than chunked recoloring when most of it changed color.</summary>
    void FullRecolor()
    {
        _passTimer.Stop();
        _passChar = -1;
        var scroll = Native.GetScrollPos(_box.Handle);
        int selStart = _box.SelectionStart, selLength = _box.SelectionLength;
        float zoom = _box.ZoomFactor;
        Native.SendMessage(_box.Handle, Native.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        _loading = true;
        try
        {
            _box.Rtf = LuaLexer.BuildRtf(_lastText, _lastRuns, Palette, FontName, FontSize, keepFinalLineBreak: true);
        }
        finally
        {
            if (_box.ZoomFactor != zoom) _box.ZoomFactor = zoom;
            _box.Select(selStart, selLength);
            Native.SetScrollPos(_box.Handle, scroll);
            _loading = false;
            Native.SendMessage(_box.Handle, Native.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _box.Invalidate();
        }
    }

    void ContinuePass()
    {
        if (_passChar < 0 || _lastRuns.Count == 0) { _passTimer.Stop(); _passChar = -1; return; }
        int from = RunIndexAt(_lastRuns, Math.Min(_passChar, Math.Max(0, _lastText.Length - 1)));
        int limit = _lastRuns[from].Start + CharsPerPass, to = from + 1;
        while (to < _lastRuns.Count && _lastRuns[to].Start < limit) to++;
        Colorize(from, to);
        if (to >= _lastRuns.Count) { _passChar = -1; _passTimer.Stop(); }
        else _passChar = _lastRuns[to].Start;
    }

    void ColorizeVisible()
    {
        int first = _box.GetCharIndexFromPosition(new Point(0, 0));
        int last = _box.GetCharIndexFromPosition(new Point(_box.ClientSize.Width, _box.ClientSize.Height));
        Colorize(RunIndexAt(_lastRuns, Math.Max(0, first - 2000)), Math.Min(_lastRuns.Count, RunIndexAt(_lastRuns, last + 2000) + 1));
    }

    void Colorize(int from, int to)
    {
        if (from >= to) return;
        if (to - from <= TomRunLimit && _tom != null && ColorizeTom(from, to)) return;
        ColorizeRtf(from, to);
    }

    /// <summary>Re-streams the span as RTF: identical text, new colors. RichEdit undo is off, so nothing is recorded.</summary>
    void ColorizeRtf(int from, int to)
    {
        int start = _lastRuns[from].Start, end = _lastRuns[to - 1].End;
        string rtf = LuaLexer.BuildRtf(_lastText, _lastRuns, from, to, Palette, FontName, FontSize);
        var scroll = Native.GetScrollPos(_box.Handle);
        int selStart = _box.SelectionStart, selLength = _box.SelectionLength;
        Native.SendMessage(_box.Handle, Native.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        _loading = true;
        try
        {
            _box.Select(start, end - start);
            _box.SelectedRtf = rtf;
        }
        finally
        {
            _box.Select(selStart, selLength);
            Native.SetScrollPos(_box.Handle, scroll);
            _loading = false;
            Native.SendMessage(_box.Handle, Native.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _box.Invalidate();
        }
    }

    bool ColorizeTom(int from, int to)
    {
        var tom = _tom!;
        tom.Freeze(out _);
        ITextRange? range = null;
        try
        {
            if (tom.Range(0, 0, out range) != 0 || range == null) return false;
            for (int k = from; k < to; k++)
            {
                var r = _lastRuns[k];
                range.SetRange(r.Start, r.End);
                if (range.GetFont(out var font) != 0 || font == null) return false;
                var c = Palette[(int)r.Kind];
                font.SetForeColor(c.R | (c.G << 8) | (c.B << 16));
                Marshal.ReleaseComObject(font);
            }
            return true;
        }
        catch (COMException) { return false; }
        finally
        {
            if (range != null) Marshal.ReleaseComObject(range);
            tom.Unfreeze(out _);
        }
    }

    static int RunIndexAt(List<Run> runs, int pos)
    {
        int lo = 0, hi = runs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (runs[mid].End <= pos) lo = mid + 1;
            else if (runs[mid].Start > pos) hi = mid - 1;
            else return mid;
        }
        return Math.Clamp(lo, 0, Math.Max(0, runs.Count - 1));
    }

    static int RunStartingAt(List<Run> runs, int start)
    {
        int lo = 0, hi = runs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (runs[mid].Start < start) lo = mid + 1;
            else if (runs[mid].Start > start) hi = mid - 1;
            else return mid;
        }
        return -1;
    }

    // ---------------------------------------------------------------- editing helpers

    internal void NewLineWithIndent()
    {
        int pos = _box.SelectionStart;
        int lineStart = _box.GetFirstCharIndexOfCurrentLine();
        string text = _box.Text;
        string before = text.Substring(lineStart, Math.Max(0, Math.Min(pos, text.Length) - lineStart));
        string indent = before[..(before.Length - before.TrimStart(' ', '\t').Length)];
        if (_lua && OpensBlock(before.Trim())) indent += "  ";
        ReplaceSelection("\n" + indent);
    }

    static readonly Regex FunctionStart = new(@"(^|[=(,\s])function\b[^)]*\)?\s*$", RegexOptions.Compiled);

    static bool OpensBlock(string line)
    {
        int comment = line.IndexOf("--", StringComparison.Ordinal);
        if (comment >= 0) line = line[..comment].TrimEnd();
        if (line.Length == 0) return false;
        return line.EndsWith(" then") || line == "then" || line.EndsWith(" do") || line == "do" || line == "else" ||
               line == "repeat" || line.EndsWith('{') || FunctionStart.IsMatch(line);
    }

    (int First, int Last, int Start, int End) SelectedLines()
    {
        int selStart = _box.SelectionStart, selEnd = selStart + _box.SelectionLength;
        int first = _box.GetLineFromCharIndex(selStart);
        int last = _box.GetLineFromCharIndex(selEnd);
        if (last > first && selEnd == _box.GetFirstCharIndexFromLine(last)) last--;
        int start = _box.GetFirstCharIndexFromLine(first);
        int lineCount = _box.GetLineFromCharIndex(_box.TextLength) + 1;
        int end = last + 1 < lineCount ? _box.GetFirstCharIndexFromLine(last + 1) - 1 : _box.TextLength;
        return (first, last, start, end);
    }

    void ReplaceLines(int start, int end, string[] lines, bool keepSelection)
    {
        string replacement = string.Join("\n", lines);
        _box.Select(start, end - start);
        ReplaceSelection(replacement);
        if (keepSelection) _box.Select(start, replacement.Length);
    }

    internal void Indent(bool outdent)
    {
        bool multi = _box.SelectionLength > 0 && _box.SelectedText.Contains('\n');
        if (!multi && !outdent)
        {
            ReplaceSelection("  ");
            return;
        }
        var (_, _, start, end) = SelectedLines();
        string[] lines = _box.Text.Substring(start, end - start).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (outdent)
            {
                int remove = 0;
                while (remove < 2 && remove < lines[i].Length && lines[i][remove] == ' ') remove++;
                if (remove == 0 && lines[i].StartsWith('\t')) remove = 1;
                lines[i] = lines[i][remove..];
            }
            else if (lines[i].Length > 0) lines[i] = "  " + lines[i];
        }
        ReplaceLines(start, end, lines, keepSelection: multi);
    }

    public void ToggleComment()
    {
        if (ReadOnly || !_lua) return;
        var (_, _, start, end) = SelectedLines();
        string[] lines = _box.Text.Substring(start, end - start).Split('\n');
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        if (nonEmpty.Count == 0) return;
        bool uncomment = nonEmpty.All(l => l.TrimStart().StartsWith("--"));
        int indent = nonEmpty.Min(l => l.Length - l.TrimStart().Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.Trim().Length == 0) continue;
            if (uncomment)
            {
                int at = l.IndexOf("--", StringComparison.Ordinal);
                int len = at + 2 < l.Length && l[at + 2] == ' ' ? 3 : 2;
                lines[i] = l.Remove(at, len);
            }
            else lines[i] = l.Insert(indent, "-- ");
        }
        ReplaceLines(start, end, lines, keepSelection: true);
    }

    // ---------------------------------------------------------------- find / replace

    public void ShowFind(bool replace)
    {
        string selected = _box.SelectionLength is > 0 and < 200 && !_box.SelectedText.Contains('\n') ? _box.SelectedText : "";
        _find.Open(replace, selected);
    }

    public void HideFind()
    {
        _find.Visible = false;
        _box.Focus();
    }

    public bool FindNext(bool backward)
    {
        string query = _find.Query;
        if (query.Length == 0) { _find.Open(false, ""); return false; }
        var comparison = _find.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = _box.Text;
        int index;
        if (backward)
        {
            int from = _box.SelectionStart - 1;
            index = from >= 0 ? text.LastIndexOf(query, from, comparison) : -1;
            if (index < 0) index = text.LastIndexOf(query, comparison);
        }
        else
        {
            int from = Math.Min(text.Length, _box.SelectionStart + _box.SelectionLength);
            index = text.IndexOf(query, from, comparison);
            if (index < 0) index = text.IndexOf(query, comparison);
        }
        if (index < 0)
        {
            _find.SetStatus("No results", true);
            return false;
        }
        _box.Select(index, query.Length);
        CenterLine(_box.GetLineFromCharIndex(index));
        _find.SetStatus($"{CountBefore(text, query, index, comparison) + 1} of {Count(text, query, comparison)}", false);
        return true;
    }

    internal void ReplaceCurrent()
    {
        if (ReadOnly) return;
        var comparison = _find.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (_box.SelectionLength > 0 && string.Equals(_box.SelectedText, _find.Query, comparison))
            ReplaceSelection(_find.Replacement);
        FindNext(false);
    }

    internal void ReplaceAll()
    {
        if (ReadOnly || _find.Query.Length == 0) return;
        var comparison = _find.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = _box.Text;
        int count = Count(text, _find.Query, comparison);
        if (count == 0) { _find.SetStatus("No results", true); return; }
        ReplaceDocument(text.Replace(_find.Query, _find.Replacement, comparison));
        _find.SetStatus($"Replaced {count}", false);
    }

    static int Count(string text, string query, StringComparison comparison)
    {
        int count = 0;
        for (int i = text.IndexOf(query, comparison); i >= 0; i = text.IndexOf(query, i + query.Length, comparison)) count++;
        return count;
    }

    static int CountBefore(string text, string query, int limit, StringComparison comparison)
    {
        int count = 0;
        for (int i = text.IndexOf(query, comparison); i >= 0 && i < limit; i = text.IndexOf(query, i + query.Length, comparison)) count++;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _highlightTimer.Dispose();
            _passTimer.Dispose();
            ReleaseTom();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- inner controls

    sealed class EditorBox : RichTextBox
    {
        readonly CodeEditor _owner;

        public EditorBox(CodeEditor owner)
        {
            _owner = owner;
            Dock = DockStyle.Fill;
            BorderStyle = BorderStyle.None;
            BackColor = C.BG;
            ForeColor = C.FG;
            Font = new Font(FontName, FontSize);
            WordWrap = false;
            ScrollBars = RichTextBoxScrollBars.ForcedBoth;
            AcceptsTab = true;
            DetectUrls = false;
            HideSelection = false;
            EnableAutoDragDrop = false;
            ShortcutsEnabled = true;
            Theme.DarkControl(this);
        }

        public void PastePlain()
        {
            if (ReadOnly || !Clipboard.ContainsText()) return;
            _owner.ReplaceSelection(Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    PastePlain();
                    return true;
                case Keys.Control | Keys.Z:
                case Keys.Alt | Keys.Back:
                    _owner.Undo();
                    return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z:
                    _owner.Redo();
                    return true;
                case Keys.Control | Keys.L: case Keys.Control | Keys.E: case Keys.Control | Keys.R: case Keys.Control | Keys.J:
                case Keys.Control | Keys.D1: case Keys.Control | Keys.D2: case Keys.Control | Keys.D5:
                case Keys.Control | Keys.B: case Keys.Control | Keys.I: case Keys.Control | Keys.U:
                case Keys.Control | Keys.Shift | Keys.L: case Keys.Control | Keys.Shift | Keys.A:
                case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Shift | Keys.Oemplus:
                    return true;
                case Keys.Tab:
                    if (!ReadOnly) _owner.Indent(false);
                    return true;
                case Keys.Shift | Keys.Tab:
                    if (!ReadOnly) _owner.Indent(true);
                    return true;
                case Keys.Enter:
                    if (ReadOnly) break;
                    _owner.NewLineWithIndent();
                    return true;
                case Keys.Control | Keys.OemQuestion:
                case Keys.Control | Keys.Divide:
                    _owner.ToggleComment();
                    return true;
                case Keys.Control | Keys.F:
                    _owner.ShowFind(false);
                    return true;
                case Keys.Control | Keys.H:
                    _owner.ShowFind(!ReadOnly);
                    return true;
                case Keys.F3:
                    _owner.FindNext(false);
                    return true;
                case Keys.Shift | Keys.F3:
                    _owner.FindNext(true);
                    return true;
                case Keys.Escape when _owner.FindVisible:
                    _owner.HideFind();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg is 0x0115 or 0x020A) _owner._gutter.Invalidate();
        }
    }

    sealed class LineGutter : Control
    {
        readonly RichTextBox _box;
        readonly string _fontName;
        readonly float _fontSize;
        public int ErrorLine = -1;

        public LineGutter(RichTextBox box, string fontName, float fontSize)
        {
            _box = box;
            _fontName = fontName;
            _fontSize = fontSize;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = C.BG;
            Width = 48;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(C.BG);
            if (!_box.IsHandleCreated) return;
            float zoom = _box.ZoomFactor;
            using var font = new Font(_fontName, _fontSize * zoom);
            int lineCount = _box.GetLineFromCharIndex(_box.TextLength) + 1;
            int wanted = TextRenderer.MeasureText(new string('8', Math.Max(3, lineCount.ToString().Length)), font).Width + this.Px(18);
            if (Math.Abs(wanted - Width) > 2) { Width = wanted; return; }

            int lineHeight = (int)Math.Ceiling(font.GetHeight(g));
            int first = _box.GetLineFromCharIndex(_box.GetCharIndexFromPosition(new Point(1, 1)));
            int caret = _box.GetLineFromCharIndex(_box.SelectionStart);
            int visibleBottom = _box.ClientSize.Height;
            for (int line = first; line < lineCount; line++)
            {
                int ci = _box.GetFirstCharIndexFromLine(line);
                if (ci < 0) break;
                var pt = _box.GetPositionFromCharIndex(ci);
                if (ci >= _box.TextLength && line > 0)
                    pt = new Point(0, _box.GetPositionFromCharIndex(_box.GetFirstCharIndexFromLine(line - 1)).Y + lineHeight);
                if (pt.Y + lineHeight / 2 > visibleBottom) break;
                if (pt.Y + lineHeight < 0) continue;
                if (line + 1 == ErrorLine)
                {
                    using var red = new SolidBrush(C.RED);
                    int d = Math.Max(6, lineHeight / 2);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillEllipse(red, this.Px(4), pt.Y + (lineHeight - d) / 2, d, d);
                }
                TextRenderer.DrawText(g, (line + 1).ToString(), font, new Rectangle(0, pt.Y, Width - this.Px(10), lineHeight),
                    line == caret ? C.FG : C.FG_FAINT, TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            }
        }
    }

    sealed class FindBar : Panel
    {
        readonly CodeEditor _owner;
        readonly TextBox _findBox, _replaceBox;
        readonly CheckBox _matchCase;
        readonly Label _status;
        readonly FlowLayoutPanel _replaceRow;

        public FindBar(CodeEditor owner)
        {
            _owner = owner;
            BackColor = C.BG3;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(this.Px(6), this.Px(4), this.Px(6), this.Px(4));

            _findBox = Box("Find", this.Px(280));
            _replaceBox = Box("Replace with", this.Px(280));
            _matchCase = new CheckBox { Text = "Aa", ForeColor = C.FG_DIM, AutoSize = true, Margin = new Padding(6, 5, 0, 0), FlatStyle = FlatStyle.Flat };
            _status = new Label { ForeColor = C.FG_DIM, AutoSize = true, Margin = new Padding(8, 7, 0, 0) };

            var findRow = Row();
            findRow.Controls.AddRange([_findBox, Btn("↑", "Previous (Shift+F3)", (_, _) => _owner.FindNext(true)),
                Btn("↓", "Next (F3)", (_, _) => _owner.FindNext(false)), _matchCase, _status,
                Btn("✕", "Close (Esc)", (_, _) => _owner.HideFind())]);
            _replaceRow = Row();
            _replaceRow.Controls.AddRange([_replaceBox, Btn("Replace", "Replace this match", (_, _) => _owner.ReplaceCurrent()),
                Btn("Replace all", "Replace every match", (_, _) => _owner.ReplaceAll())]);

            var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = C.BG3, Dock = DockStyle.Fill };
            stack.Controls.Add(findRow);
            stack.Controls.Add(_replaceRow);
            Controls.Add(stack);

            _findBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { _owner.FindNext(e.Shift); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { _owner.HideFind(); e.SuppressKeyPress = true; }
            };
            _findBox.TextChanged += (_, _) => _status.Text = "";
            _replaceBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { _owner.ReplaceCurrent(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { _owner.HideFind(); e.SuppressKeyPress = true; }
            };
        }

        public string Query => _findBox.Text;
        public string Replacement => _replaceBox.Text;
        public bool MatchCase => _matchCase.Checked;

        public void Open(bool replace, string initial)
        {
            _replaceRow.Visible = replace;
            Visible = true;
            if (initial.Length > 0) _findBox.Text = initial;
            _findBox.Focus();
            _findBox.SelectAll();
        }

        public void SetStatus(string text, bool bad)
        {
            _status.Text = text;
            _status.ForeColor = bad ? C.RED : C.FG_DIM;
        }

        static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = false, BackColor = C.BG3, Margin = Padding.Empty };

        static TextBox Box(string placeholder, int width) => new()
        {
            Width = width,
            BackColor = C.BG,
            ForeColor = C.FG,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = placeholder,
            Font = Theme.Ui(9.5f),
            Margin = new Padding(0, 2, 4, 2),
        };

        static Button Btn(string text, string tip, EventHandler click)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = C.BG4,
                ForeColor = C.FG,
                Font = Theme.Ui(9f),
                Margin = new Padding(2, 1, 2, 1),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = C.ACCENT;
            new ToolTip().SetToolTip(b, tip);
            b.Click += click;
            return b;
        }
    }
}
