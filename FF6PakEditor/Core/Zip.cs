using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FF6PakEditor.Core;

sealed class ArchiveException(string message) : Exception(message);

static class Crc32
{
    static readonly uint[] Table = CreateTable();

    static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}

/// <summary>One central-directory record of a ZIP archive, kept verbatim so it can be copied back unchanged.</summary>
sealed class ZipEntryInfo
{
    public int CdIndex { get; init; }
    public required byte[] NameBytes { get; init; }
    public required string Name { get; init; }
    public ushort VersionMadeBy { get; init; }
    public ushort VersionNeeded { get; init; }
    public ushort Flags { get; init; }
    public ushort Method { get; init; }
    public ushort ModTime { get; init; }
    public ushort ModDate { get; init; }
    public uint Crc32 { get; init; }
    public uint CompressedSize { get; init; }
    public uint UncompressedSize { get; init; }
    public ushort InternalAttributes { get; init; }
    public uint ExternalAttributes { get; init; }
    public uint LocalHeaderOffset { get; init; }
    public uint CdRecordOffset { get; init; }
    public required byte[] CdRecord { get; init; }
    public required byte[] CdExtra { get; init; }
    public required byte[] Comment { get; init; }

    internal byte[]? LocalHeader;

    public bool IsDirectory => Name.EndsWith('/');
    public bool IsEncrypted => (Flags & 0x0001) != 0;
    public bool HasDataDescriptor => (Flags & 0x0008) != 0;
    public bool CanRead => !IsEncrypted && (Method == 0 || Method == 8);
}

/// <summary>
/// Minimal random-access ZIP reader. Unlike System.IO.Compression it never rewrites anything,
/// exposes the raw records the game's VFS index refers to, and reads entry data lazily.
/// </summary>
sealed class ZipReader : IDisposable
{
    const uint SigLocal = 0x04034B50, SigCentral = 0x02014B50, SigEocd = 0x06054B50, SigZip64Locator = 0x07064B50;
    const int MaxEntrySize = 1 << 30;

    readonly FileStream _fs;
    readonly object _gate = new();

    public string FilePath { get; }
    public long Length { get; }
    public IReadOnlyList<ZipEntryInfo> Entries { get; }
    public byte[] Comment { get; }

    ZipReader(string path, FileStream fs, IReadOnlyList<ZipEntryInfo> entries, byte[] comment)
    {
        FilePath = path;
        _fs = fs;
        Length = fs.Length;
        Entries = entries;
        Comment = comment;
    }

