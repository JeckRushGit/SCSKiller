using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Essence;

/// <summary>Relic's Essence 5 engine (Company of Heroes 3), by the DX12 shader archives each content folder keeps:
/// &lt;folder&gt;\archives\DX12ShadersAndEDFs.sga (CoH3: engine, common and anvil; <see cref="RelicSga"/>). Their
/// <c>.shaderblob</c> entries are Brotli-compressed "Relic Chunky" files, each a DATA SBCN chunk holding one DXBC
/// container (u32 size, the container, 32 bytes) named &lt;effect&gt;___&lt;n&gt;___&lt;source&gt;___&lt;entry point&gt;.
/// Measured in CoH3 (2026-11): 1,965 blobs, 876 distinct containers, all SM 5.0 bytecode, none carrying a root signature.
///
/// <para>The archives' effects.bin (CoH3: engine's and anvil's) is the engine's effect database (<see cref="EssenceEffects"/>):
/// every technique is a pipeline, its shaders named by blob, and its root signature is rebuilt from its effect, byte for byte
/// as the game creates it. Each technique whose shaders are all in the archives is one IsPipeline map with that root
/// signature, so the game plans without a recording ("+RTS0", Planner.CheckD3D12). A blob name in two archives is the
/// later one's: engine, then common, then the others. Without a readable database the graphics shaders are one pool and
/// each compute shader one map, planned from a recording. EngineInfo: Family "Essence", Version "SGA10 DXBC[+RTS0]".</para></summary>
public sealed class EssenceReader : IEngineReader
{
    public const string Family = "Essence", Format = "SGA10";
    const string ArchivePattern = "DX12Shaders*.sga", Blob = ".shaderblob", EffectsFile = "effects.bin";
    const int IndexVersion = 2, DetectSample = 16;

    public sealed record Loc(string Sga, RelicSga.Entry Entry);

    /// <summary>Where each container is, per game id, from the last Index. ponytail: in memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The root signatures the last Index built, per game id: ReadShaders serves them like shaders (a plan pulls the
    /// root signatures it doesn't carry from the reader).</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>> built = new();

    /// <summary>The DX12 shader archives: &lt;folder&gt;\archives\DX12Shaders*.sga one folder below the install folder,
    /// engine's first, then common's, then the others in path order (a later one's blob of the same name wins).</summary>
    internal static List<string> Archives(Game game)
    {
        if (string.IsNullOrEmpty(game.InstallDir) || !Directory.Exists(game.InstallDir)) return [];
        try
        {
            return GameFiles.EnumerateDirectories(game.InstallDir, "*", SearchOption.TopDirectoryOnly)
                .Select(d => GameFiles.Find(d, "archives")).Where(Directory.Exists)
                .SelectMany(d => GameFiles.EnumerateFiles(d, ArchivePattern, SearchOption.TopDirectoryOnly))
                .OrderBy(Priority).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static int Priority(string sga) => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(sga))) switch
    {
        var d when d?.Equals("engine", StringComparison.OrdinalIgnoreCase) == true => 0,
        var d when d?.Equals("common", StringComparison.OrdinalIgnoreCase) == true => 1,
        _ => 2,
    };

    static bool IsBlob(RelicSga.Entry e) => e.Path.EndsWith(Blob, StringComparison.OrdinalIgnoreCase);

    static bool IsEffects(RelicSga.Entry e) => Path.GetFileName(e.Path).Equals(EffectsFile, StringComparison.OrdinalIgnoreCase);

    static string BlobName(RelicSga.Entry e) => Path.GetFileNameWithoutExtension(e.Path);

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

    /// <summary>The techniques of every archive's effect database; none when an archive has none. InvalidDataException
    /// from one that doesn't read (<see cref="EssenceEffects.Read"/>).</summary>
    static List<EssenceEffects.Technique> Techniques(IEnumerable<(string Sga, RelicSga.Table Table)> archives)
    {
        var list = new List<EssenceEffects.Technique>();
        foreach (var (sga, table) in archives)
            foreach (var e in table.Entries.Where(IsEffects))
            {
                using var h = Open(sga);
                var bytes = RelicSga.Open(h, e) ?? throw new InvalidDataException($"{e.Path} doesn't open");
                list.AddRange(EssenceEffects.Read(bytes).Techniques);
            }
        return list;
    }

