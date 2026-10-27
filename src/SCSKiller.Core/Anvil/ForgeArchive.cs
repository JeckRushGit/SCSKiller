using System.Buffers.Binary;
using System.Text;

namespace SCSKiller.Core.Anvil;

/// <summary>
/// Reader for AnvilNext ".forge" archives as shipped by Assassin's Creed Unity (and the same family of
/// Ubisoft titles). A first-draft port of the community reverse-engineering (gentlegiantJGC/ACExplorer,
/// pyUbiForge). It parses the container tables, then on demand decompresses a datafile into its raw
/// resource files. The resource bytes are what SCSKiller's DXBC carver then scans for shaders.
///
/// Format summary (little-endian throughout):
///   Header:   "scimitar" (8 bytes), 1 pad, int32 version (==27), uint64 fileDataHeaderOffset
///             then int64 fileDataOffset at (fileDataHeaderOffset + 36)
///   FileData: int32 indexCount, 4 pad, int64 indexTableOffset, int64 (unused), 8 pad,
///             int64 nameTableOffset, int64 rawDataTableOffset
///   IndexTable: indexCount * 20 bytes  { uint64 rawDataOffset, uint64 fileId, uint32 rawDataSize }
///   NameTable:  indexCount * 192 bytes { ... uint32 fileType @16 ... char[128] fileName @44 ... }
///   Each datafile lives at [rawDataOffset, rawDataOffset + rawDataSize).
///
/// A datafile begins with an 8-byte marker. When it equals <see cref="CompressedMarker"/> the body is one
/// (format 0) or many (format 128) LZO-compressed blocks; otherwise the whole datafile is a single raw file.
///
/// The ONLY external dependency is an LZO decompressor, injected as <see cref="LzoDecompress"/>:
///   mode 0/1 = lzo1x, 2 = lzo2a, 5 = lzo1c. lzo1x is by far the most common; lzo2a/lzo1c need full liblzo2.
/// </summary>
public sealed class ForgeArchive : IDisposable
{
    public const string Magic = "scimitar";
    public const int SupportedVersion = 27;
    public static readonly byte[] CompressedMarker = [0x33, 0xAA, 0xFB, 0x57, 0x99, 0xFA, 0x04, 0x10];

    /// <summary>Decompresses one LZO block. <paramref name="mode"/> picks the LZO variant (see class remarks).
    /// Must return exactly <paramref name="dstLen"/> bytes.</summary>
    public delegate byte[] LzoDecompress(int mode, ReadOnlySpan<byte> src, int dstLen);

    public sealed record DataFile(ulong FileId, long RawDataOffset, int RawDataSize, string Name, uint FileType);

    /// <summary>One resource extracted from a datafile. <see cref="Data"/> is the raw resource bytes
    /// (a shader, mesh, texture, ...). Shaders are found by scanning <see cref="Data"/> for the DXBC magic.</summary>
    public sealed record Resource(ulong FileId, uint FileType, string Name, byte[] Data);

    readonly FileStream _stream;
    readonly LzoDecompress _lzo;
    public IReadOnlyList<DataFile> DataFiles { get; }

    ForgeArchive(FileStream stream, LzoDecompress lzo, IReadOnlyList<DataFile> dataFiles)
    {
        _stream = stream;
        _lzo = lzo;
        DataFiles = dataFiles;
    }

    /// <summary>Opens a .forge file and parses its tables. Returns null when the file is not a supported
    /// "scimitar" v27 archive (so a caller can skip it quietly).</summary>
    public static ForgeArchive? Open(string path, LzoDecompress lzo)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            Span<byte> head = stackalloc byte[8];
            ReadExact(stream, head);
            if (!head.SequenceEqual(Encoding.ASCII.GetBytes(Magic))) { stream.Dispose(); return null; }

            stream.Seek(1, SeekOrigin.Current);                 // 1 pad byte
            int version = ReadI32(stream);
            ulong fileDataHeaderOffset = ReadU64(stream);
            if (version != SupportedVersion) { stream.Dispose(); return null; }

            stream.Seek((long)fileDataHeaderOffset + 36, SeekOrigin.Begin);
            long fileDataOffset = ReadI64(stream);

