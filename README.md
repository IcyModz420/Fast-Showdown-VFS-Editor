# Fast Showdown VFS Editor

Open, edit and rebuild the game archives of **Fast & Furious: Showdown** on **PC, Xbox 360 and 3DS**.

## You no longer need the original vfs.bin or vfs.dat

The game finds its files through two index files that sit next to the archive: `vfs.bin` and `vfs.dat`. Until now, an archive was only usable with the exact index files it shipped with.

This editor rebuilds both index files from the archive alone. Give it a `vfs.pak` or `vfs.zip` and it writes a matching `vfs.bin` and `vfs.dat` next to it, in the right format for the platform. On the retail PC and Xbox 360 releases, the rebuilt files are byte-for-byte identical to the ones that shipped.

**That means many prototype, beta and devkit builds of the game can now run**, including builds that turned up with an archive but no index files, or with index files that no longer match the archive.

### Got a prototype or devkit pack?

One of the packs this was tested on is a never-released 3DS pack, found on a 4 GB devkit emu. There's no telling which type of pack you have or what it was made to run on, so open it anyway. The editor works out the platform from the files inside:

- the byte order of its `vfs.dat`, if there is one;
- otherwise the byte order of its Lua scripts (big-endian on Xbox 360, little-endian on PC and 3DS);
- and 3DS model files (`.bcmdl` and similar) to tell a 3DS pack from a PC one.

The log shows what was detected and why. If it's wrong, pick the right platform in the toolbar.

## Quick start: run an archive that has no index files

1. Back up the game folder.
2. Put the archive where the game expects it.
3. In the editor, choose **File > Open Archive** and pick the archive. The log notes that there is no `vfs.bin` / `vfs.dat` and shows the platform it detected.
4. Check the platform in the toolbar (PC, Xbox 360 or 3DS). It sets the byte order of the index.
5. Choose **VFS Index > Rebuild Index for Archive on Disk**. `vfs.bin` and `vfs.dat` are written next to the archive.
6. Optional: **VFS Index > Verify Index** confirms the index matches the archive.
7. Start the game.

The index files take the archive's name: `vfs.pak` gets `vfs.bin` + `vfs.dat`, and `vfs01.zip` gets `vfs01.bin` + `vfs01.dat`.

## Editing scripts and files

1. Open the archive.
2. Double-click a `.luac` script. It is decompiled into Lua you can edit.
3. Edit it, then press **F5** to compile it back into the archive in that platform's format. **F7** only checks the syntax.
4. Press **Ctrl+S**. The archive, `vfs.bin` and `vfs.dat` are written together and checked before anything is replaced. The first time a file is replaced, the original is kept as a `.bak`.

You can also extract, replace, add and delete any file in the archive, or drag files from Explorer onto the tree.

## Features

- **Index rebuild**: writes `vfs.bin` + `vfs.dat` for any archive in the right byte order for each platform, and verifies every entry before replacing anything.
- **Verify Index**: checks the index files on disk against the archive and lists stale or missing entries.
- **Index Statistics**: shows how close an archive is to the format's limits.
- **Lua decompile, edit and compile**: decompiles `.luac` with unluac, compiles with Lua 5.1 and converts the bytecode for the target platform.
- **Scripts across platforms**: export any script as PC / 3DS or Xbox 360 bytecode. Replacing a `.luac` with a `.lua` file compiles it, and bytecode from another platform is converted.
- **goto rewriting**: Lua 5.1 has no `goto`. Where the decompiler can only express a jump that way, the editor rewrites it into `repeat ... break` blocks marked `--[[goto ...]]`. Review those spots before compiling.
- **Bytecode listing** for any script.
- **New Archive from Folder**: builds a fresh archive and index from a folder of files.
- **Backups**: originals are kept as `.bak` the first time they're replaced, and **VFS Index > Restore Original Backups** puts them back.
- A code editor with Lua highlighting, find and replace, go to line and comment toggling.

## Platforms

| Platform | Archive | Index byte order | Scripts |
|---|---|---|---|
| PC | `vfs.zip`, `vfs01.zip` | little-endian | little-endian Lua 5.1 |
| Xbox 360 | `vfs.pak` | big-endian | big-endian Lua 5.1, shipped without debug info |
| 3DS | `.zip` | little-endian | little-endian Lua 5.1, same format as PC |

