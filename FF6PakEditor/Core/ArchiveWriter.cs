using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FF6PakEditor.Core;

/// <summary>One entry to write: copied verbatim from <see cref="Source"/> unless new data is supplied.</summary>
sealed class WriteItem
{
    public required string Path { get; init; }
    public ZipEntryInfo? Source { get; init; }
    public byte[]? NewData { get; init; }
    public Func<byte[]>? LoadData { get; init; }
    public long SizeHint { get; init; }
}

/// <summary>What ended up in the written archive; exactly the fields the VFS index stores.</summary>
sealed class WrittenEntry
{
    public required string Path { get; init; }
    public bool IsDirectory => Path.EndsWith('/');
    public ushort Flags { get; init; }
    public ushort Method { get; init; }
    public uint Crc32 { get; init; }
    public uint CompressedSize { get; init; }
    public uint UncompressedSize { get; init; }
    public uint HeaderVariableLength { get; init; }
    public uint LocalHeaderOffset { get; init; }
    public uint CdRecordOffset { get; set; }
    public bool Rewritten { get; init; }
}

readonly record struct SaveProgress(string Phase, long Done, long Total, string? Current);

/// <summary>Header values used for entries that have no original to copy from.</summary>
sealed class ArchiveDefaults
{
    public ushort VersionMadeBy { get; init; } = 20;
    public ushort Flags { get; init; } = 2;
    public uint ExternalAttributes { get; init; } = 0x20;

    public static ArchiveDefaults From(ZipReader? reader)
    {
        var files = reader?.Entries.Where(e => !e.IsDirectory).ToList();
        if (files == null || files.Count == 0) return new ArchiveDefaults();
        return new ArchiveDefaults
        {
            VersionMadeBy = MostCommon(files.Select(e => e.VersionMadeBy)),
            Flags = (ushort)(MostCommon(files.Select(e => e.Flags)) & ~0x0809),
            ExternalAttributes = MostCommon(files.Select(e => e.ExternalAttributes)),
        };
    }

    static T MostCommon<T>(IEnumerable<T> values) where T : notnull =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
}

static class ArchiveWriter
{
    const uint SigLocal = 0x04034B50, SigCentral = 0x02014B50, SigEocd = 0x06054B50;

