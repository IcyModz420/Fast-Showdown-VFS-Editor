namespace FF6PakEditor.Core;

enum GamePlatform { Xbox360, PC, Nintendo3DS }

static class GamePlatforms
{
    /// <summary>PC and 3DS scripts share one format (little-endian, float numbers); Xbox 360 is big-endian.</summary>
    public static LuaFormat LuaFormat(this GamePlatform p) => p == GamePlatform.Xbox360 ? Core.LuaFormat.Xbox360 : Core.LuaFormat.Pc;
    public static bool IndexIsBigEndian(this GamePlatform p) => p == GamePlatform.Xbox360;
    public static string DisplayName(this GamePlatform p) => p switch
    {
        GamePlatform.PC => "PC",
        GamePlatform.Nintendo3DS => "3DS",
        _ => "Xbox 360",
    };

    static readonly string[] ThreeDsExtensions = [".bcmdl", ".bcmdla", ".bcskla", ".bcmata", ".bcres", ".bclim", ".bcfnt", ".bcptl", ".bcenv", ".bcsdr"];

    public static bool IsThreeDsAsset(string path) => ThreeDsExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));
}

enum ItemState { Original, Modified, Added }

sealed class ArchiveItem
{
    internal ArchiveItem(string path, ZipEntryInfo? source)
    {
        Path = path;
        Source = source;
    }

    public string Path { get; }
    public ZipEntryInfo? Source { get; internal set; }
    public byte[]? NewData { get; internal set; }
    internal string? ExternalFile;
    internal long ExternalLength;

    public ItemState State => Source == null ? ItemState.Added : NewData != null ? ItemState.Modified : ItemState.Original;
    public bool IsDirectory => Path.EndsWith('/');
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/'));
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public long Size => NewData?.LongLength ?? (ExternalFile != null ? ExternalLength : Source?.UncompressedSize ?? 0);
    public long? PackedSize => State == ItemState.Original ? Source!.CompressedSize : null;
    public string Method => State == ItemState.Original
        ? Source!.Method switch { 0 => "Store", 8 => "Deflate", var m => $"#{m}" }
        : (Source?.Method == 0 ? "Store" : "Deflate");
}

sealed record SaveResult(string ArchivePath, string BinPath, string DatPath, long ArchiveSize, IndexBuild Index,
    int ChangedFiles, List<string> Backups);

/// <summary>An opened game archive plus pending (unsaved) edits.</summary>
sealed class GameArchive : IDisposable
{
    ZipReader? _reader;
    readonly List<ArchiveItem> _items = new();
    readonly Dictionary<string, ArchiveItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ArchiveItem> _deleted = new();

    public string? FilePath { get; private set; }
    public GamePlatform Platform { get; set; }
    public GamePlatform DetectedPlatform { get; private set; }
    public string DetectionReason { get; private set; } = "";
    public List<string> Warnings { get; } = new();

    public IReadOnlyList<ArchiveItem> Items => _items;
    public int FileCount => _items.Count(i => !i.IsDirectory);
    public int PendingChanges => _items.Count(i => i.State != ItemState.Original) + _deleted.Count;
    public bool HasChanges => PendingChanges > 0;
    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : "(new archive)";

