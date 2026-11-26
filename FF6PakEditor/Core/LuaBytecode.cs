using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FF6PakEditor.Core;

sealed class LuaBytecodeException(string message) : Exception(message);

/// <summary>The machine-dependent parts of a Lua 5.1 bytecode header.</summary>
sealed record LuaFormat(bool BigEndian, byte IntSize, byte SizeTSize, byte InstructionSize, byte NumberSize, bool IntegralNumbers)
{
    /// <summary>Fast &amp; Furious: Showdown, PC: little-endian, 32-bit, float numbers.</summary>
    public static readonly LuaFormat Pc = new(false, 4, 4, 4, 4, false);
    /// <summary>Fast &amp; Furious: Showdown, Xbox 360: big-endian, 32-bit, float numbers.</summary>
    public static readonly LuaFormat Xbox360 = new(true, 4, 4, 4, 4, false);
    /// <summary>Standard 32-bit desktop Lua (double numbers): what unluac expects.</summary>
    public static readonly LuaFormat Decompiler = new(false, 4, 4, 4, 8, false);
    /// <summary>What the bundled 64-bit luac5.1.exe reads and writes.</summary>
    public static readonly LuaFormat HostLuac = new(false, 4, 8, 4, 8, false);

    public string Describe()
    {
        string number = IntegralNumbers ? $"{NumberSize * 8}-bit integer" : NumberSize == 4 ? "float" : "double";
        return $"{(BigEndian ? "big" : "little")}-endian, {SizeTSize * 8}-bit size_t, {number} numbers";
    }

    public string PlatformName =>
        this == Pc ? "PC/3DS" : this == Xbox360 ? "Xbox 360" : this == HostLuac ? "desktop x64" : this == Decompiler ? "desktop x86" : "custom";
}

sealed class LuaFunction
{
    public byte[]? Source;
    public long LineDefined, LastLineDefined;
    public byte UpvalueCount, ParameterCount, VarargFlags, MaxStackSize;
    public uint[] Code = [];
    public List<LuaConstant> Constants = new();
    public List<LuaFunction> Functions = new();
    public long[] LineInfo = [];
    public List<LuaLocal> Locals = new();
    public List<byte[]?> UpvalueNames = new();

    public int CountFunctions() => 1 + Functions.Sum(f => f.CountFunctions());
}

sealed record LuaLocal(byte[]? Name, long StartPc, long EndPc);

/// <summary>A constant. <see cref="Float32"/> keeps the exact bits of float constants so float formats round-trip.</summary>
readonly record struct LuaConstant(byte Type, bool Boolean, double Number, float? Float32, byte[]? String)
{
    public const byte Nil = 0, Bool = 1, Num = 3, Str = 4;

    public bool SameValue(LuaConstant other)
    {
        if (Type != other.Type) return false;
        return Type switch
        {
            Bool => Boolean == other.Boolean,
            Num => (float)Number == (float)other.Number || Number.Equals(other.Number),
            Str => (String ?? []).AsSpan().SequenceEqual(other.String ?? []),
            _ => true,
        };
    }
}

static class LuaBytecode
{
    public static bool IsBytecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'u' && data[3] == (byte)'a';

