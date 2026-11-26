using System.Text;

namespace FF6PakEditor.Core;

/// <summary>
/// unluac sometimes cannot rebuild structured control flow (mostly in the Xbox 360 scripts, which were
/// compiled without debug info) and falls back to Lua 5.2 "goto" / "::label::", which Lua 5.1 cannot compile.
/// This rewrites every goto into equivalent Lua 5.1:
///   forward jump  -> the code between the goto and the label is wrapped in "repeat ... until true"
///                    and the goto becomes "do break end"
///   backward jump -> the code after the label is wrapped in a restart loop
/// Jumps that cross other loops (or other wrappers) use a flag that is re-checked after each loop it leaves,
/// and existing "break" statements that a wrapper would capture are routed to their original loop the same way.
/// </summary>
static class LuaGotoFixer
{
    public sealed record Result(string Source, int GotosFixed, string? Problem);

    public static bool HasGotos(string source) =>
        source.Contains("goto ", StringComparison.Ordinal) || source.Contains("::", StringComparison.Ordinal);

    public static Result Fix(string source)
    {
        List<Token> tokens;
        Block root;
        try
        {
            tokens = Lexer.Lex(source);
            root = new Parser(tokens).ParseChunk();
        }
        catch (FormatException ex)
        {
            return new Result(source, 0, "could not parse the script: " + ex.Message);
        }

        var plan = new EditPlan(source);
        int fixedCount = 0;
        string? problem = null;
        foreach (var function in root.FunctionBodies())
        {
            var outcome = new FunctionRewriter(function, plan, source).Run();
            fixedCount += outcome.Fixed;
            problem ??= outcome.Problem;
        }
        if (problem != null) return new Result(source, 0, problem);
        if (fixedCount == 0) return new Result(source, 0, null);
        try
        {
            return new Result(plan.Apply(), fixedCount, null);
        }
        catch (FormatException ex)
        {
            return new Result(source, 0, ex.Message);
        }
    }

    // ------------------------------------------------------------------ syntax tree

    enum Kind { Expr, Assign, Local, LocalFunction, Function, Return, Break, Goto, Label, Do, While, Repeat, If, NumFor, GenFor }

    sealed class Block
    {
        public readonly List<Stmt> Stmts = new();
        public Stmt? Owner;
        public bool IsFunctionBody;
        public int Start, End;

        public IEnumerable<Block> FunctionBodies()
        {
            if (IsFunctionBody) yield return this;
            foreach (var s in Stmts)
                foreach (var b in s.Blocks)
                    foreach (var f in b.FunctionBodies())
                        yield return f;
        }
    }

    sealed class Stmt
    {
        public Kind Kind;
        public int Start, End;
        public string? Label;
        public Block Parent = null!;
        public readonly List<Block> Blocks = new();
        public bool IsLoop => Kind is Kind.While or Kind.Repeat or Kind.NumFor or Kind.GenFor;
    }

    enum TokenKind { Name, Keyword, Number, String, Op, Eof }

    readonly record struct Token(TokenKind Kind, string Text, int Start, int End);

    static class Lexer
    {
        static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local",
            "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
        };