    public static GameArchive Open(string path)
    {
        var reader = ZipReader.Open(path);
        var archive = new GameArchive { _reader = reader, FilePath = reader.FilePath };
        try
        {
            foreach (var e in reader.Entries)
            {
                if (archive._byPath.ContainsKey(e.Name))
                {
                    archive.Warnings.Add($"Duplicate entry '{e.Name}' - only the first copy is kept.");
                    continue;
                }
                var item = new ArchiveItem(e.Name, e);
                archive._items.Add(item);
                archive._byPath[e.Name] = item;
            }
            (archive.DetectedPlatform, archive.DetectionReason) = DetectPlatform(reader);
            archive.Platform = archive.DetectedPlatform;
            return archive;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public static GameArchive FromFolder(string folder, GamePlatform platform)
    {
        var archive = new GameArchive { Platform = platform, DetectedPlatform = platform, DetectionReason = "chosen when the archive was created" };
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var item = new ArchiveItem(rel, null) { ExternalFile = file, ExternalLength = new FileInfo(file).Length };
            archive._items.Add(item);
            archive._byPath[rel] = item;
        }
        if (archive._items.Count == 0) throw new ArchiveException("That folder contains no files.");
        return archive;
    }

    static (GamePlatform, string) DetectPlatform(ZipReader reader)
    {
        bool threeDs = reader.Entries.Any(e => GamePlatforms.IsThreeDsAsset(e.Name));
        (GamePlatform, string) LittleEndian(string why) => threeDs
            ? (GamePlatform.Nintendo3DS, why + " and the archive holds 3DS models (.bcmdl)")
            : (GamePlatform.PC, why);

        var (_, datPath) = VfsIndex.CompanionPaths(reader.FilePath);
        if (File.Exists(datPath))
        {
            byte[] head = new byte[4];
            using (var fs = File.OpenRead(datPath)) fs.ReadAtLeast(head, 4, throwOnEndOfStream: false);
            if (VfsIndex.DetectBigEndian(head) is bool be)
                return be ? (GamePlatform.Xbox360, $"{Path.GetFileName(datPath)} is big-endian")
                          : LittleEndian($"{Path.GetFileName(datPath)} is little-endian");
        }
        foreach (var e in reader.Entries.Where(e => e.Name.EndsWith(".luac", StringComparison.OrdinalIgnoreCase) && e.CanRead).Take(3))
        {
            try
            {
                var format = LuaBytecode.ReadFormat(reader.ReadData(e));
                return format.BigEndian ? (GamePlatform.Xbox360, "its scripts are big-endian Lua bytecode")
                                        : LittleEndian("its scripts are little-endian Lua bytecode");
            }
            catch (Exception ex) when (ex is ArchiveException or LuaBytecodeException) { }
        }
        return reader.FilePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            ? (GamePlatform.Xbox360, "by file extension (.pak)")
            : LittleEndian("by file extension");
    }

    public ArchiveItem? Find(string path) => _byPath.GetValueOrDefault(Normalize(path));

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    public byte[] ReadData(ArchiveItem item)
    {
        if (item.NewData != null) return item.NewData;
        if (item.ExternalFile != null) return File.ReadAllBytes(item.ExternalFile);
        if (item.Source == null || _reader == null) return [];
        return _reader.ReadData(item.Source);
    }

    public byte[]? ReadOriginalData(ArchiveItem item) =>
        item.Source != null && _reader != null ? _reader.ReadData(item.Source) : null;

    /// <summary>Replaces an existing entry (case-insensitive match) or adds a new one.</summary>
    public ArchiveItem SetData(string path, byte[] data)
    {
        path = Normalize(path);
        if (path.Length == 0 || path.EndsWith('/')) throw new ArchiveException("Enter a file path inside the archive, such as scripts/myscript.luac.");
        if (!_byPath.TryGetValue(path, out var item))
        {
            var restored = _deleted.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
            if (restored != null) _deleted.Remove(restored);
            item = restored ?? new ArchiveItem(path, null);
            _items.Add(item);
            _byPath[item.Path] = item;
        }
        item.NewData = data;
        item.ExternalFile = null;
        return item;
    }

    public void Delete(ArchiveItem item)
    {
        if (!_items.Remove(item)) return;
        _byPath.Remove(item.Path);
        if (item.Source != null)
        {
            item.NewData = null;
            _deleted.Add(item);
        }
    }

    /// <summary>Drops pending changes: modified entries go back to the original, added entries are removed.</summary>
    public void Revert(ArchiveItem item)
    {
        if (item.Source == null) Delete(item);
        else item.NewData = null;
    }

    public SaveResult Save(string targetPath, IProgress<SaveProgress>? progress, CancellationToken ct)
    {
        targetPath = Path.GetFullPath(targetPath);
        var (binPath, datPath) = VfsIndex.CompanionPaths(targetPath);
        string tmpArchive = targetPath + ".tmp", tmpBin = binPath + ".tmp", tmpDat = datPath + ".tmp";
        var items = _items.ToList();
        int changed = items.Count(i => i.State != ItemState.Original) + _deleted.Count;

        long needed = items.Sum(i => i.State == ItemState.Original ? i.Source!.CompressedSize + 200 : i.Size + 200) + (1 << 20);
        var drive = new DriveInfo(Path.GetPathRoot(targetPath)!);
        if (drive.IsReady && drive.AvailableFreeSpace < needed)
            throw new ArchiveException($"Not enough free space on {drive.Name}: saving needs about {Format.Size(needed)} but only {Format.Size(drive.AvailableFreeSpace)} is free.");

        IndexBuild index;
        try
        {
            var writeItems = items.Select(i => new WriteItem
            {
                Path = i.Path,
                Source = i.Source,
                NewData = i.NewData,
                LoadData = i.NewData == null && i.ExternalFile is { } file ? () => File.ReadAllBytes(file) : null,
                SizeHint = i.Size,
            }).ToList();

            List<WrittenEntry> written;
            using (var fs = new FileStream(tmpArchive, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                written = ArchiveWriter.Write(fs, _reader, writeItems, ArchiveDefaults.From(_reader), _reader?.Comment ?? [], progress, ct);
                fs.Flush(flushToDisk: true);
            }

            progress?.Report(new SaveProgress("Building index", 0, 1, null));
            index = VfsIndex.Build(written, Platform.IndexIsBigEndian());
            progress?.Report(new SaveProgress("Verifying", 0, 1, null));
            VerifyWritten(tmpArchive, written, index);
            File.WriteAllBytes(tmpBin, index.Bin);
            File.WriteAllBytes(tmpDat, index.Dat);
        }
        catch
        {
            TryDelete(tmpArchive); TryDelete(tmpBin); TryDelete(tmpDat);
            throw;
        }

        progress?.Report(new SaveProgress("Replacing files", 0, 1, null));
        var backups = new List<string>();
        string? previousPath = _reader?.FilePath;
        _reader?.Dispose();
        _reader = null;
        bool archiveCommitted = false;
        try
        {
            Commit(tmpArchive, targetPath, backups);
            archiveCommitted = true;
            Commit(tmpBin, binPath, backups);
            Commit(tmpDat, datPath, backups);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmpArchive); TryDelete(tmpBin); TryDelete(tmpDat);
            if (archiveCommitted) ReopenAfterSave(targetPath, items);
            else if (previousPath != null) _reader = ZipReader.Open(previousPath);
            if (archiveCommitted)
                throw new ArchiveException($"The archive was saved but its index could not be written ({ex.Message}). Use VFS > Rebuild Index before starting the game.");
            throw new ArchiveException($"Could not replace {Path.GetFileName(targetPath)}: {ex.Message}\n\nIf the game is running, close it and save again.");
        }

        ReopenAfterSave(targetPath, items);
        return new SaveResult(targetPath, binPath, datPath, new FileInfo(targetPath).Length, index, changed, backups);
    }

    void ReopenAfterSave(string path, List<ArchiveItem> items)
    {
        _reader = ZipReader.Open(path);
        FilePath = _reader.FilePath;
        _deleted.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Source = _reader.Entries[i];
            items[i].NewData = null;
            items[i].ExternalFile = null;
        }
    }

