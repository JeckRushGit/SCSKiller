using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Essence;

/// <summary>Relic's Essence 5 engine (Company of Heroes 3), by the DX12 shader archives each content folder keeps:
/// &lt;folder&gt;\archives\DX12ShadersAndEDFs.sga (CoH3: engine, common and anvil; <see cref="RelicSga"/>). Their
/// <c>.shaderblob</c> entries are Brotli-compressed "Relic Chunky" files, each a DATA SBCN chunk holding one DXBC
/// container (u32 size, the container, 32 bytes) named &lt;material&gt;___&lt;n&gt;___&lt;material&gt;___&lt;stage&gt;shader.
/// Measured in CoH3 (2026-11): 1,965 blobs, 876 distinct containers, all SM 5.0 bytecode, none with a root signature (the
/// engine builds them in code). So the graphics shaders of every archive are one pool (a common vertex shader serves the
/// game's pixel shaders) and each compute shader one IsPipeline map; without a root-signature rule the plan comes from a
/// recording (Planner.CheckD3D12). EngineInfo: Family "Essence", Version "SGA10 DXBC".</summary>
public sealed class EssenceReader : IEngineReader
{
    public const string Family = "Essence", Format = "SGA10";
    const string ArchivePattern = "DX12Shaders*.sga", Blob = ".shaderblob";
    const int IndexVersion = 1, DetectSample = 16;

    public sealed record Loc(string Sga, RelicSga.Entry Entry);