        public static List<Token> Lex(string s)
        {
            var tokens = new List<Token>(s.Length / 4);
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                int start = i;
                if (c == '-' && i + 1 < n && s[i + 1] == '-')
                {
                    i += 2;
                    int level = LongBracket(s, i);
                    if (level >= 0) i = CloseLong(s, i + level + 2, level, "comment");
                    else
                    {
                        int nl = s.IndexOf('\n', i);
                        i = nl < 0 ? n : nl;
                    }
                    continue;
                }
                if (c == '[' && LongBracket(s, i) is var lvl && lvl >= 0)
                {
                    i = CloseLong(s, i + lvl + 2, lvl, "string");
                    tokens.Add(new Token(TokenKind.String, s[start..i], start, i));
                    continue;
                }
                if (c is '"' or '\'')
                {
                    i++;
                    while (i < n && s[i] != c)
                    {
                        if (s[i] == '\\') i++;
                        else if (s[i] == '\n') throw new FormatException($"unfinished string at offset {start}");
                        i++;
                    }
                    if (i >= n) throw new FormatException($"unfinished string at offset {start}");
                    i++;
                    tokens.Add(new Token(TokenKind.String, s[start..i], start, i));
                    continue;
                }
                if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1])))
                {
                    if (c == '0' && i + 1 < n && s[i + 1] is 'x' or 'X') { i += 2; while (i < n && char.IsAsciiHexDigit(s[i])) i++; }
                    else
                    {
                        while (i < n && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
                        if (i < n && s[i] is 'e' or 'E')
                        {
                            i++;
                            if (i < n && s[i] is '+' or '-') i++;
                            while (i < n && char.IsAsciiDigit(s[i])) i++;
                        }
                    }
                    tokens.Add(new Token(TokenKind.Number, s[start..i], start, i));
                    continue;
                }
                if (char.IsAsciiLetter(c) || c == '_')
                {
                    while (i < n && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    string word = s[start..i];
                    tokens.Add(new Token(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Name, word, start, i));
                    continue;
                }
                string op = i + 2 < n && s.AsSpan(i, 3) is "..." ? "..."
                    : i + 1 < n && s.Substring(i, 2) is ".." or "==" or "~=" or "<=" or ">=" or "::" ? s.Substring(i, 2)
                    : c.ToString();
                if (op.Length == 1 && "+-*/%^#<>=(){}[];:,.".IndexOf(c) < 0) throw new FormatException($"unexpected character '{c}' at offset {i}");
                i += op.Length;
                tokens.Add(new Token(TokenKind.Op, op, start, i));
            }
            tokens.Add(new Token(TokenKind.Eof, "", n, n));
            return tokens;
        }

        static int LongBracket(string s, int i)
        {
            if (i >= s.Length || s[i] != '[') return -1;
            int j = i + 1;
            while (j < s.Length && s[j] == '=') j++;
            return j < s.Length && s[j] == '[' ? j - i - 1 : -1;
        }

        static int CloseLong(string s, int from, int level, string what)
        {
            string close = "]" + new string('=', level) + "]";
            int idx = s.IndexOf(close, from, StringComparison.Ordinal);
            if (idx < 0) throw new FormatException($"unfinished long {what}");
            return idx + close.Length;
        }
    }

    sealed class Parser(List<Token> tokens)
    {
        int _p;
        Token Cur => tokens[_p];
        Token Peek(int k = 1) => tokens[Math.Min(_p + k, tokens.Count - 1)];
        int LastEnd => _p > 0 ? tokens[_p - 1].End : 0;

        bool Is(string text) => Cur.Kind is TokenKind.Keyword or TokenKind.Op && Cur.Text == text;

        void Expect(string text)
        {
            if (!Is(text)) throw new FormatException($"'{text}' expected near '{Cur.Text}' (offset {Cur.Start})");
            _p++;
        }

        string ExpectName()
        {
            if (Cur.Kind != TokenKind.Name) throw new FormatException($"name expected near '{Cur.Text}' (offset {Cur.Start})");
            return tokens[_p++].Text;
        }

        public Block ParseChunk()
        {
            var block = ParseBlock(null);
            block.IsFunctionBody = true;
            if (Cur.Kind != TokenKind.Eof) throw new FormatException($"'<eof>' expected near '{Cur.Text}' (offset {Cur.Start})");
            return block;
        }

        Block ParseBlock(Stmt? owner)
        {
            var block = new Block { Owner = owner, Start = Cur.Start };
            while (!(Cur.Kind == TokenKind.Eof || Is("end") || Is("else") || Is("elseif") || Is("until")))
            {
                if (Is(";")) { _p++; continue; }
                block.Stmts.Add(ParseStatement(block));
            }
            block.End = LastEnd;
            return block;
        }

        Stmt ParseStatement(Block parent)
        {
            var s = new Stmt { Parent = parent, Start = Cur.Start };
            if (Is("::"))
            {
                _p++;
                s.Kind = Kind.Label;
                s.Label = ExpectName();
                Expect("::");
            }
            else if (Is("break")) { _p++; s.Kind = Kind.Break; }
            else if (Cur.Kind == TokenKind.Name && Cur.Text == "goto" && Peek().Kind == TokenKind.Name)
            {
                _p++;
                s.Kind = Kind.Goto;
                s.Label = ExpectName();
            }
            else if (Is("do"))
            {
                _p++;
                s.Kind = Kind.Do;
                s.Blocks.Add(ParseBlock(s));
                Expect("end");
            }
            else if (Is("while"))
            {
                _p++;
                s.Kind = Kind.While;
                Exp(s);
                Expect("do");
                s.Blocks.Add(ParseBlock(s));
                Expect("end");
            }
            else if (Is("repeat"))
            {
                _p++;
                s.Kind = Kind.Repeat;
                s.Blocks.Add(ParseBlock(s));
                Expect("until");
                Exp(s);
            }
            else if (Is("if"))
            {
                _p++;
                s.Kind = Kind.If;
                Exp(s);
                Expect("then");
                s.Blocks.Add(ParseBlock(s));
                while (Is("elseif"))
                {
                    _p++;
                    Exp(s);
                    Expect("then");
                    s.Blocks.Add(ParseBlock(s));
                }
                if (Is("else"))
                {
                    _p++;
                    s.Blocks.Add(ParseBlock(s));
                }
                Expect("end");
            }
            else if (Is("for"))
            {
                _p++;
                ExpectName();
                if (Is("="))
                {
                    _p++;
                    s.Kind = Kind.NumFor;
                    Exp(s);
                    Expect(",");
                    Exp(s);
                    if (Is(",")) { _p++; Exp(s); }
                }
                else
                {
                    s.Kind = Kind.GenFor;
                    while (Is(",")) { _p++; ExpectName(); }
                    Expect("in");
                    ExpList(s);
                }
                Expect("do");
                s.Blocks.Add(ParseBlock(s));
                Expect("end");
            }
            else if (Is("function"))
            {
                _p++;
                s.Kind = Kind.Function;
                ExpectName();
                while (Is(".")) { _p++; ExpectName(); }
                if (Is(":")) { _p++; ExpectName(); }
                FuncBody(s);
            }
            else if (Is("local"))
            {
                _p++;
                if (Is("function"))
                {
                    _p++;
                    s.Kind = Kind.LocalFunction;
                    ExpectName();
                    FuncBody(s);
                }
                else
                {
                    s.Kind = Kind.Local;
                    ExpectName();
                    while (Is(",")) { _p++; ExpectName(); }
                    if (Is("=")) { _p++; ExpList(s); }
                }
            }
            else if (Is("return"))
            {
                _p++;
                s.Kind = Kind.Return;
                if (!(Cur.Kind == TokenKind.Eof || Is("end") || Is("else") || Is("elseif") || Is("until") || Is(";"))) ExpList(s);
                if (Is(";")) _p++;
            }
            else
            {
                SuffixedExp(s);
                if (Is("=") || Is(","))
                {
                    s.Kind = Kind.Assign;
                    while (Is(",")) { _p++; SuffixedExp(s); }
                    Expect("=");
                    ExpList(s);
                }
                else s.Kind = Kind.Expr;
            }
            s.End = LastEnd;
            return s;
        }

        void FuncBody(Stmt owner)
        {
            Expect("(");
            if (!Is(")"))
            {
                while (true)
                {
                    if (Is("...")) { _p++; break; }
                    ExpectName();
                    if (!Is(",")) break;
                    _p++;
                }
            }
            Expect(")");
            var body = ParseBlock(owner);
            body.IsFunctionBody = true;
            owner.Blocks.Add(body);
            Expect("end");
        }

        void ExpList(Stmt owner)
        {
            Exp(owner);
            while (Is(",")) { _p++; Exp(owner); }
        }

        static readonly HashSet<string> BinaryOps = new(StringComparer.Ordinal)
            { "+", "-", "*", "/", "%", "^", "..", "==", "~=", "<", "<=", ">", ">=", "and", "or" };

        void Exp(Stmt owner)
        {
            if (Is("not") || Is("-") || Is("#")) { _p++; Exp(owner); return; }
            Simple(owner);
            while (Cur.Kind is TokenKind.Op or TokenKind.Keyword && BinaryOps.Contains(Cur.Text))
            {
                _p++;
                if (Is("not") || Is("-") || Is("#")) { _p++; Exp(owner); return; }
                Simple(owner);
            }
        }

        void Simple(Stmt owner)
        {
            if (Cur.Kind is TokenKind.Number or TokenKind.String || Is("nil") || Is("true") || Is("false") || Is("...")) { _p++; return; }
            if (Is("function")) { _p++; FuncBody(owner); return; }
            if (Is("{")) { Table(owner); return; }
            SuffixedExp(owner);
        }

        void SuffixedExp(Stmt owner)
        {
            if (Is("(")) { _p++; Exp(owner); Expect(")"); }
            else ExpectName();
            while (true)
            {
                if (Is(".")) { _p++; ExpectName(); }
                else if (Is("[")) { _p++; Exp(owner); Expect("]"); }
                else if (Is(":")) { _p++; ExpectName(); Args(owner); }
                else if (Is("(") || Is("{") || Cur.Kind == TokenKind.String) Args(owner);
                else return;
            }
        }

        void Args(Stmt owner)
        {
            if (Cur.Kind == TokenKind.String) { _p++; return; }
            if (Is("{")) { Table(owner); return; }
            Expect("(");
            if (!Is(")")) ExpList(owner);
            Expect(")");
        }

        void Table(Stmt owner)
        {
            Expect("{");
            while (!Is("}"))
            {
                if (Is("[")) { _p++; Exp(owner); Expect("]"); Expect("="); Exp(owner); }
                else if (Cur.Kind == TokenKind.Name && Peek().Kind == TokenKind.Op && Peek().Text == "=") { _p += 2; Exp(owner); }
                else Exp(owner);
                if (Is(",") || Is(";")) _p++;
                else break;
            }
            Expect("}");
        }
    }

    // ------------------------------------------------------------------ text edits

    sealed class EditPlan(string source)
    {
        readonly List<(int Pos, int Remove, int Order, string Text)> _edits = new();
        int _sequence;

        public void Insert(int pos, int order, string text) => _edits.Add((pos, 0, order * 100000 + _sequence++, text));
        public void Replace(int pos, int length, string text) => _edits.Add((pos, length, int.MaxValue, text));

        public string Apply()
        {
            var sb = new StringBuilder(source.Length + _edits.Count * 32);
            int cursor = 0;
            foreach (var e in _edits.OrderBy(e => e.Pos).ThenBy(e => e.Order))
            {
                if (e.Pos < cursor) throw new FormatException("two rewrites overlap");
                sb.Append(source, cursor, e.Pos - cursor);
                sb.Append(e.Text);
                cursor = e.Pos + e.Remove;
            }
            sb.Append(source, cursor, source.Length - cursor);
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ the rewrite

    sealed class Wrapper
    {
        public required string Label;
        public required Block Block;
        public int First, Last;        // statement indexes in Block, inclusive
        public bool Backward;
        public required string Flag;
        public bool NeedsFlag;
        public readonly List<string> ChecksAfter = new();
        public readonly List<string> DeclsBefore = new();
        public int Depth;
        public bool Contains(int index) => index >= First && index <= Last;
        public bool Contains(Wrapper w) => w.Block == Block && w.First >= First && w.Last <= Last && w != this;
    }

    sealed class FunctionRewriter(Block body, EditPlan plan, string source)
    {
        readonly List<Wrapper> _wrappers = new();
        readonly Dictionary<Stmt, List<string>> _loopChecks = new();
        int _flagCounter;

        public (int Fixed, string? Problem) Run()
        {
            var labels = new List<Stmt>();
            var gotos = new List<Stmt>();
            var breaks = new List<Stmt>();
            Collect(body, labels, gotos, breaks);
            if (gotos.Count == 0 && labels.Count == 0) return (0, null);

            // Resolve each goto to the visible label (the innermost enclosing block that declares it).
            var targets = new Dictionary<Stmt, Stmt>();
            foreach (var g in gotos)
            {
                Stmt? label = null;
                for (Block? b = g.Parent; b != null && label == null; b = b.IsFunctionBody ? null : b.Owner?.Parent)
                    label = b.Stmts.FirstOrDefault(s => s.Kind == Kind.Label && s.Label == g.Label);
                if (label == null) return (0, $"goto {g.Label} has no matching label");
                targets[g] = label;
            }

            foreach (var label in labels)
            {
                var jumps = gotos.Where(g => targets[g] == label).ToList();
                if (jumps.Count == 0) continue;
                var block = label.Parent;
                int at = block.Stmts.IndexOf(label);
                var tops = jumps.Select(g => block.Stmts.IndexOf(TopIn(g, block))).ToList();
                bool forward = tops.All(t => t < at), backward = tops.All(t => t > at);
                if (!forward && !backward) return (0, $"label {label.Label} is jumped to from both directions");
                _wrappers.Add(new Wrapper
                {
                    Label = label.Label!,
                    Block = block,
                    First = forward ? tops.Min() : at + 1,
                    Last = forward ? at - 1 : tops.Max(),
                    Backward = backward,
                    Flag = $"__goto_{label.Label}_{++_flagCounter}",
                });
            }

            // Wrappers in the same block must nest: widen any that only partially overlap.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var a in _wrappers)
                    foreach (var b in _wrappers)
                    {
                        if (a == b || a.Block != b.Block) continue;
                        bool overlap = a.First <= b.Last && b.First <= a.Last;
                        if (!overlap || a.Contains(b) || b.Contains(a) || (a.First == b.First && a.Last == b.Last)) continue;
                        var outer = a.Last >= b.Last ? a : b;
                        var inner = outer == a ? b : a;
                        if (outer.Backward) outer.Last = Math.Max(outer.Last, inner.Last);
                        else outer.First = Math.Min(outer.First, inner.First);
                        changed = true;
                    }
            }
            foreach (var w in _wrappers)
            {
                var label = w.Block.Stmts.First(s => s.Kind == Kind.Label && s.Label == w.Label);
                int at = w.Block.Stmts.IndexOf(label);
                if (w.Contains(at)) return (0, $"could not place label {w.Label}");
                foreach (var o in _wrappers)
                {
                    if (o == w || o.Block != w.Block) continue;
                    bool overlap = o.First <= w.Last && w.First <= o.Last;
                    if (overlap && !o.Contains(w) && !w.Contains(o)) return (0, $"the jumps to {w.Label} and {o.Label} cross each other");
                }
                w.Depth = _wrappers.Count(o => o != w && o.Block == w.Block && o.Contains(w.First) && o.Contains(w.Last) &&
                    (o.First < w.First || o.Last > w.Last || _wrappers.IndexOf(o) < _wrappers.IndexOf(w)));
            }

            foreach (var g in gotos)
            {
                string? problem = RewriteGoto(g, Find(targets[g]));
                if (problem != null) return (0, problem);
            }
            foreach (var br in breaks) RewriteBreak(br);
            foreach (var label in labels) RemoveLabel(label);
            EmitWrappers();
            return (gotos.Count, null);
        }

        Wrapper Find(Stmt label) => _wrappers.First(w => w.Block == label.Parent && w.Label == label.Label);

        static void Collect(Block block, List<Stmt> labels, List<Stmt> gotos, List<Stmt> breaks)
        {
            foreach (var s in block.Stmts)
            {
                if (s.Kind == Kind.Label) labels.Add(s);
                else if (s.Kind == Kind.Goto) gotos.Add(s);
                else if (s.Kind == Kind.Break) breaks.Add(s);
                foreach (var child in s.Blocks)
                    if (!child.IsFunctionBody) Collect(child, labels, gotos, breaks);
            }
        }

        /// <summary>The statement directly inside <paramref name="block"/> that contains <paramref name="s"/>.</summary>
        static Stmt TopIn(Stmt s, Block block)
        {
            while (s.Parent != block) s = s.Parent.Owner ?? throw new InvalidOperationException("statement is outside the block");
            return s;
        }

        /// <summary>Loops and wrappers between a statement and a stopping point, innermost first.</summary>
        IEnumerable<object> Crossed(Stmt from, Func<Block, int, Wrapper?, bool> stopAt)
        {
            var s = from;
            while (true)
            {
                var block = s.Parent;
                int index = block.Stmts.IndexOf(s);
                foreach (var w in _wrappers.Where(w => w.Block == block && w.Contains(index)).OrderByDescending(w => w.Depth))
                {
                    if (stopAt(block, index, w)) yield break;
                    yield return w;
                }
                if (stopAt(block, index, null) || block.IsFunctionBody || block.Owner == null) yield break;
                s = block.Owner;
                if (s.IsLoop) yield return s;
            }
        }

        string? RewriteGoto(Stmt g, Wrapper target)
        {
            bool reached = false;
            var crossed = Crossed(g, (_, _, w) => reached = w == target).ToList();
            if (!reached) return $"goto {g.Label} is not inside the region rebuilt for its label";
            bool viaFlag = target.Backward || crossed.Count > 0;
            if (viaFlag) target.NeedsFlag = true;
            foreach (var c in crossed) AddCheck(c, target.Flag);
            string replacement = viaFlag ? $"{target.Flag} = true do break end" : "do break end";
            plan.Replace(g.Start, g.End - g.Start, $"{replacement} --[[goto {g.Label}]]");
            return null;
        }

        void RewriteBreak(Stmt br)
        {
            // Wrappers crossed before reaching the loop this break belongs to.
            var crossed = new List<Wrapper>();
            foreach (var c in Crossed(br, (_, _, _) => false))
            {
                if (c is Stmt) break;
                crossed.Add((Wrapper)c);
            }
            if (crossed.Count == 0) return;
            string flag = $"__break_{++_flagCounter}";
            crossed[^1].DeclsBefore.Add($"local {flag} = false");
            foreach (var w in crossed) w.ChecksAfter.Add($"if {flag} then break end");
            plan.Replace(br.Start, br.End - br.Start, $"{flag} = true do break end");
        }

        void AddCheck(object crossed, string flag)
        {
            if (crossed is Wrapper w) w.ChecksAfter.Add($"if {flag} then break end");
            else
            {
                var loop = (Stmt)crossed;
                if (!_loopChecks.TryGetValue(loop, out var list)) _loopChecks[loop] = list = new List<string>();
                if (!list.Contains(flag)) list.Add(flag);
            }
        }

        void RemoveLabel(Stmt label)
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(0, label.Start - 1)) + 1;
            int lineEnd = source.IndexOf('\n', label.End);
            bool alone = source.AsSpan(lineStart, label.Start - lineStart).IsWhiteSpace() &&
                         source.AsSpan(label.End, (lineEnd < 0 ? source.Length : lineEnd) - label.End).IsWhiteSpace();
            if (alone) plan.Replace(lineStart, (lineEnd < 0 ? source.Length : lineEnd + 1) - lineStart, "");
            else plan.Replace(label.Start, label.End - label.Start, "");
        }

        string IndentAt(int pos)
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
            int i = lineStart;
            while (i < pos && source[i] is ' ' or '\t') i++;
            return source[lineStart..i];
        }

        void EmitWrappers()
        {
            foreach (var (loop, flags) in _loopChecks)
            {
                string indent = IndentAt(loop.Start);
                plan.Insert(loop.End, 0, string.Concat(flags.Select(f => $"\n{indent}if {f} then break end")));
            }
            foreach (var w in _wrappers)
            {
                var first = w.Block.Stmts[w.First];
                var last = w.Block.Stmts[w.Last];
                string indent = IndentAt(first.Start);
                var head = new StringBuilder();
                foreach (string d in w.DeclsBefore) head.Append(d).Append('\n').Append(indent);
                if (w.Backward)
                {
                    head.Append($"repeat -- restart point for goto {w.Label}\n").Append(indent);
                    head.Append($"local {w.Flag} = false\n").Append(indent);
                    head.Append("repeat\n").Append(indent);
                }
                else
                {
                    if (w.NeedsFlag) head.Append($"local {w.Flag} = false\n").Append(indent);
                    head.Append($"repeat -- skipped by goto {w.Label}\n").Append(indent);
                }
                plan.Insert(first.Start, w.Depth, head.ToString());

                var tail = new StringBuilder();
                tail.Append('\n').Append(indent).Append(w.Backward ? $"until true\n{indent}until not {w.Flag}" : "until true");
                foreach (string check in w.ChecksAfter) tail.Append('\n').Append(indent).Append(check);
                plan.Insert(last.End, 1000 - w.Depth, tail.ToString());
            }
        }
    }
}