    public static LuaFormat ReadFormat(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12 || !IsBytecode(d)) throw new LuaBytecodeException("This is not Lua bytecode.");
        if (d[4] != 0x51) throw new LuaBytecodeException($"Unsupported Lua version 0x{d[4]:X2}; only Lua 5.1 bytecode is supported.");
        if (d[5] != 0) throw new LuaBytecodeException($"Unsupported Lua bytecode format {d[5]}.");
        if (d[6] > 1) throw new LuaBytecodeException($"Invalid endianness flag {d[6]}.");
        if (d[7] is not (4 or 8) || d[8] is not (4 or 8) || d[9] != 4 || d[10] is not (4 or 8) || d[11] > 1)
            throw new LuaBytecodeException($"Unsupported Lua type sizes (int={d[7]}, size_t={d[8]}, instruction={d[9]}, number={d[10]}).");
        return new LuaFormat(d[6] == 0, d[7], d[8], d[9], d[10], d[11] != 0);
    }

    public static (LuaFormat Format, LuaFunction Main) Parse(byte[] data)
    {
        var format = ReadFormat(data);
        var reader = new Reader(data, format);
        var main = reader.Function();
        if (reader.Position != data.Length)
            throw new LuaBytecodeException($"{data.Length - reader.Position} unexpected bytes after the main function.");
        return (format, main);
    }

    /// <summary>Top-level chunk name such as "@..\live\\Scripts\main.lua", or null when stripped.</summary>
    public static string? ReadChunkName(byte[] data)
    {
        try
        {
            var reader = new Reader(data, ReadFormat(data));
            var source = reader.String();
            return source == null ? null : Encoding.Latin1.GetString(source, 0, Math.Max(0, source.Length - 1));
        }
        catch (Exception) { return null; }
    }

    /// <param name="chunkName">Replaces the top-level source name; "" strips it, null keeps it.</param>
    public static byte[] Write(LuaFunction main, LuaFormat target, string? chunkName = null)
    {
        var writer = new Writer(target);
        byte[]? source = chunkName == null ? main.Source
            : chunkName.Length == 0 ? null
            : [.. Encoding.Latin1.GetBytes(chunkName), 0];
        writer.Header();
        writer.Function(main, source);
        return writer.ToArray();
    }

    public static byte[] Convert(byte[] data, LuaFormat target, string? chunkName = null)
    {
        var (_, main) = Parse(data);
        return Write(main, target, chunkName);
    }

    /// <summary>Compares the executable parts (code, constants, nested functions); ignores names and line numbers.</summary>
    public static bool SameCode(LuaFunction a, LuaFunction b, out string? difference, string path = "main")
    {
        difference = null;
        if (a.ParameterCount != b.ParameterCount || a.VarargFlags != b.VarargFlags || a.UpvalueCount != b.UpvalueCount)
            difference = $"{path}: signature differs";
        else if (a.MaxStackSize != b.MaxStackSize)
            difference = $"{path}: stack size {a.MaxStackSize} vs {b.MaxStackSize}";
        else if (!a.Code.AsSpan().SequenceEqual(b.Code))
            difference = $"{path}: instructions differ ({a.Code.Length} vs {b.Code.Length})";
        else if (a.Constants.Count != b.Constants.Count || a.Constants.Where((c, i) => !c.SameValue(b.Constants[i])).Any())
            difference = $"{path}: constants differ";
        else if (a.Functions.Count != b.Functions.Count)
            difference = $"{path}: nested function count {a.Functions.Count} vs {b.Functions.Count}";
        if (difference != null) return false;
        for (int i = 0; i < a.Functions.Count; i++)
            if (!SameCode(a.Functions[i], b.Functions[i], out difference, $"{path}.{i + 1}")) return false;
        return true;
    }

    /// <summary>The double that prints shortest while still converting back to exactly <paramref name="f"/>.</summary>
    public static double CleanDouble(float f)
    {
        if (!float.IsFinite(f)) return f;
        double d = double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return BitConverter.SingleToInt32Bits((float)d) == BitConverter.SingleToInt32Bits(f) ? d : f;
    }

    sealed class Reader(byte[] data, LuaFormat format)
    {
        int _p = 12;
        public int Position => _p;

        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _p + n > data.Length) throw new LuaBytecodeException("Unexpected end of bytecode (file is truncated or corrupt).");
            var span = data.AsSpan(_p, n);
            _p += n;
            return span;
        }

        long Integer(int size)
        {
            var s = Take(size);
            return size switch
            {
                4 => format.BigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
                8 => format.BigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s),
                _ => throw new LuaBytecodeException($"Unsupported integer size {size}."),
            };
        }

        public long Int() => Integer(format.IntSize);

        int Count(int minBytesEach)
        {
            long n = Int();
            if (n < 0 || n * minBytesEach > data.Length - _p) throw new LuaBytecodeException("Bytecode is corrupt (invalid element count).");
            return (int)n;
        }

        public byte[]? String()
        {
            long size = Integer(format.SizeTSize);
            if (size == 0) return null;
            if (size < 0 || size > data.Length - _p) throw new LuaBytecodeException("Bytecode is corrupt (invalid string length).");
            return Take((int)size).ToArray();
        }

        public LuaFunction Function()
        {
            var f = new LuaFunction
            {
                Source = String(),
                LineDefined = Int(),
                LastLineDefined = Int(),
            };
            var b = Take(4);
            f.UpvalueCount = b[0]; f.ParameterCount = b[1]; f.VarargFlags = b[2]; f.MaxStackSize = b[3];

            f.Code = new uint[Count(format.InstructionSize)];
            for (int i = 0; i < f.Code.Length; i++)
            {
                var s = Take(4);
                f.Code[i] = format.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
            }

            int constants = Count(1);
            for (int i = 0; i < constants; i++)
            {
                byte type = Take(1)[0];
                f.Constants.Add(type switch
                {
                    LuaConstant.Nil => new LuaConstant(type, false, 0, null, null),
                    LuaConstant.Bool => new LuaConstant(type, Take(1)[0] != 0, 0, null, null),
                    LuaConstant.Num => Number(),
                    LuaConstant.Str => new LuaConstant(type, false, 0, null, String()),
                    _ => throw new LuaBytecodeException($"Unknown constant type {type}."),
                });
            }

            int functions = Count(1);
            for (int i = 0; i < functions; i++) f.Functions.Add(Function());

            f.LineInfo = new long[Count(format.IntSize)];
            for (int i = 0; i < f.LineInfo.Length; i++) f.LineInfo[i] = Int();

            int locals = Count(1);
            for (int i = 0; i < locals; i++) f.Locals.Add(new LuaLocal(String(), Int(), Int()));

            int upvalues = Count(1);
            for (int i = 0; i < upvalues; i++) f.UpvalueNames.Add(String());
            return f;
        }

        LuaConstant Number()
        {
            if (format.IntegralNumbers)
                return new LuaConstant(LuaConstant.Num, false, Integer(format.NumberSize), null, null);
            var s = Take(format.NumberSize);
            if (format.NumberSize == 4)
            {
                float f = format.BigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
                return new LuaConstant(LuaConstant.Num, false, CleanDouble(f), f, null);
            }
            double d = format.BigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s);
            return new LuaConstant(LuaConstant.Num, false, d, null, null);
        }
    }

    sealed class Writer(LuaFormat format)
    {
        readonly MemoryStream _out = new();
        readonly byte[] _buf = new byte[8];

        public byte[] ToArray() => _out.ToArray();

        public void Header() => _out.Write([0x1B, (byte)'L', (byte)'u', (byte)'a', 0x51, 0,
            (byte)(format.BigEndian ? 0 : 1), format.IntSize, format.SizeTSize, format.InstructionSize, format.NumberSize,
            (byte)(format.IntegralNumbers ? 1 : 0)]);

        void Integer(long v, int size, string what)
        {
            var s = _buf.AsSpan(0, size);
            if (size == 4)
            {
                if (v is < int.MinValue or > int.MaxValue) throw new LuaBytecodeException($"{what} {v} does not fit in 32 bits.");
                if (format.BigEndian) BinaryPrimitives.WriteInt32BigEndian(s, (int)v); else BinaryPrimitives.WriteInt32LittleEndian(s, (int)v);
            }
            else if (format.BigEndian) BinaryPrimitives.WriteInt64BigEndian(s, v);
            else BinaryPrimitives.WriteInt64LittleEndian(s, v);
            _out.Write(s);
        }

        void Int(long v) => Integer(v, format.IntSize, "Value");

        void String(byte[]? s)
        {
            Integer(s?.Length ?? 0, format.SizeTSize, "String length");
            if (s != null) _out.Write(s);
        }

        void Number(LuaConstant c)
        {
            var s = _buf.AsSpan(0, format.NumberSize);
            if (format.IntegralNumbers)
            {
                if (c.Number != Math.Floor(c.Number)) throw new LuaBytecodeException($"Number {c.Number} is not an integer, but the target format only stores integers.");
                Integer((long)c.Number, format.NumberSize, "Number");
                return;
            }
            if (format.NumberSize == 4)
            {
                float f = c.Float32 ?? (float)c.Number;
                if (float.IsInfinity(f) && !double.IsInfinity(c.Number))
                    throw new LuaBytecodeException($"The number {c.Number.ToString(CultureInfo.InvariantCulture)} is too large for the game (it stores numbers as 32-bit floats).");
                if (format.BigEndian) BinaryPrimitives.WriteSingleBigEndian(s, f); else BinaryPrimitives.WriteSingleLittleEndian(s, f);
            }
            else
            {
                double d = c.Float32.HasValue ? CleanDouble(c.Float32.Value) : c.Number;
                if (format.BigEndian) BinaryPrimitives.WriteDoubleBigEndian(s, d); else BinaryPrimitives.WriteDoubleLittleEndian(s, d);
            }
            _out.Write(s);
        }

        public void Function(LuaFunction f, byte[]? source)
        {
            String(source);
            Int(f.LineDefined);
            Int(f.LastLineDefined);
            _out.Write([f.UpvalueCount, f.ParameterCount, f.VarargFlags, f.MaxStackSize]);

            Int(f.Code.Length);
            var s = _buf.AsSpan(0, 4);
            foreach (uint ins in f.Code)
            {
                if (format.BigEndian) BinaryPrimitives.WriteUInt32BigEndian(s, ins); else BinaryPrimitives.WriteUInt32LittleEndian(s, ins);
                _out.Write(s);
            }

            Int(f.Constants.Count);
            foreach (var c in f.Constants)
            {
                _out.WriteByte(c.Type);
                if (c.Type == LuaConstant.Bool) _out.WriteByte((byte)(c.Boolean ? 1 : 0));
                else if (c.Type == LuaConstant.Num) Number(c);
                else if (c.Type == LuaConstant.Str) String(c.String);
            }

            Int(f.Functions.Count);
            foreach (var child in f.Functions) Function(child, child.Source);

            Int(f.LineInfo.Length);
            foreach (long line in f.LineInfo) Int(line);

            Int(f.Locals.Count);
            foreach (var local in f.Locals)
            {
                String(local.Name);
                Int(local.StartPc);
                Int(local.EndPc);
            }

            Int(f.UpvalueNames.Count);
            foreach (var name in f.UpvalueNames) String(name);
        }
    }
}
