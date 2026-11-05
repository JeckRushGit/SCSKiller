using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Essence;

/// <summary>Relic's SGA archive, version 10 (Essence 5: Company of Heroes 3). Header: "_ARCHIVE", u16 version (10), u16
/// product, the name (64 UTF-16 chars), then at 0x8C u64 table offset, u32 table size, u64 data offset, u64 data size, u32,
/// and a 256-byte signature up to the data. The table starts with eight u32: drives offset and count, folders offset and
/// count, files offset and count, names offset and size (offsets from the table's start). A folder is 20 bytes (u32 name,
/// first and last subfolder, first and last file: [first, last)), a file 30 bytes: u32 name, u32 (into the table's hash
/// block), u64 offset from the data offset, u32 stored size, u32 size, u8 storage, u8 verification, u32 CRC. Names are
/// NUL-terminated ASCII in the names block; a folder's name is its path from the archive root. Measured in CoH3 (54
/// archives): storage 4 is Brotli (window 22), 0 is stored.</summary>
public static class RelicSga
{
    public sealed record Entry(string Path, long Offset, uint StoredSize, uint Size, byte Storage);

    public sealed record Table(IReadOnlyList<Entry> Entries, byte[] Bytes);

    public const int Version = 10;
    const int HeaderSize = 0xAC, FolderSize = 20, FileSize = 30;
    const uint MaxTable = 64u << 20;

    public static Table Read(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(h);
    }

    /// <summary>The archive's file entries, and the table's raw bytes (a patch changes them: the index hashes them).</summary>
    public static Table Read(SafeFileHandle h)
    {
        var len = RandomAccess.GetLength(h);
        var head = new byte[HeaderSize];
        if (len < HeaderSize || RandomAccess.Read(h, head, 0) != HeaderSize || !head.AsSpan(0, 8).SequenceEqual("_ARCHIVE"u8))
            throw new InvalidDataException("not a Relic SGA archive");
        if (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8)) is var v && v != Version) throw new InvalidDataException($"SGA version {v}, not {Version}");
        var tableAt = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0x8C));
        var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x94));
        var dataAt = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0x98));
        if (tableSize > MaxTable || tableAt < HeaderSize || tableAt + tableSize > len || dataAt < HeaderSize || dataAt > len)
            throw new InvalidDataException("table out of the archive");
        var t = new byte[tableSize];
        if (RandomAccess.Read(h, t, tableAt) != t.Length) throw new InvalidDataException("table cut short");
        if (t.Length < 32) throw new InvalidDataException("table corrupt");
        uint U(int at) => at >= 0 && at + 4 <= t.Length ? BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(at)) : throw new InvalidDataException("table corrupt");
        uint folders = U(8), folderCount = U(12), files = U(16), fileCount = U(20), names = U(24), namesSize = U(28);
        if ((long)folders + (long)folderCount * FolderSize > t.Length || (long)files + (long)fileCount * FileSize > t.Length || (long)names + namesSize > t.Length)
            throw new InvalidDataException("table corrupt");
        string Name(uint off)
        {
            if (off >= namesSize) throw new InvalidDataException("name out of the table");
            var s = (int)(names + off);
            var e = Array.IndexOf(t, (byte)0, s, (int)(names + namesSize) - s);
            if (e < 0) throw new InvalidDataException("name not terminated");
            return Encoding.ASCII.GetString(t, s, e - s);
        }
        var entries = new List<Entry>();
        var seen = new bool[fileCount];
        for (var fo = 0; fo < folderCount; fo++)
        {
            var at = (int)(folders + fo * FolderSize);
            var dir = Name(U(at)).Replace('\\', '/').Trim('/');
            uint first = U(at + 12), last = U(at + 16);
            if (first > last || last > fileCount) throw new InvalidDataException("folder's files out of the table");
            for (var i = first; i < last; i++)
            {
                if (seen[i]) continue;   // listed by two folders: once is enough
                seen[i] = true;
                var f = (int)(files + i * FileSize);
                var name = Name(U(f));
                var offset = dataAt + BinaryPrimitives.ReadInt64LittleEndian(t.AsSpan(f + 8));
                var e = new Entry(dir.Length == 0 ? name : dir + "/" + name, offset, U(f + 16), U(f + 20), t[f + 24]);
                if (e.Offset < dataAt || e.Offset + e.StoredSize > len) throw new InvalidDataException($"{e.Path} runs past the end of the archive");
                entries.Add(e);
            }
        }
        return new Table(entries, t);
    }

    /// <summary>An entry's bytes; null when they aren't stored, Brotli or zlib, or don't come to its size.</summary>
    public static byte[]? Open(SafeFileHandle h, Entry e)
    {
        if (e.Size > 256 << 20 || e.StoredSize > 256 << 20) return null;
        var raw = new byte[e.StoredSize];
        if (RandomAccess.Read(h, raw, e.Offset) != raw.Length) return null;
        if (e.StoredSize == e.Size && e.Storage == 0) return raw;
        var b = new byte[e.Size];
        if (BrotliDecoder.TryDecompress(raw, b, out var written) && written == b.Length) return b;
        try
        {
            using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
            var read = 0;
            while (read < b.Length && z.Read(b, read, b.Length - read) is var r and > 0) read += r;
            return read == b.Length && z.ReadByte() < 0 ? b : null;
        }
        catch (InvalidDataException) { return e.StoredSize == e.Size ? raw : null; }
    }
}
