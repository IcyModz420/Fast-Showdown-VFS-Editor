using System.Text;

namespace FF6PakEditor.UI;

enum TokenKind : byte { Plain, Keyword, Builtin, Number, String, Comment }

readonly record struct Run(int Start, int Length, TokenKind Kind)
{
    public int End => Start + Length;
}

/// <summary>Splits Lua source into colored runs covering the whole text.</summary>
static class LuaLexer
{
    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local",
        "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "assert", "collectgarbage", "dofile", "error", "getfenv", "getmetatable", "ipairs", "load", "loadfile",
        "loadstring", "module", "next", "pairs", "pcall", "print", "rawequal", "rawget", "rawset", "require",
        "select", "setfenv", "setmetatable", "tonumber", "tostring", "type", "unpack", "xpcall", "self",
        "coroutine", "debug", "io", "math", "os", "package", "string", "table", "_G", "_VERSION",
    };

    public static List<Run> Lex(string text, bool lua = true)
    {
        var runs = new List<Run>(text.Length / 6 + 4);
        if (!lua)
        {
            if (text.Length > 0) runs.Add(new Run(0, text.Length, TokenKind.Plain));
            return runs;
        }

        int n = text.Length, i = 0, plain = -1;
        void Flush(int upTo)
        {
            if (plain >= 0 && upTo > plain) runs.Add(new Run(plain, upTo - plain, TokenKind.Plain));
            plain = -1;
        }
        void Emit(int start, int end, TokenKind kind)
        {
            Flush(start);
            runs.Add(new Run(start, end - start, kind));
        }

        while (i < n)
        {
            char c = text[i];
            if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                int start = i;
                int level = LongBracket(text, i + 2);
                if (level >= 0) i = CloseLong(text, i + 2 + level + 2, level);
                else
                {
                    int nl = text.IndexOf('\n', i);
                    i = nl < 0 ? n : nl;
                }
                Emit(start, i, TokenKind.Comment);
            }
            else if (c == '[' && LongBracket(text, i) is var lvl && lvl >= 0)
            {
                int start = i;
                i = CloseLong(text, i + lvl + 2, lvl);
                Emit(start, i, TokenKind.String);
            }
            else if (c is '"' or '\'')
            {
                int start = i++;
                while (i < n)
                {
                    char ch = text[i];
                    if (ch == '\\') { i += 2; continue; }
                    if (ch == '\n') break;
                    i++;
                    if (ch == c) break;
                }
                i = Math.Min(i, n);
                Emit(start, i, TokenKind.String);
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(text[i + 1])))
            {
                int start = i;
                if (c == '0' && i + 1 < n && (text[i + 1] is 'x' or 'X'))
                {
                    i += 2;
                    while (i < n && char.IsAsciiHexDigit(text[i])) i++;
                }
                else
                {
                    while (i < n && (char.IsAsciiDigit(text[i]) || text[i] == '.')) i++;
                    if (i < n && text[i] is 'e' or 'E')
                    {
                        i++;
                        if (i < n && text[i] is '+' or '-') i++;
                        while (i < n && char.IsAsciiDigit(text[i])) i++;
                    }
                }
                Emit(start, i, TokenKind.Number);
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && IsIdent(text[i])) i++;
                string word = text[start..i];
                TokenKind kind = Keywords.Contains(word) ? TokenKind.Keyword
                    : Builtins.Contains(word) && !(start > 0 && text[start - 1] is '.' or ':') ? TokenKind.Builtin
                    : TokenKind.Plain;
                if (kind == TokenKind.Plain) { if (plain < 0) plain = start; }
                else Emit(start, i, kind);
            }
            else
            {
                if (plain < 0) plain = i;
                i++;
            }
        }
        Flush(n);
        return runs;
    }

    static bool IsIdent(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>Level of a long bracket "[==[" starting at <paramref name="i"/>, or -1.</summary>
    static int LongBracket(string text, int i)
    {
        if (i >= text.Length || text[i] != '[') return -1;
        int j = i + 1;
        while (j < text.Length && text[j] == '=') j++;
        return j < text.Length && text[j] == '[' ? j - i - 1 : -1;
    }

    static int CloseLong(string text, int from, int level)
    {
        string close = "]" + new string('=', level) + "]";
        int idx = from <= text.Length ? text.IndexOf(close, from, StringComparison.Ordinal) : -1;
        return idx < 0 ? text.Length : idx + close.Length;
    }

    /// <param name="keepFinalLineBreak">A full document load swallows one final \par, so add an extra one to keep a trailing line break.</param>
    public static string BuildRtf(string text, List<Run> runs, Color[] palette, string fontName, float fontSize, bool keepFinalLineBreak = false)
    {
        string rtf = BuildRtf(text, runs, 0, runs.Count, palette, fontName, fontSize);
        return keepFinalLineBreak && text.EndsWith('\n') ? rtf[..^1] + "\\par}" : rtf;
    }

    /// <summary>RTF for runs [fromRun, toRun) only, used to recolor a span in one stream-in.</summary>
    public static string BuildRtf(string text, List<Run> allRuns, int fromRun, int toRun, Color[] palette, string fontName, float fontSize)
    {
        var runs = fromRun == 0 && toRun == allRuns.Count ? allRuns : allRuns.GetRange(fromRun, toRun - fromRun);
        int length = runs.Count == 0 ? 0 : runs[^1].End - runs[0].Start;
        var sb = new StringBuilder(length + length / 3 + 512);
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fmodern\fcharset0 ").Append(fontName).Append(";}}");
        sb.Append(@"{\colortbl ;");
        foreach (var c in palette) sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
        sb.Append('}');
        sb.Append(@"\viewkind4\uc1\pard\f0\fs").Append((int)Math.Round(fontSize * 2)).Append(' ');
        int current = -1;
        foreach (var run in runs)
        {
            int color = (int)run.Kind + 1;
            if (color != current)
            {
                sb.Append(@"\cf").Append(color).Append(' ');
                current = color;
            }
            for (int i = run.Start; i < run.End; i++)
            {
                char ch = text[i];
                switch (ch)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '{': sb.Append(@"\{"); break;
                    case '}': sb.Append(@"\}"); break;
                    case '\n': sb.Append("\\par\r\n"); break;
                    case '\r': break;
                    case '\t': sb.Append(@"\tab "); break;
                    default:
                        if (ch < 0x80) sb.Append(ch);
                        else sb.Append(@"\u").Append((short)ch).Append('?');
                        break;
                }
            }
        }
        sb.Append('}');
        return sb.ToString();
    }
}
