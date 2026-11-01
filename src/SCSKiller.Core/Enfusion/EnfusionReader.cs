using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Enfusion;

/// <summary>Bohemia's Enfusion engine (Arma Reforger), by addons\core\shaders-dx12.pak (<see cref="EnfusionPak"/>). Its
/// <c>system/.shadercacheDX12/</c> entries are DXIL containers, zlib-compressed, one per permutation, named
/// <c>&lt;stage&gt;_&lt;shader&gt;[$DEFINE$value...].&lt;vso|pso|cso&gt;</c>. Each carries its root signature (RTS0), and the
/// engine uses a handful (measured in Reforger 1.x: 3, one for nearly every graphics shader, one for compute, one for water),
/// so a pipeline's shaders share one: graphics shaders are pooled per root signature (paired by linkage, as the carver pools
/// a file), each compute shader is one IsPipeline map, on its [WaveSize] platform (<see cref="CarvedReader.LanePlatform"/>).
/// Only files are read: the game has BattlEye, so the recorder stays out (<see cref="GameFiles"/>).
/// EngineInfo: Family "Enfusion", Version "PAC1 DXIL+RTS0".</summary>
public sealed class EnfusionReader : IEngineReader
{
    public const string Family = "Enfusion", Format = "PAC1 DXIL";
    public const string ShaderPak = @"addons\core\shaders-dx12.pak", ShaderDir = "system/.shadercacheDX12/";
    const int IndexVersion = 1, DetectSample = 32;

    public sealed record Loc(string Pak, EnfusionPak.Entry Entry);

    /// <summary>Where each container is, per game id, from the last Index. ponytail: in memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The shader archive: under the install folder, else beside the exe.</summary>
    static string? Pak(Game game) =>
        new[] { game.InstallDir, Path.GetDirectoryName(game.ExePath) }
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => GameFiles.Find(d!, ShaderPak)).FirstOrDefault(File.Exists);

    static bool IsShader(string p) => p.StartsWith(ShaderDir, StringComparison.OrdinalIgnoreCase)
        && (p.EndsWith(".vso", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".pso", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cso", StringComparison.OrdinalIgnoreCase));

    static SafeFileHandle Open(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>A valid container, null for anything else.</summary>
    static byte[]? Container(byte[]? b) => b != null && Dxbc.HeaderSize(b) == b.Length && Dxbc.Valid(b) ? b : null;

    public EngineInfo? Detect(Game game)
    {
        if (Pak(game) is not { } path) return null;
        EnfusionPak.Table table;
        try { table = EnfusionPak.Read(path); }
        catch (InvalidDataException e) { return new EngineInfo(Family, "-", null, "D3D12", false, $"its shader archive doesn't open ({e.Message})"); }
        var shaders = table.Entries.Where(e => IsShader(e.Path)).ToList();
        if (shaders.Count == 0) return new EngineInfo(Family, "-", null, "D3D12", false, "no D3D12 shaders in its shader archive");
        // a sample spread over the archive says what it holds; Index reads them all
        int read = 0, dxil = 0, rts0 = 0;
        using (var h = Open(path))
            foreach (var e in Enumerable.Range(0, Math.Min(DetectSample, shaders.Count)).Select(i => shaders[(int)((long)i * shaders.Count / Math.Min(DetectSample, shaders.Count))]))
            {
                if (Container(EnfusionPak.Open(h, e)) is not { } c) continue;
                read++;
                if (!Dxbc.Part(c, "DXIL"u8).IsEmpty) dxil++;
                if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) rts0++;
            }
        if (read == 0) return new EngineInfo(Family, "-", null, "D3D12", false, "its shader archive holds no shader SCSKiller can read");
        var version = (dxil == read ? Format : "PAC1 DXBC") + (rts0 >= 0.9 * read ? CarvedReader.EmbeddedRootSignatures : "");
        return new EngineInfo(Family, version, null, "D3D12", false, null);
    }

    public int ReaderVersion(EngineInfo engine) => 1;

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Index(game, engine, log, null, ct);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var path = Pak(game) ?? throw new InvalidDataException("no Enfusion shader archive: scan the game again");
        var table = EnfusionPak.Read(path);
        var entries = table.Entries.Where(e => IsShader(e.Path)).OrderBy(e => e.Offset).ToList();

        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var rootSigs = new RootSigCarriers();
        var lanes = new Dictionary<string, string>();   // compute shader -> its [WaveSize] platform
        int containers = 0, bad = 0, unread = 0, done = 0;
        using (var h = Open(path))
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((++done, entries.Count));
                if (Container(EnfusionPak.Open(h, e)) is not { } c) { unread++; continue; }
                containers++;
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (!locs.TryAdd(sha, new Loc(path, e))) continue;   // same bytes in another entry
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
        if (shaders.Count == 0) throw new InvalidDataException($"no shader in {GameFiles.Rel(game.InstallDir, path)}");
        rootSigs.Apply(shaders);

        var rel = GameFiles.Rel(game.InstallDir, path);
        var maps = Maps(rel, shaders, h => lanes.GetValueOrDefault(h, CarvedReader.Platform));
        located[game.Id] = locs;

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        content.AppendData(Encoding.UTF8.GetBytes($"{Family} {IndexVersion}\n"));
        content.AppendData(table.Bytes);   // names, offsets, sizes and times: a patch changes them, the same on every PC

        var rs = shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count();
        log?.Report($"{rel}: {containers} shader containers, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {rs} embedded root signatures, {shaders.Values.Count(s => s.RootSignature == null)} without one -> {maps.Count} maps"
            + (unread > 0 ? $"; warning: {unread} entries unreadable" : "") + (bad > 0 ? $"; {bad} unparseable" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. lanes.Values.Distinct().Order(StringComparer.Ordinal)],
            shaders, maps, new Dictionary<string, byte[]>(rootSigs.Blobs));
    }

    /// <summary>One pooled map of graphics shaders per root signature (none: a pool of its own), and one IsPipeline map per
    /// compute shader with its root signature, on the shader's platform.</summary>
    internal static List<ShaderMap> Maps(string rel, IReadOnlyDictionary<string, ShaderInfo> shaders, Func<string, string> platform)
    {
        var maps = new List<ShaderMap>();
        foreach (var pool in shaders.Where(s => s.Value.Stage != Stage.Compute).GroupBy(s => s.Value.RootSignature ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var root = pool.Key.Length == 0 ? null : pool.Key;
            maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{Family}|{rel}|{pool.Key}"), rel, CarvedReader.Platform,
                pool.Select(s => s.Key).Order(StringComparer.Ordinal).ToList(), RootSignature: root));
        }
        foreach (var (cs, info) in shaders.Where(s => s.Value.Stage == Stage.Compute).OrderBy(s => s.Key, StringComparer.Ordinal))
            maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{cs}|{info.RootSignature}"), rel, platform(cs), [cs], IsPipeline: true, info.RootSignature));
        return maps;
    }

    /// <summary>Re-reads each container from the entry the index found it in; one whose bytes changed since (game patched)
    /// is skipped: it fails at replay and is counted there. Root signatures are served by the shaders carrying them.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Pak))
        {
            SafeFileHandle h;
            try { h = Open(file.Key); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }   // gone since the index
            using (h)
                foreach (var (sha, at) in file.OrderBy(x => x.At.Entry.Offset))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Container(EnfusionPak.Open(h, at.Entry)) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
                }
        }
    }
}