            stream.Seek(fileDataOffset, SeekOrigin.Begin);
            int indexCount = ReadI32(stream);
            stream.Seek(4, SeekOrigin.Current);                 // pad
            long indexTableOffset = ReadI64(stream);
            _ = ReadI64(stream);                                // fileDataOffset2 (unused)
            stream.Seek(8, SeekOrigin.Current);                 // pad
            long nameTableOffset = ReadI64(stream);
            _ = ReadI64(stream);                                // rawDataTableOffset (unused here)

            // Index table: fixed-size records, read in bulk.
            var indexBytes = new byte[20 * indexCount];
            stream.Seek(indexTableOffset, SeekOrigin.Begin);
            ReadExact(stream, indexBytes);

            var nameBytes = new byte[192 * indexCount];
            stream.Seek(nameTableOffset, SeekOrigin.Begin);
            ReadExact(stream, nameBytes);

            var files = new List<DataFile>(indexCount);
            for (int n = 0; n < indexCount; n++)
            {
                var idx = indexBytes.AsSpan(n * 20, 20);
                ulong rawDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(idx);
                ulong fileId = BinaryPrimitives.ReadUInt64LittleEndian(idx[8..]);
                uint rawDataSize = BinaryPrimitives.ReadUInt32LittleEndian(idx[16..]);

                var nm = nameBytes.AsSpan(n * 192, 192);
                uint fileType = BinaryPrimitives.ReadUInt32LittleEndian(nm[16..]);
                string name = ReadCString(nm.Slice(44, 128));

                files.Add(new DataFile(fileId, (long)rawDataOffset, (int)rawDataSize, name, fileType));
            }

            return new ForgeArchive(stream, lzo, files);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Decompresses one datafile into its raw resource files. Datafile id 0 and ids above 2^40 are
    /// sentinels in the container and yield nothing.</summary>
    public IReadOnlyList<Resource> ReadResources(DataFile df)
    {
        if (df.FileId == 0 || df.FileId > (1UL << 40)) return [];

        var chunk = new byte[df.RawDataSize];
        _stream.Seek(df.RawDataOffset, SeekOrigin.Begin);
        ReadExact(_stream, chunk);

        var r = new SpanReader(chunk);
        var marker = r.Read(8);

        if (!marker.SequenceEqual(CompressedMarker))
        {
            // Not compressed: the whole datafile is a single raw file. (Guard against a misparse.)
            if (IndexOf(chunk, CompressedMarker) >= 0)
                throw new InvalidDataException($"{df.Name}: unexpected compressed marker in a datafile read as raw");
            return [new Resource(df.FileId, df.FileType, df.Name, chunk)];
        }

        var (formatVersion, blocks) = ReadCompressedSection(ref r);
        if (formatVersion == 128 && r.Remaining >= 8 && r.Peek(8).SequenceEqual(CompressedMarker))
        {
            r.Skip(8);
            var (_, more) = ReadCompressedSection(ref r);
            blocks.AddRange(more);
        }

        var decompressed = Concat(blocks);

        return formatVersion switch
        {
            0 => [new Resource(df.FileId, df.FileType, df.Name, decompressed)],
            128 => ParseResourceContainer(df, decompressed),
            _ => throw new InvalidDataException($"{df.Name}: unknown format version {formatVersion}"),
        };
    }

    /// <summary>Reads one compressed section: a small header selecting the LZO mode and the block layout,
    /// then the LZO blocks themselves. Returns the format version and the decompressed blocks in order.</summary>
    (int FormatVersion, List<byte[]> Blocks) ReadCompressedSection(ref SpanReader r)
    {
        r.Skip(2);
        int compressionType = r.ReadU8();       // LZO mode
        r.Skip(3);
        int formatVersion = r.ReadU8();
        var blocks = new List<byte[]>();

        if (formatVersion == 0)
        {
            // Rare single-block layout: a count byte (only 1 is known), then (compressed, uncompressed) u32 pair.
            int count = r.ReadU8();
            if (count != 1) throw new InvalidDataException($"format 0 block count {count} not supported");
            for (int i = 0; i < count; i++)
            {
                int compressed = (int)r.ReadU32();
                int uncompressed = (int)r.ReadU32();
                r.Skip(4);                       // per-block hash
                blocks.Add(Decompress(compressionType, r.Read(compressed), uncompressed));
            }
        }
        else if (formatVersion == 128)
        {
            // Common layout: u32 block count, then per block a (uncompressed u16, compressed u16) size pair.
            int count = (int)r.ReadU32();
            Span<(int unc, int comp)> sizes = count <= 256 ? stackalloc (int, int)[count] : new (int, int)[count];
            for (int i = 0; i < count; i++) sizes[i] = (r.ReadU16(), r.ReadU16());
            for (int i = 0; i < count; i++)
            {
                r.Skip(4);                       // per-block hash
                blocks.Add(Decompress(compressionType, r.Read(sizes[i].comp), sizes[i].unc));
            }
        }
        else throw new InvalidDataException($"unknown format version {formatVersion}");

        return (formatVersion, blocks);
    }

