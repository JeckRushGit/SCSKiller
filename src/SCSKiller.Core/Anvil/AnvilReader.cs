using System.Security.Cryptography;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Anvil;

/// <summary>
/// Engine reader for Ubisoft's AnvilNext, starting with Assassin's Creed Unity (DirectX 11). The game ships
/// its shaders packed and LZO-compressed inside ".forge" archives, which the generic Carved reader cannot
/// see. This reader unpacks the archives with <see cref="ForgeArchive"/>, then reuses the EXISTING DXBC
/// machinery (<see cref="Dxbc"/>, <see cref="ShaderContainer"/>) to validate and reflect the shaders found
/// inside — the same code that indexes Starfield or Cyberpunk, just fed decompressed bytes.
///
/// STATUS: first-draft scaffold. It needs (1) a real LZO decompressor wired into <see cref="Lzo"/>,
/// (2) validation against real .forge files, (3) a build, and (4) the map-pairing to converge with
/// CarvedReader's for best pipeline coverage. Detection is deliberately narrow (Unity only) for now.
/// Register it in the reader list in ScsKiller.cs, BEFORE the generic CarvedReader.
/// </summary>
public sealed class AnvilReader : IEngineReader
{
    public const string Family = "Anvil";
    const string Platform = "D3D11";

    /// <summary>The LZO decompressor the archive needs. Inject a real one (managed minilzo for lzo1x, or a
    /// bundled liblzo2 for lzo1x/lzo2a/lzo1c). Left unset, indexing throws with a clear message.</summary>
    public static ForgeArchive.LzoDecompress? Lzo { get; set; }

    static ForgeArchive.LzoDecompress RequireLzo() =>
        Lzo ?? throw new InvalidOperationException("AnvilReader.Lzo is not set: no LZO decompressor available");

    public EngineInfo? Detect(Game game)
    {
        var dir = Path.GetDirectoryName(game.ExePath);
        if (dir == null || !IsAssassinsCreedUnity(game, dir)) return null;
        // One readable scimitar v27 archive confirms the format before we commit to the engine.
        if (!EnumerateForge(dir).Any(IsScimitarV27)) return null;
        return new EngineInfo(Family, "Unity", Fork: null, GraphicsApi: Platform,
            Encrypted: false, Unsupported: null);
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var lzo = RequireLzo();
        var dir = Path.GetDirectoryName(game.ExePath)!;

        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new List<ShaderMap>();
        var mapHashes = new HashSet<string>();
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        foreach (var forgePath in EnumerateForge(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            using var forge = ForgeArchive.Open(forgePath, lzo);
            if (forge == null) continue;
            var rel = Path.GetFileName(forgePath);

            foreach (var df in forge.DataFiles)
            {
                ct.ThrowIfCancellationRequested();
                List<ForgeArchive.Resource> resources;
                try { resources = [.. forge.ReadResources(df)]; }
                catch (Exception e) { log?.Report($"{rel}/{df.Name}: unpack failed: {e.Message}"); continue; }

                foreach (var res in resources)
                {
                    // One resource may hold several DXBC containers back to back (a pipeline's stages).
                    var run = new List<string>();
                    foreach (var (_, c) in Dxbc.Containers(res.Data))
                    {
                        var kind = Dxbc.Kind(c);
                        if (kind < 0) continue;
                        var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                        if (!shaders.ContainsKey(sha))
                        {
                            if (ShaderContainer.Parse(c, sha, new ResourceCounts(0, 0, 0, 0)) is not { } info) continue;
                            shaders[sha] = info;
                            content.AppendData(c.AsSpan(0, Math.Min(32, c.Length)));   // fingerprint the build
                        }
                        run.Add(sha);
                    }
                    if (run.Count > 0)
                    {
                        var h = CarvedReader.Sha1Hex(string.Join(',', run.Order(StringComparer.Ordinal)));
                        if (mapHashes.Add(h))
                            maps.Add(new ShaderMap(h, rel, Platform, run, IsPipeline: run.Count > 1));
                    }
                }
            }
        }

        log?.Report($"Anvil: {shaders.Count} shaders in {maps.Count} maps");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [Platform], shaders, maps);
    }

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var lzo = RequireLzo();
        var dir = Path.GetDirectoryName(game.ExePath)!;
        var want = new HashSet<string>(sha1s);

        foreach (var forgePath in EnumerateForge(dir))
        {
            if (want.Count == 0) return;
            ct.ThrowIfCancellationRequested();
            using var forge = ForgeArchive.Open(forgePath, lzo);
            if (forge == null) continue;

            foreach (var df in forge.DataFiles)
            {
                if (want.Count == 0) return;
                ct.ThrowIfCancellationRequested();
                List<ForgeArchive.Resource> resources;
                try { resources = [.. forge.ReadResources(df)]; }
                catch { continue; }

                foreach (var res in resources)
                    foreach (var (_, c) in Dxbc.Containers(res.Data))
                    {
                        var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                        if (want.Remove(sha)) sink(sha, c);
                    }
            }
        }
    }

    // --- detection helpers (first-draft, conservative) -----------------------

    static bool IsAssassinsCreedUnity(Game game, string dir) =>
        string.Equals(Path.GetFileName(game.ExePath), "ACU.exe", StringComparison.OrdinalIgnoreCase)
        || Directory.EnumerateFiles(dir, "*.forge", SearchOption.TopDirectoryOnly)
            .Any(f => Path.GetFileName(f).StartsWith("DataPC", StringComparison.OrdinalIgnoreCase));

    static IEnumerable<string> EnumerateForge(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.forge", SearchOption.AllDirectories); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static bool IsScimitarV27(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> h = stackalloc byte[13];
            if (s.Read(h) != h.Length) return false;
            return h[..8].SequenceEqual("scimitar"u8)
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(h[9..]) == ForgeArchive.SupportedVersion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