    static void VerifyWritten(string archivePath, List<WrittenEntry> written, IndexBuild index)
    {
        using var check = ZipReader.Open(archivePath);
        if (check.Entries.Count != written.Count)
            throw new ArchiveException($"Verification failed: wrote {written.Count} entries but read back {check.Entries.Count}.");
        for (int i = 0; i < written.Count; i++)
        {
            var w = written[i];
            var e = check.Entries[i];
            if (e.LocalHeaderOffset != w.LocalHeaderOffset || e.CompressedSize != w.CompressedSize || e.UncompressedSize != w.UncompressedSize ||
                e.Method != w.Method || check.ReadLocalHeader(e).Length - 30 != w.HeaderVariableLength || e.CdRecordOffset != w.CdRecordOffset)
                throw new ArchiveException($"Verification failed for '{w.Path}': the written headers do not match.");
            if (w.Rewritten && Crc32.Compute(check.ReadData(e)) != w.Crc32)
                throw new ArchiveException($"Verification failed for '{w.Path}': the data read back is different.");
        }

        var parsed = VfsIndex.Parse(index.Bin, index.Dat);
        if (parsed.Problems.Count > 0) throw new ArchiveException("Verification failed: " + parsed.Problems[0]);
        var files = written.Where(w => !w.IsDirectory).ToList();
        if (parsed.Files.Count != files.Count)
            throw new ArchiveException($"Verification failed: the index lists {parsed.Files.Count} files but the archive has {files.Count}.");
        foreach (var f in parsed.Files)
        {
            var w = files[f.FileIndex];
            if (!string.Equals(f.Path, w.Path, StringComparison.OrdinalIgnoreCase) || f.LocalHeaderOffset != w.LocalHeaderOffset)
                throw new ArchiveException($"Verification failed: index entry '{f.Path}' does not point at the right file.");
        }
    }