    byte[] Decompress(int mode, ReadOnlySpan<byte> src, int dstLen)
    {
        if (src.Length == dstLen) return src.ToArray();      // stored block, no compression
        var dst = _lzo(mode, src, dstLen);
        if (dst.Length != dstLen) throw new InvalidDataException($"LZO mode {mode}: produced {dst.Length}, expected {dstLen}");
        return dst;
    }

    /// <summary>Parses the format-128 inner container: a file count, an index table, then each resource's
    /// header and raw bytes.</summary>
    static List<Resource> ParseResourceContainer(DataFile df, byte[] data)
    {
        var r = new SpanReader(data);
        int fileCount = r.ReadU16();

        Span<ulong> ids = fileCount <= 256 ? stackalloc ulong[fileCount] : new ulong[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            ids[i] = r.ReadU64();                // file_id
            _ = r.ReadU32();                     // data_size (file_size + header)
            int extra16 = r.ReadU16();           // extra16_count
            r.Skip(extra16 * 2);
        }

        var resources = new List<Resource>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            uint fileType = r.ReadU32();
            int fileSize = (int)r.ReadU32();
            int nameSize = (int)r.ReadU32();
            string name = Encoding.UTF8.GetString(r.Read(nameSize));

            int check = r.ReadU8();
            if (check == 1)
            {
                r.Skip(3);
                int unk = (int)r.ReadU32();
                r.Skip(12 * unk);
            }
            else if (check != 0) throw new InvalidDataException($"{df.Name}: unexpected check byte {check}");

            var raw = r.Read(fileSize).ToArray();
            if (name.Length == 0) name = ids[i].ToString("X16");
            resources.Add(new Resource(ids[i], fileType, name, raw));
        }
        return resources;
    }

    public void Dispose() => _stream.Dispose();

    // --- small helpers -------------------------------------------------------

    static byte[] Concat(List<byte[]> parts)
    {
        long total = 0;
        foreach (var p in parts) total += p.Length;
        var outBuf = new byte[total];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(outBuf, o); o += p.Length; }
        return outBuf;
    }

    static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) => haystack.IndexOf(needle);

    static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    static void ReadExact(Stream s, Span<byte> buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf[read..]);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    static int ReadI32(Stream s) { Span<byte> b = stackalloc byte[4]; ReadExact(s, b); return BinaryPrimitives.ReadInt32LittleEndian(b); }
    static long ReadI64(Stream s) { Span<byte> b = stackalloc byte[8]; ReadExact(s, b); return BinaryPrimitives.ReadInt64LittleEndian(b); }
    static ulong ReadU64(Stream s) { Span<byte> b = stackalloc byte[8]; ReadExact(s, b); return BinaryPrimitives.ReadUInt64LittleEndian(b); }

    /// <summary>A forward-only reader over an in-memory buffer, little-endian.</summary>
    ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _d = data;
        int _p = 0;

        public readonly int Remaining => _d.Length - _p;
        public void Skip(int n) => _p += n;
        public readonly ReadOnlySpan<byte> Peek(int n) => _d.Slice(_p, n);
        public ReadOnlySpan<byte> Read(int n) { var s = _d.Slice(_p, n); _p += n; return s; }
        public byte ReadU8() => _d[_p++];
        public ushort ReadU16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[_p..]); _p += 2; return v; }
        public uint ReadU32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_p..]); _p += 4; return v; }
        public ulong ReadU64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_d[_p..]); _p += 8; return v; }
    }
}
