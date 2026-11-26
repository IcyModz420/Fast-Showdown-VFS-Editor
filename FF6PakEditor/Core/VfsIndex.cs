using System.Buffers.Binary;
using System.Text;

namespace FF6PakEditor.Core;

sealed record IndexBuild(byte[] Bin, byte[] Dat, int NodeCount, int FileCount, int DirectoryCount, int NameTableSize);

sealed record IndexFileRecord(string Path, int FileIndex, uint Flags, uint Method, uint CompressedSize,
    uint UncompressedSize, uint HeaderVariableLength, uint LocalHeaderOffset, uint CdRecordOffset);

sealed record ParsedIndex(bool BigEndian, int NodeCount, int FileCount, int NameTableSize, List<IndexFileRecord> Files, List<string> Problems);

/// <summary>
/// The game's VFS index (vfs.bin + vfs.dat). Verified byte-for-byte against both shipped games:
/// nodes are numbered in the order they are first created while walking the archive's files in
/// central-directory order, siblings keep that same first-seen order, a file's payload is its
/// position among the archive's files, and the name table de-duplicates lower-cased segment names
/// in node order. The PC build stores everything little-endian, the Xbox 360 build big-endian.
/// </summary>
static class VfsIndex
{
    public const ushort Magic = 0xFFEE, Version = 0x0200, DirectoryPayload = 0x3FFF, NoLink = 0xFFFF;
    public const int MaxFiles = DirectoryPayload;
    public const int MaxNodes = 0xFFFF;
    public const int MaxNameOffset = 0xFFFF;

    sealed class Node
    {
        public string Name = "";
        public bool IsDirectory;
        public int FileIndex = -1;
        public int Index;
        public int FirstChild = -1, NextSibling = -1;
        public Node? LastChild;
        public Dictionary<string, Node>? Children;
    }

    public static IndexBuild Build(IReadOnlyList<WrittenEntry> archiveEntries, bool bigEndian)
    {
        var files = archiveEntries.Where(e => !e.IsDirectory).ToList();
        if (files.Count >= MaxFiles)
            throw new ArchiveException($"Too many files ({files.Count:N0}); the VFS index supports at most {MaxFiles - 1:N0}.");

        var root = new Node { IsDirectory = true };
        var nodes = new List<Node> { root };
        for (int k = 0; k < files.Count; k++)
        {
            string[] parts = files[k].Path.ToLowerInvariant().Split('/');
            var cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                string seg = parts[i];
                bool leaf = i == parts.Length - 1;
                if (seg.Length == 0) throw new ArchiveException($"Invalid path '{files[k].Path}' (empty folder name).");
                cur.Children ??= new Dictionary<string, Node>(StringComparer.Ordinal);
                if (cur.Children.TryGetValue(seg, out var existing))
                {
                    if (leaf || !existing.IsDirectory)
                        throw new ArchiveException($"'{files[k].Path}' conflicts with another entry (paths are case-insensitive).");
                    cur = existing;
                    continue;
                }
                var node = new Node { Name = seg, IsDirectory = !leaf, FileIndex = leaf ? k : -1, Index = nodes.Count };
                nodes.Add(node);
                cur.Children[seg] = node;
                if (cur.LastChild == null) cur.FirstChild = node.Index;
                else cur.LastChild.NextSibling = node.Index;
                cur.LastChild = node;
                cur = node;
            }
        }
        if (nodes.Count > MaxNodes)
            throw new ArchiveException($"Too many files and folders ({nodes.Count:N0} nodes); the VFS index supports at most {MaxNodes:N0}.");

        var names = new List<byte>();
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var nameOffset = new int[nodes.Count];
        for (int i = 1; i < nodes.Count; i++)
        {
            string name = nodes[i].Name;
            if (!offsets.TryGetValue(name, out int off))
            {
                off = names.Count;
                if (off > MaxNameOffset)
                    throw new ArchiveException("The VFS name table is full (more than 64 KB of distinct file and folder names).");
                offsets[name] = off;
                names.AddRange(Encoding.Latin1.GetBytes(name));
                names.Add(0);
            }
            nameOffset[i] = off;
        }

        int nodeBytes = nodes.Count * 8, fileBytes = files.Count * 28;
        var bin = new byte[nodeBytes + fileBytes + names.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            int o = i * 8;
            Put16(bin, o, nameOffset[i], bigEndian);
            Put16(bin, o + 2, n.NextSibling < 0 ? NoLink : n.NextSibling - 1, bigEndian);
            Put16(bin, o + 4, n.FirstChild < 0 ? NoLink : n.FirstChild - 1, bigEndian);
            Put16(bin, o + 6, i == 0 ? 0xFFFF : n.IsDirectory ? DirectoryPayload : n.FileIndex, bigEndian);
        }
        for (int k = 0; k < files.Count; k++)
        {
            var f = files[k];
            int o = nodeBytes + k * 28;
            Put32(bin, o, f.Flags, bigEndian);
            Put32(bin, o + 4, f.Method, bigEndian);
            Put32(bin, o + 8, f.CompressedSize, bigEndian);
            Put32(bin, o + 12, f.UncompressedSize, bigEndian);
            Put32(bin, o + 16, f.HeaderVariableLength, bigEndian);
            Put32(bin, o + 20, f.LocalHeaderOffset, bigEndian);
            Put32(bin, o + 24, f.CdRecordOffset, bigEndian);
        }
        names.CopyTo(bin, nodeBytes + fileBytes);