    public EngineInfo? Detect(Game game)
    {
        if (Archives(game) is not [_, ..] archives) return null;
        var tables = new List<(string Sga, RelicSga.Table Table)>();
        foreach (var sga in archives)
            try { tables.Add((sga, RelicSga.Read(sga))); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return new EngineInfo(Family, "-", null, "D3D12", false, $"its shader archive {GameFiles.Rel(game.InstallDir, sga)} doesn't open ({e.Message})");
            }
        var blobs = tables.SelectMany(t => t.Table.Entries.Where(IsBlob).Select(e => (t.Sga, Entry: e))).ToList();
        if (blobs.Count == 0) return new EngineInfo(Family, "-", null, "D3D12", false, "no shaders in its DX12 shader archives");
        // a sample spread over the archives says what they hold; Index reads them all
        int read = 0, dxil = 0;
        var take = Math.Min(DetectSample, blobs.Count);
        foreach (var file in Enumerable.Range(0, take).Select(i => blobs[(int)((long)i * blobs.Count / take)]).GroupBy(x => x.Sga))
            try
            {
                using var h = Open(file.Key);
                foreach (var c in file.SelectMany(x => Containers(RelicSga.Open(h, x.Entry))))
                {
                    read++;
                    if (!Dxbc.Part(c, "DXIL"u8).IsEmpty) dxil++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (read == 0) return new EngineInfo(Family, "-", null, "D3D12", false, "its shader archives hold no shader SCSKiller can read");
        // root signatures from the effect database: every pipeline's, as the engine builds it
        var effects = false;
        try { effects = Techniques(tables).Count > 0; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException) { }
        var version = Format + (dxil == read ? " DXIL" : dxil == 0 ? " DXBC" : " DXIL+DXBC") + (effects ? CarvedReader.EmbeddedRootSignatures : "");
        return new EngineInfo(Family, version, null, "D3D12", false, null, Game: game.Id);
    }

    public int ReaderVersion(EngineInfo engine) => 2;

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var archives = Archives(game);
        if (archives.Count == 0) throw new InvalidDataException("no Essence shader archive: scan the game again");

        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // blob name -> container, a later archive's winning
        var rootSigs = new RootSigCarriers();
        var lanes = new Dictionary<string, string>();   // compute shader -> its [WaveSize] platform
        int blobs = 0, containers = 0, bad = 0, unread = 0, overridden = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        content.AppendData(Encoding.UTF8.GetBytes($"{Family} {IndexVersion}\n"));
        var rels = new List<string>();
        var tables = new List<(string Sga, RelicSga.Table Table)>();
        foreach (var sga in archives)
        {
            var rel = GameFiles.Rel(game.InstallDir, sga);
            rels.Add(rel);
            var table = RelicSga.Read(sga);
            tables.Add((sga, table));
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
                    if (byName.TryGetValue(BlobName(e), out var before) && before != sha) overridden++;
                    byName[BlobName(e)] = sha;
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
        string PlatformOf(string sha) => lanes.GetValueOrDefault(sha, CarvedReader.Platform);
        var roots = new Dictionary<string, byte[]>(rootSigs.Blobs);
        List<ShaderMap> maps;
        string how;
        try
        {
            (maps, how) = PipelineMaps(Techniques(tables), byName, shaders, roots, library, PlatformOf);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or RootSig.SerializeException)
        {
            maps = Pools(library, shaders, PlatformOf);
            how = $"effect database not read ({e.Message}): one pool, planned from a recording";
        }
        located[game.Id] = locs;
        built[game.Id] = roots;

        log?.Report($"{string.Join(", ", rels)}: {blobs} shader blobs, {containers} containers, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $") -> {maps.Count} maps; {how}"
            + (overridden > 0 ? $"; {overridden} blob names replaced by a later archive" : "")
            + (unread > 0 ? $"; warning: {unread} blobs unreadable" : "") + (bad > 0 ? $"; {bad} unparseable" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. lanes.Values.Distinct().Order(StringComparer.Ordinal)],
            shaders, maps, roots);
    }

    /// <summary>One IsPipeline map per distinct technique pipeline (its shaders, with the root signature its effect gives
    /// them, added to <paramref name="roots"/>), and what was left out, for the log. A technique naming a blob the archives
    /// don't have, or a shader that didn't parse, is left out; so is one sampling a register its effect has no sampler at.</summary>
    static (List<ShaderMap> Maps, string How) PipelineMaps(List<EssenceEffects.Technique> techniques, Dictionary<string, string> byName,
        Dictionary<string, ShaderInfo> shaders, Dictionary<string, byte[]> roots, string library, Func<string, string> platform)
    {
        if (techniques.Count == 0) throw new InvalidDataException("no techniques");
        var maps = new List<ShaderMap>();
        var seen = new HashSet<string>();
        var serialized = new Dictionary<(EssenceEffects.Layout, string), string?>();
        int missing = 0, noSampler = 0;
        foreach (var t in techniques)
        {
            var shas = new List<string>();
            foreach (var n in t.Shaders)
                if (byName.TryGetValue(n, out var sha) && shaders.ContainsKey(sha)) shas.Add(sha);
            if (shas.Count != t.Shaders.Count) { missing++; continue; }
            var regs = shas.SelectMany(s => shaders[s].Bindings).Where(b => b.Class == "sampler")
                .SelectMany(b => Enumerable.Range(b.Lower, Math.Max(1, b.Count))).Distinct().Order().ToList();
            var key = (t.Layout, string.Join(',', regs));
            if (!serialized.TryGetValue(key, out var rs))
            {
                var blob = t.Layout.RootSignature(regs);
                rs = blob == null ? null : Convert.ToHexStringLower(SHA1.HashData(blob));
                if (blob != null) roots.TryAdd(rs!, blob);
                serialized[key] = rs;
            }
            if (rs == null) { noSampler++; continue; }
            var sorted = shas.Distinct().Order(StringComparer.Ordinal).ToList();
            var hash = CarvedReader.Sha1Hex($"{Family}|{string.Join(',', sorted)}|{rs}");
            if (seen.Add(hash))
                maps.Add(new ShaderMap(hash, library, sorted.Select(platform).FirstOrDefault(p => p != CarvedReader.Platform, CarvedReader.Platform), sorted, IsPipeline: true, rs));
        }
        if (maps.Count == 0) throw new InvalidDataException("no technique's shaders are in the archives");
        var inMaps = maps.SelectMany(m => m.Shaders).ToHashSet();
        return (maps, $"{techniques.Count} techniques -> {maps.Count} pipelines with {maps.Select(m => m.RootSignature).Distinct().Count()} root signatures from the effect database"
            + (missing > 0 ? $"; {missing} techniques name shaders the archives don't have" : "")
            + (noSampler > 0 ? $"; {noSampler} sample where their effect has no sampler" : "")
            + $"; {shaders.Count - shaders.Keys.Count(inMaps.Contains)} shaders in no technique (a recording's PSOs still find them)");
    }

    /// <summary>Without the effect database: one pool of graphics shaders per root signature (none: one pool of them all),
    /// and one IsPipeline map per compute shader with its root signature, on the shader's platform.</summary>
    internal static List<ShaderMap> Pools(string library, IReadOnlyDictionary<string, ShaderInfo> shaders, Func<string, string> platform)
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
    /// is skipped: it fails at replay and is counted there. The root signatures Index built are served from memory.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs) || !built.TryGetValue(game.Id, out var roots))
        {
            Index(game, engine, null, ct);
            (locs, roots) = (located[game.Id], built[game.Id]);
        }
        foreach (var sha in sha1s)
            if (!locs.ContainsKey(sha) && roots.TryGetValue(sha, out var rs)) sink(sha, rs);
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