    static void Commit(string tmp, string final, List<string> backups)
    {
        if (!File.Exists(final))
        {
            File.Move(tmp, final);
            return;
        }
        string backup = final + ".bak";
        if (File.Exists(backup))
        {
            File.Replace(tmp, final, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Replace(tmp, final, backup, ignoreMetadataErrors: true);
            backups.Add(backup);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Rewrites only the .bin/.dat next to the archive on disk (for archives edited by other tools).</summary>
    public IndexBuild RebuildIndex(List<string> backups)
    {
        if (_reader == null || FilePath == null) throw new ArchiveException("Save the archive first.");
        if (HasChanges) throw new ArchiveException("The archive has unsaved changes. Save it instead - saving always rebuilds the index.");
        var index = VfsIndex.Build(VfsIndex.Describe(_reader), Platform.IndexIsBigEndian());
        var (binPath, datPath) = VfsIndex.CompanionPaths(FilePath);
        File.WriteAllBytes(binPath + ".tmp", index.Bin);
        File.WriteAllBytes(datPath + ".tmp", index.Dat);
        Commit(binPath + ".tmp", binPath, backups);
        Commit(datPath + ".tmp", datPath, backups);
        return index;
    }

    /// <summary>Compares the .bin/.dat on disk with what the archive on disk requires.</summary>
    public List<string> VerifyIndexOnDisk()
    {
        var report = new List<string>();
        if (_reader == null || FilePath == null) { report.Add("The archive has not been saved yet."); return report; }
        var (binPath, datPath) = VfsIndex.CompanionPaths(FilePath);
        if (!File.Exists(binPath) || !File.Exists(datPath))
        {
            report.Add($"No index found: {Path.GetFileName(binPath)} / {Path.GetFileName(datPath)} are missing. Use VFS > Rebuild Index.");
            return report;
        }
        byte[] bin = File.ReadAllBytes(binPath), dat = File.ReadAllBytes(datPath);
        var expected = VfsIndex.Build(VfsIndex.Describe(_reader), Platform.IndexIsBigEndian());
        if (bin.AsSpan().SequenceEqual(expected.Bin) && dat.AsSpan().SequenceEqual(expected.Dat))
        {
            report.Add($"OK: {Path.GetFileName(binPath)} and {Path.GetFileName(datPath)} match {Path.GetFileName(FilePath)} byte-for-byte ({expected.FileCount:N0} files, {Platform.DisplayName()} format).");
            return report;
        }

        ParsedIndex parsed;
        try { parsed = VfsIndex.Parse(bin, dat); }
        catch (ArchiveException ex) { report.Add("The index on disk is unreadable: " + ex.Message); return report; }

        if (parsed.BigEndian != Platform.IndexIsBigEndian())
            report.Add($"The index on disk is {(parsed.BigEndian ? "big-endian (Xbox 360)" : "little-endian (PC)")} but the platform is set to {Platform.DisplayName()}.");
        report.AddRange(parsed.Problems.Take(10));

        var byOffset = _reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name.ToLowerInvariant());
        int bad = 0, missing = 0;
        foreach (var f in parsed.Files)
        {
            if (!byOffset.Remove(f.Path, out var e)) { missing++; if (missing <= 5) report.Add($"Index lists '{f.Path}' but the archive does not contain it."); continue; }
            uint hv = (uint)(_reader.ReadLocalHeader(e).Length - 30);
            if (f.LocalHeaderOffset != e.LocalHeaderOffset || f.CompressedSize != e.CompressedSize || f.UncompressedSize != e.UncompressedSize || f.Method != e.Method || f.HeaderVariableLength != hv)
            {
                bad++;
                if (bad <= 5) report.Add($"'{f.Path}': the index points at old data (offset {f.LocalHeaderOffset:N0}, archive has {e.LocalHeaderOffset:N0}).");
            }
        }
        foreach (var e in byOffset.Values.Take(5)) report.Add($"'{e.Name}' is in the archive but missing from the index.");
        report.Insert(0, bad + missing + byOffset.Count == 0
            ? "The index is valid for this archive (same files and offsets), but not byte-identical to a fresh build."
            : $"MISMATCH: {bad} stale, {missing} missing from archive, {byOffset.Count} missing from index. The game will crash or load wrong data - use VFS > Rebuild Index.");
        return report;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
    }
}

static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}
