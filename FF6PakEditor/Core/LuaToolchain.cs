using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FF6PakEditor.Core;

sealed record LuaCompileResult(byte[]? Bytecode, string? Error, int? Line)
{
    public bool Success => Bytecode != null;
}

/// <summary>Wraps the bundled luac5.1.exe (compiler) and unluac.jar (decompiler, runs on Java).</summary>
sealed class LuaToolchain
{
    public string? LuacPath { get; private init; }
    public string? UnluacPath { get; private init; }
    public string? JavaPath { get; private init; }

    public bool CanCompile => LuacPath != null;
    public bool CanDecompile => UnluacPath != null && JavaPath != null;

    public static LuaToolchain Discover()
    {
        string baseDir = AppContext.BaseDirectory;
        string? luac = new[]
        {
            Path.Combine(baseDir, "tools", "lua51", "luac5.1.exe"),
            Path.Combine(baseDir, "tools", "luac5.1.exe"),
            Path.Combine(baseDir, "lua51", "luac5.1.exe"),
            Path.Combine(baseDir, "luac5.1.exe"),
        }.FirstOrDefault(File.Exists) ?? FindOnPath("luac5.1.exe");

        string? unluac = new[]
        {
            Path.Combine(baseDir, "tools", "unluac.jar"),
            Path.Combine(baseDir, "unluac.jar"),
        }.FirstOrDefault(File.Exists);

        return new LuaToolchain { LuacPath = luac, UnluacPath = unluac, JavaPath = FindJava() };
    }

    public string Describe() =>
        $"Compiler: {LuacPath ?? "not found (tools\\lua51\\luac5.1.exe)"}\n" +
        $"Decompiler: {UnluacPath ?? "not found (tools\\unluac.jar)"}\n" +
        $"Java: {JavaPath ?? "not found - install Java 8 or newer to decompile scripts"}";

    /// <summary>Compiles Lua source and converts the result to <paramref name="target"/>.</summary>
    /// <param name="fileName">Name used for the chunk while compiling (shown in error messages).</param>
    /// <param name="chunkName">Chunk name to store in the result (null keeps "@fileName").</param>
    /// <param name="strip">Leave out debug information (line numbers, local names), like the Xbox 360 scripts.</param>
    public LuaCompileResult Compile(string source, string fileName, LuaFormat target, string? chunkName, bool strip = false)
    {
        if (LuacPath == null)
            return new LuaCompileResult(null, "luac5.1.exe was not found. It should be in the 'tools\\lua51' folder next to the editor.", null);

        fileName = SafeFileName(fileName);
        if (strip) chunkName = "";
        using var work = new WorkDir();
        File.WriteAllBytes(Path.Combine(work.Path, fileName), new UTF8Encoding(false).GetBytes(source));
        var r = Run(LuacPath, strip ? ["-s", "-o", "out.luac", fileName] : ["-o", "out.luac", fileName], work.Path, 30_000);
        if (r.ExitCode != 0 || !File.Exists(Path.Combine(work.Path, "out.luac")))
        {
            string message = (r.StdErr + r.StdOut).Trim();
            var m = Regex.Match(message, Regex.Escape(fileName) + @":(\d+):\s*(.*)");
            if (m.Success) return new LuaCompileResult(null, $"Line {m.Groups[1].Value}: {m.Groups[2].Value.Trim()}", int.Parse(m.Groups[1].Value));
            return new LuaCompileResult(null, message.Length > 0 ? message : $"luac exited with code {r.ExitCode}.", null);
        }

        byte[] native = File.ReadAllBytes(Path.Combine(work.Path, "out.luac"));
        try
        {
            return new LuaCompileResult(LuaBytecode.Convert(native, target, chunkName), null, null);
        }
        catch (LuaBytecodeException ex)
        {
            return new LuaCompileResult(null, ex.Message, null);
        }
    }

    /// <summary>Checks syntax only.</summary>
    public LuaCompileResult CheckSyntax(string source, string fileName) =>
        Compile(source, fileName, LuaFormat.HostLuac, null);