        var dat = new byte[16];
        Put16(dat, 0, Magic, bigEndian);
        Put16(dat, 2, Version, bigEndian);
        Put32(dat, 4, (uint)(nodes.Count - 1), bigEndian);
        Put32(dat, 8, (uint)files.Count, bigEndian);
        Put32(dat, 12, (uint)names.Count, bigEndian);

        int dirs = nodes.Count(n => n.IsDirectory) - 1;
        return new IndexBuild(bin, dat, nodes.Count - 1, files.Count, dirs, names.Count);
    }

    /// <summary>Describe an archive already on disk the way the writer would have, for index-only rebuilds.</summary>
    public static List<WrittenEntry> Describe(ZipReader reader) =>
        reader.Entries.Select(e => new WrittenEntry
        {
            Path = e.Name, Flags = e.Flags, Method = e.Method, Crc32 = e.Crc32,
            CompressedSize = e.CompressedSize, UncompressedSize = e.UncompressedSize,
            HeaderVariableLength = (uint)(reader.ReadLocalHeader(e).Length - 30),
            LocalHeaderOffset = e.LocalHeaderOffset, CdRecordOffset = e.CdRecordOffset,
        }).ToList();

    /// <summary>true = big-endian (Xbox 360), false = little-endian (PC), null = not a VFS header.</summary>
    public static bool? DetectBigEndian(ReadOnlySpan<byte> dat)
    {
        if (dat.Length < 4) return null;
        if (dat[0] == 0xFF && dat[1] == 0xEE) return true;
        if (dat[0] == 0xEE && dat[1] == 0xFF) return false;
        return null;
    }

    public static (string Bin, string Dat) CompanionPaths(string archivePath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        string baseName = Path.GetFileNameWithoutExtension(archivePath);
        return (Path.Combine(dir, baseName + ".bin"), Path.Combine(dir, baseName + ".dat"));
    }

    public static ParsedIndex Parse(byte[] bin, byte[] dat)
    {
        bool be = DetectBigEndian(dat) ?? throw new ArchiveException("The .dat file is not a VFS header (unknown magic number).");
        if (dat.Length < 16) throw new ArchiveException("The .dat file is too short.");
        uint nodeCount = Get32(dat, 4, be), fileCount = Get32(dat, 8, be), nameSize = Get32(dat, 12, be);
        long nodeBytes = (nodeCount + 1L) * 8, fileBytes = fileCount * 28L;
        if (nodeBytes + fileBytes + nameSize != bin.Length)
            throw new ArchiveException($"The .bin file is {bin.Length:N0} bytes but the .dat header describes {nodeBytes + fileBytes + nameSize:N0}.");

        var problems = new List<string>();
        int nameStart = (int)(nodeBytes + fileBytes);
        string NameAt(int off)
        {
            if (off >= nameSize) { problems.Add($"Name offset {off} is outside the name table."); return "?"; }
            int end = Array.IndexOf(bin, (byte)0, nameStart + off, (int)nameSize - off);
            if (end < 0) { problems.Add($"Name at offset {off} is not terminated."); end = nameStart + (int)nameSize; }
            return Encoding.Latin1.GetString(bin, nameStart + off, end - nameStart - off);
        }
        int Link(int node, int field)
        {
            int v = Get16(bin, node * 8 + field, be);
            return v == NoLink ? -1 : v + 1;
        }

        var files = new List<IndexFileRecord>();
        var visited = new bool[nodeCount + 1];
        var stack = new Stack<(int Node, string Prefix, int Depth)>();
        int first = Link(0, 4);
        if (first >= 0) stack.Push((first, "", 0));
        while (stack.Count > 0)
        {
            var (start, prefix, depth) = stack.Pop();
            for (int n = start; n >= 0; n = Link(n, 2))
            {
                if (n > nodeCount) { problems.Add($"Link to node {n} is outside the tree."); break; }
                if (visited[n]) { problems.Add($"Node {n} is linked more than once (cycle)."); break; }
                visited[n] = true;
                string path = prefix + NameAt(Get16(bin, n * 8, be));
                int payload = Get16(bin, n * 8 + 6, be);
                if (payload == DirectoryPayload)
                {
                    int child = Link(n, 4);
                    if (child >= 0 && depth < 64) stack.Push((child, path + "/", depth + 1));
                }
                else if (payload < fileCount)
                {
                    int o = (int)nodeBytes + payload * 28;
                    files.Add(new IndexFileRecord(path, payload, Get32(bin, o, be), Get32(bin, o + 4, be), Get32(bin, o + 8, be),
                        Get32(bin, o + 12, be), Get32(bin, o + 16, be), Get32(bin, o + 20, be), Get32(bin, o + 24, be)));
                }
                else problems.Add($"'{path}' has an invalid file index {payload}.");
            }
        }
        return new ParsedIndex(be, (int)nodeCount, (int)fileCount, (int)nameSize, files, problems);
    }

    static void Put16(byte[] b, int o, int v, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), (ushort)v);
        else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v);
    }

    static void Put32(byte[] b, int o, uint v, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
        else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    }

    static ushort Get16(byte[] b, int o, bool be) =>
        be ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));

    static uint Get32(byte[] b, int o, bool be) =>
        be ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
}