    /// <summary>Where each container is, per game id, from the last Index. ponytail: in memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The DX12 shader archives: &lt;folder&gt;\archives\DX12Shaders*.sga one folder below the install folder,
    /// in path order.</summary>
    internal static List<string> Archives(Game game)
    {
        if (string.IsNullOrEmpty(game.InstallDir) || !Directory.Exists(game.InstallDir)) return [];
        try
        {
            return GameFiles.EnumerateDirectories(game.InstallDir, "*", SearchOption.TopDirectoryOnly)
                .Select(d => GameFiles.Find(d, "archives")).Where(Directory.Exists)
                .SelectMany(d => GameFiles.EnumerateFiles(d, ArchivePattern, SearchOption.TopDirectoryOnly))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static bool IsBlob(RelicSga.Entry e) => e.Path.EndsWith(Blob, StringComparison.OrdinalIgnoreCase);

    static SafeFileHandle Open(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The DXBC containers of a Relic Chunky file's DATA SBCN chunks (FOLD chunks are walked into); none for
    /// anything else.</summary>
    internal static List<byte[]> Containers(byte[]? b)
    {
        var found = new List<byte[]>();
        if (b == null || b.Length < 24 || !b.AsSpan(0, 12).SequenceEqual("Relic Chunky"u8)) return found;
        Chunks(b, 24, b.Length, 0, found);
        return found;
    }

    static void Chunks(byte[] b, int at, int end, int depth, List<byte[]> found)
    {
        while (depth < 16 && at + 20 <= end)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 12));
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 16));
            var body = at + 20L + nameLength;
            if (body + size > end) return;
            var kind = b.AsSpan(at, 8);
            if (kind[..4].SequenceEqual("FOLD"u8)) Chunks(b, (int)body, (int)(body + size), depth + 1, found);
            else if (kind.SequenceEqual("DATASBCN"u8) && size >= 4 + 32)
            {
                var n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)body));
                if (n >= 32 && 4 + n <= size && b.AsSpan((int)body + 4, (int)n) is var c && Dxbc.HeaderSize(c) == c.Length && Dxbc.Valid(c))
                    found.Add(c.ToArray());
            }
            at = (int)(body + size);
        }
    }

    public EngineInfo? Detect(Game game)
    {
        if (Archives(game) is not [_, ..] archives) return null;
        var blobs = new List<(string Sga, RelicSga.Entry Entry)>();
        foreach (var sga in archives)
            try { blobs.AddRange(RelicSga.Read(sga).Entries.Where(IsBlob).Select(e => (sga, e))); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return new EngineInfo(Family, "-", null, "D3D12", false, $"its shader archive {GameFiles.Rel(game.InstallDir, sga)} doesn't open ({e.Message})");
            }
        if (blobs.Count == 0) return new EngineInfo(Family, "-", null, "D3D12", false, "no shaders in its DX12 shader archives");
        // a sample spread over the archives says what they hold; Index reads them all
        int read = 0, dxil = 0, rts0 = 0;
        var take = Math.Min(DetectSample, blobs.Count);
        foreach (var file in Enumerable.Range(0, take).Select(i => blobs[(int)((long)i * blobs.Count / take)]).GroupBy(x => x.Sga))
            try
            {
                using var h = Open(file.Key);
                foreach (var c in file.SelectMany(x => Containers(RelicSga.Open(h, x.Entry))))
                {
                    read++;
                    if (!Dxbc.Part(c, "DXIL"u8).IsEmpty) dxil++;
                    if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) rts0++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (read == 0) return new EngineInfo(Family, "-", null, "D3D12", false, "its shader archives hold no shader SCSKiller can read");
        var version = Format + (dxil == read ? " DXIL" : dxil == 0 ? " DXBC" : " DXIL+DXBC") + (rts0 >= 0.9 * read ? CarvedReader.EmbeddedRootSignatures : "");
        return new EngineInfo(Family, version, null, "D3D12", false, null, Game: game.Id);
    }

    public int ReaderVersion(EngineInfo engine) => 1;

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var archives = Archives(game);
        if (archives.Count == 0) throw new InvalidDataException("no Essence shader archive: scan the game again");

        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var rootSigs = new RootSigCarriers();
        var lanes = new Dictionary<string, string>();   // compute shader -> its [WaveSize] platform
        int blobs = 0, containers = 0, bad = 0, unread = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        content.AppendData(Encoding.UTF8.GetBytes($"{Family} {IndexVersion}\n"));
        var rels = new List<string>();
        foreach (var sga in archives)
        {
            var rel = GameFiles.Rel(game.InstallDir, sga);
            rels.Add(rel);
            var table = RelicSga.Read(sga);
            content.AppendData(Encoding.UTF8.GetBytes(rel + "\n"));
            content.AppendData(table.Bytes);   // names, offsets, sizes and CRCs: a patch changes them, the same on every PC
            using var h = Open(sga);
            foreach (var e in table.Entries.Where(IsBlob).OrderBy(e => e.Offset))
            {
                ct.ThrowIfCancellationRequested();
                blobs++;
                var found = Containers(RelicSga.Open(h, e));
                if (found.Count == 0) { unread++; continue; }
                foreach (var c in found)
                {
                    containers++;
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    if (!locs.TryAdd(sha, new Loc(sga, e))) continue;   // same bytes in another blob
                    var kind = Dxbc.Kind(c);
                    rootSigs.See(c, sha, kind);
                    if (kind < 0) continue;
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is not { } info) continue;
                        shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                        if (info.Stage == Stage.Compute && CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } l) lanes[sha] = l;
                    }
                    catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { bad++; }   // valid container, odd program: not usable
                }
            }
        }
        if (shaders.Count == 0) throw new InvalidDataException($"no shader in {string.Join(", ", rels)}");
        rootSigs.Apply(shaders);

        var library = rels.Count == 1 ? rels[0] : $@"*\archives\{Path.GetFileName(archives[0])}";
        var maps = Maps(library, shaders, s => lanes.GetValueOrDefault(s, CarvedReader.Platform));
        located[game.Id] = locs;

        var rs = shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count();
        log?.Report($"{string.Join(", ", rels)}: {blobs} shader blobs, {containers} containers, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {rs} embedded root signatures, {shaders.Values.Count(s => s.RootSignature == null)} without one -> {maps.Count} maps"
            + (unread > 0 ? $"; warning: {unread} blobs unreadable" : "") + (bad > 0 ? $"; {bad} unparseable" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. lanes.Values.Distinct().Order(StringComparer.Ordinal)],
            shaders, maps, new Dictionary<string, byte[]>(rootSigs.Blobs));
    }

    /// <summary>One pool of graphics shaders per root signature (none: one pool of them all), and one IsPipeline map per
    /// compute shader with its root signature, on the shader's platform.</summary>
    internal static List<ShaderMap> Maps(string library, IReadOnlyDictionary<string, ShaderInfo> shaders, Func<string, string> platform)
    {
        var maps = new List<ShaderMap>();
        foreach (var pool in shaders.Where(s => s.Value.Stage != Stage.Compute).GroupBy(s => s.Value.RootSignature ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
            maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{Family}|{library}|{pool.Key}"), library, CarvedReader.Platform,
                pool.Select(s => s.Key).Order(StringComparer.Ordinal).ToList(), RootSignature: pool.Key.Length == 0 ? null : pool.Key));
        foreach (var (cs, info) in shaders.Where(s => s.Value.Stage == Stage.Compute).OrderBy(s => s.Key, StringComparer.Ordinal))
            maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{cs}|{info.RootSignature}"), library, platform(cs), [cs], IsPipeline: true, info.RootSignature));
        return maps;
    }

    /// <summary>Re-reads each container from the blob the index found it in; one whose bytes changed since (game patched)
    /// is skipped: it fails at replay and is counted there. Root signatures are served by the shaders carrying them.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Sga))
        {
            SafeFileHandle h;
            try { h = Open(file.Key); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }   // gone since the index
            using (h)
                foreach (var blob in file.GroupBy(x => x.At.Entry).OrderBy(g => g.Key.Offset))
                {
                    ct.ThrowIfCancellationRequested();
                    var want = blob.Select(x => x.Sha).ToHashSet();
                    foreach (var c in Containers(RelicSga.Open(h, blob.Key)))
                        if (Convert.ToHexStringLower(SHA1.HashData(c)) is var sha && want.Remove(sha)) sink(sha, c);
                }
        }
    }
}