All three use Lua 5.1 bytecode with 32-bit float numbers. Xbox 360 scripts have no debug info, so decompiled local variable names are generated.

## Requirements

- Windows 10 or 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Java 8 or newer (for example [Eclipse Temurin](https://adoptium.net/)) to decompile scripts. Everything else works without it.

The Lua 5.1 compiler and the unluac decompiler are included in `FF6PakEditor/tools`.

## Building

You need the .NET 8 SDK or newer.

```
git clone https://github.com/IcyModz420/Fast-Showdown-VFS-Editor.git
cd Fast-Showdown-VFS-Editor
dotnet build -c Release
```

The editor is built to `FF6PakEditor\bin\Release\net8.0-windows\FF6PakEditor.exe`, with its `tools` folder next to it. You can also open `FastShowdownVfsEditor.sln` in Visual Studio 2022 or newer and press F5.

## Good to know

- **The archive and its index are a pair.** `vfs.bin` stores byte offsets into the archive. If you change an archive with any other tool, run **VFS Index > Rebuild Index for Archive on Disk** before starting the game.
- **Back up first.** The editor keeps `.bak` copies, but keep your own copy of the game folder too.
- **Test on an emulator first**, such as Xenia for Xbox 360, before real hardware.
- **Format limits:** up to 16,382 files, 64 KB of distinct file and folder names, and archives under 4 GB. Entries must be Store or Deflate; ZIP64 is not supported.
- Use your own copy of the game. This repository contains no game files, so please don't post game archives or scripts in issues.

## The index format

The archive is an ordinary ZIP file (Store or Deflate). The game doesn't use the ZIP's own directory; it finds files through `vfs.bin` and `vfs.dat`. Both use the platform's byte order: little-endian on PC and 3DS, big-endian on Xbox 360.

**`vfs.dat`** is a 16-byte header:

| Offset | Type | Meaning |
|---:|---|---|
| 0 | u16 | magic `0xFFEE` |
| 2 | u16 | version `0x0200` |
| 4 | u32 | node count, not counting the root |
| 8 | u32 | file count |
| 12 | u32 | name table size in bytes |

**`vfs.bin`** has three parts, back to back:

1. **Path tree**: (node count + 1) nodes of 8 bytes each. Node 0 is the root.

   | Offset | Type | Meaning |
   |---:|---|---|
   | 0 | u16 | name offset in the name table |
   | 2 | u16 | next sibling, stored as node index − 1 (`0xFFFF` = none) |
   | 4 | u16 | first child, stored as node index − 1 (`0xFFFF` = none) |
   | 6 | u16 | `0x3FFF` for a folder, otherwise the file index (`0xFFFF` on the root) |

2. **File records**: one 28-byte record per file, in archive order.

   | Offset | Type | Meaning |
   |---:|---|---|
   | 0 | u32 | ZIP flags |
   | 4 | u32 | method (0 = Store, 8 = Deflate) |
   | 8 | u32 | compressed size |
   | 12 | u32 | uncompressed size |
   | 16 | u32 | length of the local header's name + extra field |
   | 20 | u32 | local header offset in the archive |
   | 24 | u32 | central directory record offset, from the start of the central directory |

3. **Name table**: NUL-terminated, lower-case, de-duplicated path segments. Lookups are case-insensitive.

A file's data starts at `local header offset + 30 + name/extra length`.

To rebuild an index byte-for-byte: number nodes in the order they are first created while walking the archive's files in central directory order, keep siblings in that same order, give each file its position among the archive's files (skipping folder entries), and add names to the table in node order.

## Credits

- Made by [IcyModz420](https://github.com/IcyModz420).
- Includes the [Lua 5.1.5](https://www.lua.org/) compiler (Lua.org, PUC-Rio) and [unluac](https://sourceforge.net/projects/unluac/) 1.2.3 by tehtmi, both under the MIT license. See [`FF6PakEditor/tools/THIRD-PARTY-NOTICES.txt`](FF6PakEditor/tools/THIRD-PARTY-NOTICES.txt).

This is a fan-made modding tool. It is not affiliated with or endorsed by the developers, publishers or rights holders of Fast & Furious: Showdown.
