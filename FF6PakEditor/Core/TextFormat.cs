using System.Text;

namespace FF6PakEditor.Core;

/// <summary>How a text file was stored, so it can be written back the same way.</summary>
sealed record TextEncodingInfo(Encoding Encoding, byte[] Preamble, string NewLine)
{
    public static readonly TextEncodingInfo Utf8Lf = new(new UTF8Encoding(false), [], "\n");

    bool IsLatin1 => Encoding.CodePage == 28591;

    public string Describe()
    {
        string name = Encoding.CodePage switch
        {
            65001 => "UTF-8",
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            28591 => "Latin-1",
            _ => Encoding.WebName,
        };
        return $"{name}{(Preamble.Length > 0 ? " (BOM)" : "")}, {(NewLine == "\r\n" ? "CRLF" : "LF")}";
    }

    public bool CanEncode(string text) => !IsLatin1 || text.All(ch => ch <= 0xFF);
}

static class TextFormat
{
    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lua", ".txt", ".xml", ".ini", ".cfg", ".conf", ".json", ".csv", ".tsv", ".htm", ".html", ".log", ".md",
        ".fx", ".fxh", ".hlsl", ".glsl", ".vsh", ".psh", ".vert", ".frag", ".shader", ".def", ".yaml", ".yml",
        ".css", ".js", ".bat", ".cmd", ".nfo", ".list", ".lst", ".mtl", ".obj", ".h", ".c", ".cpp", ".cs",
    };

    static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".luac", ".dds", ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".gif", ".wav", ".ogg", ".mp3", ".xma", ".bik",
        ".wavebatch", ".bdae", ".pvr", ".zip", ".pak", ".bin", ".dat", ".exe", ".dll", ".xex", ".ttf", ".otf",
    };

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool LooksLikeText(string path, ReadOnlySpan<byte> data)
    {
        string ext = Path.GetExtension(path);
        if (BinaryExtensions.Contains(ext) || LuaBytecode.IsBytecode(data)) return false;
        if (data.Length == 0) return true;
        if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF))) return true;

        var sample = data[..Math.Min(data.Length, 65536)];
        int control = 0;
        foreach (byte b in sample)
        {
            if (b == 0) return false;
            if (b < 0x20 && b is not (9 or 10 or 12 or 13)) control++;
        }
        return control <= sample.Length / 200 || (TextExtensions.Contains(ext) && control <= sample.Length / 20);
    }

    public static (string Text, TextEncodingInfo Info) Decode(byte[] data)
    {
        Encoding encoding;
        byte[] preamble = [];
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) { encoding = new UTF8Encoding(false); preamble = data[..3]; }
        else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { encoding = new UnicodeEncoding(false, false); preamble = data[..2]; }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { encoding = new UnicodeEncoding(true, false); preamble = data[..2]; }
        else
        {
            try
            {
                StrictUtf8.GetCharCount(data);
                encoding = new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Latin1;
            }
        }

        string text = encoding.GetString(data, preamble.Length, data.Length - preamble.Length);
        string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        return (text.Replace("\r\n", "\n"), new TextEncodingInfo(encoding, preamble, newLine));
    }

    public static byte[] Encode(string text, TextEncodingInfo info)
    {
        text = text.Replace("\r\n", "\n");
        if (info.NewLine != "\n") text = text.Replace("\n", info.NewLine);
        return [.. info.Preamble, .. info.Encoding.GetBytes(text)];
    }
}