    /// <summary>Decompiles any Lua 5.1 bytecode (PC, Xbox 360, ...) to source text.</summary>
    public string Decompile(byte[] bytecode)
    {
        if (UnluacPath == null) throw new InvalidOperationException("unluac.jar was not found. It should be in the 'tools' folder next to the editor.");
        if (JavaPath == null) throw new InvalidOperationException("Java was not found. Decompiling scripts needs Java 8 or newer (for example Eclipse Temurin). Install it or set JAVA_HOME, then restart the editor.");

        byte[] standard = LuaBytecode.Convert(bytecode, LuaFormat.Decompiler);
        using var work = new WorkDir();
        File.WriteAllBytes(Path.Combine(work.Path, "in.luac"), standard);
        var r = Run(JavaPath, ["-Dfile.encoding=UTF-8", "-jar", UnluacPath, "--output", "out.lua", "in.luac"], work.Path, 120_000);
        string outFile = Path.Combine(work.Path, "out.lua");
        if (r.ExitCode != 0 || !File.Exists(outFile))
        {
            string message = (r.StdErr + "\n" + r.StdOut).Trim();
            throw new InvalidOperationException("unluac could not decompile this script:\n" + (message.Length > 0 ? message : $"exit code {r.ExitCode}"));
        }
        return Encoding.UTF8.GetString(File.ReadAllBytes(outFile)).Replace("\r\n", "\n");
    }

    /// <summary>Bytecode listing from luac -l -l (works for any platform's bytecode).</summary>
    public string Disassemble(byte[] bytecode)
    {
        if (LuacPath == null) throw new InvalidOperationException("luac5.1.exe was not found.");
        byte[] native = LuaBytecode.Convert(bytecode, LuaFormat.HostLuac);
        using var work = new WorkDir();
        File.WriteAllBytes(Path.Combine(work.Path, "in.luac"), native);
        var r = Run(LuacPath, ["-l", "-l", "-p", "in.luac"], work.Path, 60_000);
        if (r.ExitCode != 0) throw new InvalidOperationException((r.StdErr + r.StdOut).Trim());
        return r.StdOut.Replace("\r\n", "\n");
    }

    static string SafeFileName(string name)
    {
        name = Path.GetFileName(name.Replace('/', '\\'));
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (!name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) name = Path.ChangeExtension(name, ".lua");
        return name.Length > 1 ? name : "script.lua";
    }

    readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    static ProcessResult Run(string exe, IEnumerable<string> args, string workDir, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish within {timeoutMs / 1000} seconds.");
        }
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout.Result, stderr.Result);
    }

    static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim().Trim('"'), exe))
            .FirstOrDefault(File.Exists);

    static string? FindJava()
    {
        var candidates = new List<string>();
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home)) candidates.Add(Path.Combine(home.Trim().Trim('"'), "bin", "java.exe"));
        if (FindOnPath("java.exe") is { } onPath) candidates.Add(onPath);

        foreach (string key in new[] { @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\Java Development Kit" })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(key);
                if (k == null) continue;
                foreach (string ver in k.GetSubKeyNames().OrderByDescending(v => v))
                {
                    using var vk = k.OpenSubKey(ver);
                    if (vk?.GetValue("JavaHome") is string jh) candidates.Add(Path.Combine(jh, "bin", "java.exe"));
                }
            }
            catch (System.Security.SecurityException) { }
        }

        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (string vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft", "Semeru" })
            {
                string dir = Path.Combine(root, vendor);
                if (!Directory.Exists(dir)) continue;
                foreach (string jdk in Directory.GetDirectories(dir).OrderByDescending(d => d))
                    candidates.Add(Path.Combine(jdk, "bin", "java.exe"));
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Short temp folder (luac does not support long paths).</summary>
    sealed class WorkDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FSVfsEditor", Guid.NewGuid().ToString("N")[..10]);
        public WorkDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
