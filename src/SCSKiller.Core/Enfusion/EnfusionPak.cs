using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Enfusion;

/// <summary>Bohemia's Enfusion archive (.pak): an IFF form, "FORM" u32 BE size "PAC1", then chunks of a 4-byte tag and a
/// u32 BE size: HEAD (28 bytes), DATA (the entries' bytes back to back), FILE (the table). The table is a tree of nodes, each
/// u8 kind (0 folder, 1 file), u8 name length, the name (Latin-1); a folder then a u32 LE child count and its children, a
/// file then 24 bytes LE: u32 offset (from the start of the archive), u32 stored size, u32 size, u32, u16, u16 compression
/// (0x0601 zlib, measured; 0 stored), u32 time. A stored size equal to the size is stored as is (Arma Reforger: every
/// shader zlib, its version.txt stored).</summary>
public static class EnfusionPak
{
    public sealed record Entry(string Path, uint Offset, uint StoredSize, uint Size, ushort Compression);

    public sealed record Table(IReadOnlyList<Entry> Entries, byte[] Bytes);

    const int MaxNameDepth = 64;

    /// <summary>The archive's table, and its raw bytes (a patch changes them: the index hashes them).</summary>
    public static Table Read(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(h);
    }

    public static Table Read(SafeFileHandle h)
    {
        var len = RandomAccess.GetLength(h);
        var head = new byte[12];
        if (len < 12 || RandomAccess.Read(h, head, 0) != 12 || !head.AsSpan(0, 4).SequenceEqual("FORM"u8) || !head.AsSpan(8, 4).SequenceEqual("PAC1"u8))
            throw new InvalidDataException("not an Enfusion PAC1 archive");
        var chunk = new byte[8];
        for (long p = 12; p + 8 <= len;)
        {
            if (RandomAccess.Read(h, chunk, p) != 8) break;
            var size = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(4));
            if (p + 8 + size > len) throw new InvalidDataException($"chunk {Encoding.ASCII.GetString(chunk, 0, 4)} runs past the end");
            if (chunk.AsSpan(0, 4).SequenceEqual("FILE"u8))
            {
                if (size > 64 << 20) throw new InvalidDataException("file table too large");
                var table = new byte[size];
                if (RandomAccess.Read(h, table, p + 8) != table.Length) throw new InvalidDataException("file table cut short");
                var entries = new List<Entry>();
                var at = 0;
                Node(table, ref at, "", 0, entries, len);
                return new Table(entries, table);
            }
            p += 8 + size;
        }
        throw new InvalidDataException("no file table");
    }

    static void Node(byte[] t, ref int at, string dir, int depth, List<Entry> entries, long archiveLength)
    {
        if (depth > MaxNameDepth || at + 2 > t.Length) throw new InvalidDataException("file table corrupt");
        int kind = t[at], n = t[at + 1];
        if (at + 2 + n > t.Length) throw new InvalidDataException("file table corrupt");
        var name = Encoding.Latin1.GetString(t, at + 2, n);
        at += 2 + n;
        if (kind == 0)
        {
            if (at + 4 > t.Length) throw new InvalidDataException("file table corrupt");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(at));
            at += 4;
            var path = name.Length == 0 ? dir : dir + name + "/";
            for (var i = 0u; i < count; i++) Node(t, ref at, path, depth + 1, entries, archiveLength);
            return;
        }
        if (kind != 1 || at + 24 > t.Length) throw new InvalidDataException("file table corrupt");
        var s = t.AsSpan(at);
        var e = new Entry(dir + name, BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), BinaryPrimitives.ReadUInt16LittleEndian(s[18..]));
        at += 24;
        if (e.Offset + (long)e.StoredSize > archiveLength) throw new InvalidDataException($"{e.Path} runs past the end of the archive");
        entries.Add(e);
    }

    /// <summary>An entry's bytes; null when it isn't stored or zlib, or doesn't inflate to its size.</summary>
    public static byte[]? Open(SafeFileHandle h, Entry e)
    {
        if (e.Size > 256 << 20 || e.StoredSize > 256 << 20) return null;
        var raw = new byte[e.StoredSize];
        if (RandomAccess.Read(h, raw, e.Offset) != raw.Length) return null;
        if (e.StoredSize == e.Size) return raw;
        try
        {
            using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
            var b = new byte[e.Size];
            var read = 0;
            while (read < b.Length && z.Read(b, read, b.Length - read) is var r and > 0) read += r;
            return read == b.Length && z.ReadByte() < 0 ? b : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