    public static ZipReader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.RandomAccess);
        try
        {
            var (entries, comment) = ReadCentralDirectory(fs);
            return new ZipReader(Path.GetFullPath(path), fs, entries, comment);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    static (List<ZipEntryInfo>, byte[]) ReadCentralDirectory(FileStream fs)
    {
        long len = fs.Length;
        if (len < 22) throw new ArchiveException("The file is too small to be a ZIP archive.");
        if (len > uint.MaxValue) throw new ArchiveException("The archive is larger than 4 GB, which the game's VFS index cannot address.");

        int window = (int)Math.Min(len, 22 + ushort.MaxValue);
        byte[] tail = ReadExact(fs, len - window, window);
        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != SigEocd) continue;
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (i + 22 + commentLen == tail.Length) { eocd = i; break; }
            if (eocd < 0) eocd = i;
        }
        if (eocd < 0) throw new ArchiveException("This is not a ZIP archive (no end-of-central-directory record was found).");

        var e = tail.AsSpan(eocd);
        ushort disk = U16(e, 4), cdDisk = U16(e, 6), countThisDisk = U16(e, 8), count = U16(e, 10);
        uint cdSize = U32(e, 12), cdOffset = U32(e, 16);
        int archiveCommentLen = Math.Min((int)U16(e, 20), tail.Length - eocd - 22);
        byte[] comment = e.Slice(22, archiveCommentLen).ToArray();

        if (disk != 0 || cdDisk != 0 || countThisDisk != count)
            throw new ArchiveException("Split (multi-part) archives are not supported.");
        if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF ||
            (eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) == SigZip64Locator))
            throw new ArchiveException("ZIP64 archives are not supported by the game's VFS format.");
        if ((long)cdOffset + cdSize > len)
            throw new ArchiveException("The archive's central directory points past the end of the file (truncated or corrupt).");

        byte[] cd = ReadExact(fs, cdOffset, (int)cdSize);
        var list = new List<ZipEntryInfo>(count);
        int p = 0;
        for (int n = 0; n < count; n++)
        {
            if (p + 46 > cd.Length || BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) != SigCentral)
                throw new ArchiveException($"The central directory is corrupt at record {n}.");
            var r = cd.AsSpan(p);
            int nameLen = U16(r, 28), extraLen = U16(r, 30), commentLen = U16(r, 32);
            int recLen = 46 + nameLen + extraLen + commentLen;
            if (p + recLen > cd.Length) throw new ArchiveException($"The central directory is corrupt at record {n}.");

            ushort flags = U16(r, 8);
            byte[] nameBytes = r.Slice(46, nameLen).ToArray();
            string name = ((flags & 0x0800) != 0 ? Encoding.UTF8 : Encoding.Latin1).GetString(nameBytes).Replace('\\', '/');
            uint comp = U32(r, 20), unc = U32(r, 24), loc = U32(r, 42);
            if (comp == 0xFFFFFFFF || unc == 0xFFFFFFFF || loc == 0xFFFFFFFF)
                throw new ArchiveException($"'{name}' uses ZIP64 sizes, which the game's VFS format cannot represent.");
            if ((long)loc + 30 > len)
                throw new ArchiveException($"'{name}' points past the end of the archive.");

            list.Add(new ZipEntryInfo
            {
                CdIndex = n,
                NameBytes = nameBytes,
                Name = name,
                VersionMadeBy = U16(r, 4),
                VersionNeeded = U16(r, 6),
                Flags = flags,
                Method = U16(r, 10),
                ModTime = U16(r, 12),
                ModDate = U16(r, 14),
                Crc32 = U32(r, 16),
                CompressedSize = comp,
                UncompressedSize = unc,
                InternalAttributes = U16(r, 36),
                ExternalAttributes = U32(r, 38),
                LocalHeaderOffset = loc,
                CdRecordOffset = (uint)p,
                CdRecord = r.Slice(0, recLen).ToArray(),
                CdExtra = r.Slice(46 + nameLen, extraLen).ToArray(),
                Comment = r.Slice(46 + nameLen + extraLen, commentLen).ToArray(),
            });
            p += recLen;
        }
        return (list, comment);
    }

    /// <summary>Raw local file header (30 fixed bytes + name + extra), cached.</summary>
    public byte[] ReadLocalHeader(ZipEntryInfo e)
    {
        if (e.LocalHeader != null) return e.LocalHeader;
        lock (_gate)
        {
            byte[] head = ReadExact(_fs, e.LocalHeaderOffset, 30);
            if (BinaryPrimitives.ReadUInt32LittleEndian(head) != SigLocal)
                throw new ArchiveException($"'{e.Name}': no local file header at offset {e.LocalHeaderOffset} (the archive is corrupt).");
            int varLen = U16(head, 26) + U16(head, 28);
            byte[] full = new byte[30 + varLen];
            head.CopyTo(full, 0);
            if (varLen > 0) ReadExact(_fs, e.LocalHeaderOffset + 30, varLen).CopyTo(full, 30);
            e.LocalHeader = full;
            return full;
        }
    }

    public long DataOffset(ZipEntryInfo e) => e.LocalHeaderOffset + (long)ReadLocalHeader(e).Length;

    public byte[] ReadCompressed(ZipEntryInfo e)
    {
        long off = DataOffset(e);
        if (off + e.CompressedSize > Length)
            throw new ArchiveException($"'{e.Name}' extends past the end of the archive.");
        if (e.CompressedSize > MaxEntrySize)
            throw new ArchiveException($"'{e.Name}' is too large to load ({e.CompressedSize:N0} bytes).");
        lock (_gate) return ReadExact(_fs, off, (int)e.CompressedSize);
    }

    public byte[] ReadData(ZipEntryInfo e)
    {
        if (e.IsEncrypted) throw new ArchiveException($"'{e.Name}' is encrypted and cannot be read.");
        if (e.UncompressedSize > MaxEntrySize) throw new ArchiveException($"'{e.Name}' is too large to load ({e.UncompressedSize:N0} bytes).");
        byte[] raw = ReadCompressed(e);
        switch (e.Method)
        {
            case 0:
                return raw;
            case 8:
                var output = new byte[e.UncompressedSize];
                using (var ds = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress))
                {
                    int got = 0;
                    while (got < output.Length)
                    {
                        int n = ds.Read(output, got, output.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got != output.Length)
                        throw new ArchiveException($"'{e.Name}' is corrupt (decompressed {got:N0} of {output.Length:N0} bytes).");
                }
                return output;
            default:
                throw new ArchiveException($"'{e.Name}' uses compression method {e.Method}, which is not supported.");
        }
    }

    public FileStream OpenSequential() =>
        new(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);

    public void Dispose() => _fs.Dispose();

    internal static byte[] ReadExact(Stream s, long pos, int count)
    {
        var buffer = new byte[count];
        s.Position = pos;
        try { s.ReadExactly(buffer); }
        catch (EndOfStreamException) { throw new ArchiveException("Unexpected end of file (the archive is truncated or corrupt)."); }
        return buffer;
    }

    static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
}