    public static List<WrittenEntry> Write(Stream output, ZipReader? reader, IReadOnlyList<WriteItem> items,
        ArchiveDefaults defaults, byte[] archiveComment, IProgress<SaveProgress>? progress, CancellationToken ct)
    {
        if (items.Count > 0xFFFF) throw new ArchiveException($"Too many entries ({items.Count:N0}) for a non-ZIP64 archive.");

        long total = items.Sum(i => i.NewData?.LongLength ?? (i.LoadData != null ? i.SizeHint : i.Source?.CompressedSize ?? 0));
        long done = 0;
        var written = new List<WrittenEntry>(items.Count);
        var central = new List<byte[]>(items.Count);
        var (dosTime, dosDate) = DosNow();
        byte[] buffer = new byte[1 << 20];
        using var source = reader?.OpenSequential();
        var lastReport = Environment.TickCount64;

        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            long pos = output.Position;
            if (pos > uint.MaxValue) throw new ArchiveException("The archive would exceed 4 GB, which the game's VFS index cannot address.");
            uint localOffset = (uint)pos;
            byte[]? newData = item.NewData ?? item.LoadData?.Invoke();

            if (newData == null)
            {
                var e = item.Source ?? throw new InvalidOperationException($"'{item.Path}' has no data.");
                byte[] lh = reader!.ReadLocalHeader(e);
                byte[] cd = (byte[])e.CdRecord.Clone();
                ushort flags = e.Flags;
                if (e.HasDataDescriptor)
                {
                    lh = (byte[])lh.Clone();
                    flags = (ushort)(flags & ~0x0008);
                    BinaryPrimitives.WriteUInt16LittleEndian(lh.AsSpan(6), flags);
                    BinaryPrimitives.WriteUInt32LittleEndian(lh.AsSpan(14), e.Crc32);
                    BinaryPrimitives.WriteUInt32LittleEndian(lh.AsSpan(18), e.CompressedSize);
                    BinaryPrimitives.WriteUInt32LittleEndian(lh.AsSpan(22), e.UncompressedSize);
                    BinaryPrimitives.WriteUInt16LittleEndian(cd.AsSpan(8), flags);
                }
                output.Write(lh);
                CopyRange(source!, reader.DataOffset(e), e.CompressedSize, output, buffer);
                BinaryPrimitives.WriteUInt32LittleEndian(cd.AsSpan(42), localOffset);
                central.Add(cd);
                written.Add(new WrittenEntry
                {
                    Path = e.Name, Flags = flags, Method = e.Method, Crc32 = e.Crc32,
                    CompressedSize = e.CompressedSize, UncompressedSize = e.UncompressedSize,
                    HeaderVariableLength = (uint)(lh.Length - 30), LocalHeaderOffset = localOffset,
                });
                done += e.CompressedSize;
            }
            else
            {
                byte[] data = newData;
                var src = item.Source;
                ushort method = src?.Method is 0 or 8 ? src.Method : (ushort)8;
                byte[] packed = method == 8 ? Deflate(data) : data;
                if (src == null && method == 8 && packed.Length >= data.Length && data.Length > 0)
                {
                    method = 0;
                    packed = data;
                }

                byte[] name = src?.NameBytes ?? EncodeName(item.Path, out _);
                ushort flags = (ushort)((src?.Flags ?? defaults.Flags) & ~0x0009);
                if (src == null && !IsAscii(item.Path)) flags |= 0x0800;
                byte[] localExtra = src != null ? LocalExtraOf(reader!.ReadLocalHeader(src)) : [];
                byte[] cdExtra = src?.CdExtra ?? [];
                byte[] comment = src?.Comment ?? [];
                ushort versionNeeded = src != null && src.Method == method ? src.VersionNeeded : (ushort)(method == 8 ? 20 : 10);
                uint crc = Crc32.Compute(data);

                var lh = new byte[30 + name.Length + localExtra.Length];
                var l = lh.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(l, SigLocal);
                BinaryPrimitives.WriteUInt16LittleEndian(l[4..], versionNeeded);
                BinaryPrimitives.WriteUInt16LittleEndian(l[6..], flags);
                BinaryPrimitives.WriteUInt16LittleEndian(l[8..], method);
                BinaryPrimitives.WriteUInt16LittleEndian(l[10..], dosTime);
                BinaryPrimitives.WriteUInt16LittleEndian(l[12..], dosDate);
                BinaryPrimitives.WriteUInt32LittleEndian(l[14..], crc);
                BinaryPrimitives.WriteUInt32LittleEndian(l[18..], (uint)packed.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(l[22..], (uint)data.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(l[26..], (ushort)name.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(l[28..], (ushort)localExtra.Length);
                name.CopyTo(lh, 30);
                localExtra.CopyTo(lh, 30 + name.Length);
                output.Write(lh);
                output.Write(packed);

                var cd = new byte[46 + name.Length + cdExtra.Length + comment.Length];
                var c = cd.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(c, SigCentral);
                BinaryPrimitives.WriteUInt16LittleEndian(c[4..], src?.VersionMadeBy ?? defaults.VersionMadeBy);
                BinaryPrimitives.WriteUInt16LittleEndian(c[6..], versionNeeded);
                BinaryPrimitives.WriteUInt16LittleEndian(c[8..], flags);
                BinaryPrimitives.WriteUInt16LittleEndian(c[10..], method);
                BinaryPrimitives.WriteUInt16LittleEndian(c[12..], dosTime);
                BinaryPrimitives.WriteUInt16LittleEndian(c[14..], dosDate);
                BinaryPrimitives.WriteUInt32LittleEndian(c[16..], crc);
                BinaryPrimitives.WriteUInt32LittleEndian(c[20..], (uint)packed.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(c[24..], (uint)data.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(c[28..], (ushort)name.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(c[30..], (ushort)cdExtra.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(c[32..], (ushort)comment.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(c[36..], src?.InternalAttributes ?? 0);
                BinaryPrimitives.WriteUInt32LittleEndian(c[38..], src?.ExternalAttributes ?? defaults.ExternalAttributes);
                BinaryPrimitives.WriteUInt32LittleEndian(c[42..], localOffset);
                name.CopyTo(cd, 46);
                cdExtra.CopyTo(cd, 46 + name.Length);
                comment.CopyTo(cd, 46 + name.Length + cdExtra.Length);
                central.Add(cd);

                written.Add(new WrittenEntry
                {
                    Path = src?.Name ?? item.Path, Flags = flags, Method = method, Crc32 = crc,
                    CompressedSize = (uint)packed.Length, UncompressedSize = (uint)data.Length,
                    HeaderVariableLength = (uint)(name.Length + localExtra.Length), LocalHeaderOffset = localOffset,
                    Rewritten = true,
                });
                done += data.Length;
            }

            if (progress != null && Environment.TickCount64 - lastReport > 100)
            {
                lastReport = Environment.TickCount64;
                progress.Report(new SaveProgress("Writing", done, total, item.Path));
            }
        }

        long cdStart = output.Position;
        for (int i = 0; i < central.Count; i++)
        {
            written[i].CdRecordOffset = (uint)(output.Position - cdStart);
            output.Write(central[i]);
        }
        long cdEnd = output.Position;
        if (cdEnd > uint.MaxValue) throw new ArchiveException("The archive would exceed 4 GB, which the game's VFS index cannot address.");

        var eocd = new byte[22 + archiveComment.Length];
        var s = eocd.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, SigEocd);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], (ushort)central.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], (ushort)central.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)(cdEnd - cdStart));
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)cdStart);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)archiveComment.Length);
        archiveComment.CopyTo(eocd, 22);
        output.Write(eocd);
        progress?.Report(new SaveProgress("Writing", total, total, null));
        return written;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream(data.Length / 2 + 64);
        using (var ds = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            ds.Write(data);
        return ms.ToArray();
    }

    public static byte[] EncodeName(string path, out bool utf8)
    {
        utf8 = !IsAscii(path);
        return (utf8 ? Encoding.UTF8 : Encoding.ASCII).GetBytes(path);
    }

    static bool IsAscii(string s) => s.All(ch => ch < 0x80);

    static byte[] LocalExtraOf(byte[] localHeader)
    {
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(26));
        return localHeader.AsSpan(30 + nameLen).ToArray();
    }

    static void CopyRange(Stream source, long offset, long count, Stream output, byte[] buffer)
    {
        if (source.Position != offset) source.Position = offset;
        while (count > 0)
        {
            int n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new ArchiveException("Unexpected end of file while copying from the source archive.");
            output.Write(buffer, 0, n);
            count -= n;
        }
    }

    static (ushort time, ushort date) DosNow()
    {
        var t = DateTime.Now;
        return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)),
                (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }
}
