using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Essence;

/// <summary>Essence 5's effect database (effects.bin in a DX12 shader archive): a Relic Chunky file of the engine's reflection
/// types (DATARFTY: each type's name, hash, size and fields, each field's name, type, offset, size and array count) and two
/// memory images (DATARFCI, 8-byte aligned from the file's start after its 4-byte prefix): rr::rrDataBases (effects,
/// techniques, param blocks, sampler states) and rr::rrShaderDataBase (each shader's name, the archive's blob name). In the
/// images a ReflectOffsetPointer is a u64 offset from the pointer itself, a ReflectArray that pointer and an i32 count, a
/// ReflectString that pointer and an i32 length, and a ReflectObjectRef the u64 object id at +8 that the object carries at +8.
///
/// <para>A technique is a pipeline: m_shaders names one shader per stage. Its root signature is its effect's, rebuilt here
/// as the engine builds it (CoH3's recording, 2026-11: 100 of 100 root signatures, 286 of 286 PSOs, byte for byte): version
/// 1.1, flags ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT; first an SRV table for the instance indices (m_instanceIndicesBindingRegister,
/// when set), then one table per param block in the effect's order (a technique's exclusions drop none): its constant
/// buffer (CBV at m_constantBufferRegisterMappings, flags DATA_STATIC_WHILE_SET_AT_EXECUTE) when it has data entries and a
/// register, then its SRVs from m_srvStartRegisterMappings (its read-only textures and buffers, plus one structured buffer for
/// its data when it has no CBV register; DESCRIPTORS_VOLATILE), then its UAVs from m_uavStartRegisterMappings (GPU-writable
/// entries; DESCRIPTORS_VOLATILE); every range appended, visibility ALL; a block with none of these has no table. Static
/// samplers are the registers the pipeline's shaders sample, each the effect's m_samplers entry at that register: filter
/// (rr::FilterMode, 9 per reduction: none, comparison, minimum, maximum), address modes and comparison (rr's + 1), LOD bias,
/// anisotropy, border color (transparent black, opaque black or opaque white by its alpha and red), LOD range.</para></summary>
public sealed class EssenceEffects
{
    /// <summary>A technique: its effect's and its own name, its shaders' blob names, and its effect's layout.</summary>
    public sealed record Technique(string Effect, string Name, IReadOnlyList<string> Shaders, Layout Layout);

    /// <summary>An effect's root parameters (RootSig.Desc rows) and its 48 sampler slots (52-byte D3D12_STATIC_SAMPLER_DESC
    /// without the register, by register; null: no sampler state there).</summary>
    public sealed record Layout(List<uint[]> Rows, uint[]?[] Samplers)
    {
        /// <summary>The serialized root signature of a pipeline whose shaders sample <paramref name="samplerRegisters"/>;
        /// null when one of them has no sampler state in the effect.</summary>
        public byte[]? RootSignature(IEnumerable<int> samplerRegisters)
        {
            var desc = new List<uint>();
            foreach (var reg in samplerRegisters.Distinct().Order())
            {
                if (reg < 0 || reg >= Samplers.Length || Samplers[reg] is not { } s) return null;
                desc.AddRange(s);
                desc.AddRange([(uint)reg, 0, 0]);   // register, space, visibility ALL
            }
            return RootSig.Serialize(new RootSig.Desc(AllowInputLayout, Rows, AppendRanges: true), MemoryMarshal.AsBytes(desc.ToArray().AsSpan()).ToArray());
        }
    }

    public IReadOnlyList<Technique> Techniques { get; }

    const uint AllowInputLayout = 1, Volatile = 1, StaticWhileSet = 8;
    const uint Srv = 0, Uav = 1, Cbv = 2;
    const int SamplerSlots = 48;
    static readonly uint[] BaseFilter = [0x00, 0x01, 0x04, 0x05, 0x10, 0x11, 0x14, 0x15, 0x55];

    EssenceEffects(List<Technique> techniques) => Techniques = techniques;

    /// <summary>The database's techniques; InvalidDataException when it isn't one this reads (a field it needs is missing,
    /// or a pointer leaves its image).</summary>
    public static EssenceEffects Read(byte[] b) => new Reader(b).Read();

    /// <summary>D3D12_FILTER of an rr::FilterMode.</summary>
    internal static uint Filter(uint mode) => mode < 36 ? (mode / 9) << 7 | BaseFilter[mode % 9] : throw new InvalidDataException($"filter mode {mode}");

    sealed record Field(string Type, int Offset, int Size, int Count);

    sealed record TypeDef(string Name, ulong Hash, int Size, Dictionary<string, Field> Fields)
    {
        public Field this[string field] => Fields.TryGetValue(field, out var f) ? f : throw new InvalidDataException($"{Name} has no {field}");
    }

    sealed class Reader(byte[] b)
    {
        readonly Dictionary<string, TypeDef> types = [];
        readonly List<(int Start, int End)> images = [];
        int prefix;

        public EssenceEffects Read()
        {
            prefix = b.AsSpan().IndexOf("Relic Chunky"u8);
            if (prefix < 0 || prefix > 64) throw new InvalidDataException("not a Relic Chunky file");
            Chunks(prefix + 24, b.Length, 0);
            if (images.Count < 2) throw new InvalidDataException("no effect and shader databases");
            var root = Type("rr::rrDataBases");
            var data = images.FirstOrDefault(i => I64(i.Start + 16) == (long)root.Hash);
            var shaderRoot = Type("rr::rrShaderDataBase");
            var shaderData = images.FirstOrDefault(i => I64(i.Start + 16) == (long)shaderRoot.Hash);
            if (data == default || shaderData == default) throw new InvalidDataException("no effect or shader database image");

            // objects by id: effects, techniques, param blocks, sampler states
            var byId = new Dictionary<ulong, long>();
            var effects = new List<(long At, TypeDef Type)>();
            var typeByHash = types.Values.GroupBy(t => t.Hash).ToDictionary(g => g.Key, g => g.First());
            long Sub(long at, TypeDef t, string f) => at + t[f].Offset;
            foreach (var p in Pointers(data, Sub(data.Start, root, "effectDataBase") + Type("rr::EffectDataBase")["m_effects"].Offset))
            {
                byId[U64(p + 8)] = p;
                effects.Add((p, typeByHash.GetValueOrDefault(U64(p + 16)) ?? throw new InvalidDataException("an effect of an unknown type")));
            }
            foreach (var p in Pointers(data, Sub(data.Start, root, "techniqueDataBase") + Type("rr::TechniqueDataBase")["m_techniques"].Offset)) byId[U64(p + 8)] = p;
            foreach (var p in Pointers(data, Sub(data.Start, root, "paramBlockDataBase") + Type("rr::ParamBlockDataBase")["m_paramBlockMetaData"].Offset)) byId[U64(p + 8)] = p;
            foreach (var p in Pointers(data, Sub(data.Start, root, "stateBlockDataBase") + Type("rr::StateBlockDataBase")["m_samplerStates"].Offset)) byId[U64(p + 8)] = p;

            // shader names by id
            var shaderDb = Type("rr::ShaderDataBase");
            var shaderType = Type("rr::ShaderData");
            var names = new Dictionary<uint, string>();
            var (shaders, shaderCount) = Array(shaderData, Sub(shaderData.Start, shaderRoot, "shaderDataBase") + shaderDb["m_shaderData"].Offset);
            for (var i = 0; i < shaderCount; i++)
            {
                var at = shaders + (long)i * shaderType.Size;
                Check(shaderData, at, shaderType.Size);
                names[U32(at + shaderType["id"].Offset)] = Str(shaderData, at + shaderType["name"].Offset);
            }

            var technique = Type("rr::Technique");
            var shaderSlots = technique["m_shaders"];
            var refType = Type("util::ReflectObjectRef<rr::Technique,StdTraits>");
            var list = new List<Technique>();
            foreach (var (at, type) in effects)
            {
                var name = Str(data, at + type["m_name"].Offset);
                var layout = LayoutOf(data, at, type, byId);
                var (refs, n) = Array(data, at + type["m_techniques"].Offset);
                for (var i = 0; i < n; i++)
                {
                    var r = refs + (long)i * refType.Size;
                    Check(data, r, refType.Size);
                    if (!byId.TryGetValue(U64(r + refType["m_objectID"].Offset), out var t)) throw new InvalidDataException($"{name}: a technique that isn't in the database");
                    Check(data, t, technique.Size);
                    var ids = Enumerable.Range(0, shaderSlots.Count).Select(k => U32(t + shaderSlots.Offset + 4 * k)).Where(id => id != uint.MaxValue).ToList();
                    if (ids.Count == 0) continue;
                    foreach (var id in ids)
                        if (!names.ContainsKey(id)) throw new InvalidDataException($"{name}: shader {id} isn't in the shader database");
                    list.Add(new Technique(name, Str(data, t + technique["m_name"].Offset), ids.Select(id => names[id]).ToList(), layout));
                }
            }
            return new EssenceEffects(list);
        }

        Layout LayoutOf((int Start, int End) data, long at, TypeDef type, Dictionary<ulong, long> byId)
        {
            int[] Ints(string field)
            {
                if (!type.Fields.ContainsKey(field)) return [];
                var (s, n) = Array(data, at + type[field].Offset);
                Check(data, s, 4L * n);
                return Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)(s + 4 * i)))).ToArray();
            }
            var cb = Ints("m_constantBufferRegisterMappings");
            var srv = Ints("m_srvStartRegisterMappings");
            var uav = Ints("m_uavStartRegisterMappings");
            var rows = new List<uint[]>();
            if (type.Fields.ContainsKey("m_instanceIndicesBindingRegister") && BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)(at + type["m_instanceIndicesBindingRegister"].Offset))) is var ii and >= 0)
                rows.Add([0, 0, Srv, 1, (uint)ii, 0, Volatile]);

            var block = Type("rr::ParamBlockMetaData");
            var tex = Type("rr::ParamBlockTextureEntry");
            var buf = Type("rr::ParamBlockBufferEntry");
            var refType = Type("util::ReflectObjectRef<rr::ParamBlockMetaData,StdTraits>");
            var (refs, n) = Array(data, at + type["m_paramBlocks"].Offset);
            for (var i = 0; i < n; i++)
            {
                var r = refs + (long)i * refType.Size;
                Check(data, r, refType.Size);
                if (!byId.TryGetValue(U64(r + refType["m_objectID"].Offset), out var p)) throw new InvalidDataException("a param block that isn't in the database");
                Check(data, p, block.Size);
                int srvs = 0, uavs = 0;
                foreach (var (entries, t) in new[] { (block["textureEntries"], tex), (block["bufferEntries"], buf) })
                {
                    var (s, count) = Array(data, p + entries.Offset);
                    for (var k = 0; k < count; k++)
                    {
                        var e = s + (long)k * t.Size;
                        Check(data, e, t.Size);
                        var c = (int)U32(e + t["count"].Offset);
                        if (b[e + t["needsGPUWriteAccess"].Offset] != 0) uavs += c; else srvs += c;
                    }
                }
                var hasData = Array(data, p + block["dataEntries"].Offset).Count > 0;
                int cbReg = i < cb.Length ? cb[i] : -1, srvReg = i < srv.Length ? srv[i] : -1, uavReg = i < uav.Length ? uav[i] : -1;
                var row = new List<uint> { 0, 0 };
                if (hasData && cbReg >= 0) row.AddRange([Cbv, 1, (uint)cbReg, 0, StaticWhileSet]);
                if (hasData && cbReg < 0) srvs++;   // its data as a structured buffer
                if (srvs > 0 && srvReg >= 0) row.AddRange([Srv, (uint)srvs, (uint)srvReg, 0, Volatile]);
                if (uavs > 0 && uavReg >= 0) row.AddRange([Uav, (uint)uavs, (uint)uavReg, 0, Volatile]);
                if (row.Count > 2) rows.Add([.. row]);
            }

            var samplers = new uint[]?[SamplerSlots];
            var slots = type["m_samplers"];
            var sampler = Type("rr::SamplerState");
            for (var i = 0; i < Math.Min(SamplerSlots, slots.Count); i++)
            {
                var id = U64(at + slots.Offset + (long)slots.Size * i + 8);
                if (id == 0) continue;
                if (!byId.TryGetValue(id, out var s)) throw new InvalidDataException("a sampler state that isn't in the database");
                Check(data, s, sampler.Size);
                uint F(string f) => U32(s + sampler[f].Offset);
                float Border(int k) => BitConverter.UInt32BitsToSingle(U32(s + sampler["borderColor"].Offset + 4 * k));
                samplers[i] = [Filter(F("filterMode")), F("addressModeU") + 1, F("addressModeV") + 1, F("addressModeW") + 1, F("mipLodBias"),
                    F("maxAnisotropy"), F("comparisonFunc") + 1, Border(3) == 0 ? 0u : Border(0) == 0 ? 1u : 2u, F("minLod"), F("maxLod")];
            }
            return new Layout(rows, samplers);
        }

        TypeDef Type(string name) => types.TryGetValue(name, out var t) ? t : throw new InvalidDataException($"no type {name}");

        void Chunks(int at, int end, int depth)
        {
            while (depth < 8 && at + 20 <= end)
            {
                var size = (int)U32(at + 12);
                var body = at + 20 + (int)U32(at + 16);
                if (size < 0 || body + (long)size > end) throw new InvalidDataException("chunk runs past its folder");
                var kind = b.AsSpan(at, 8);
                if (kind[..4].SequenceEqual("FOLD"u8)) Chunks(body, body + size, depth + 1);
                else if (kind.SequenceEqual("DATARFTY"u8)) ParseType(body, size);
                else if (kind.SequenceEqual("DATARFCI"u8))
                {
                    var start = body;
                    while ((start - prefix) % 8 != 0) start++;
                    images.Add((start, body + size));
                }
                at = body + size;
            }
        }

        /// <summary>A type: name, hash, size, then its fields, each a u32-prefixed name, a u64 hash, a u32-prefixed type name,
        /// a u64 hash, then u32 offset, size, array flag and count. What comes between fields isn't read: each field is
        /// found by its name and type name and checked against the type's size.</summary>
        void ParseType(int body, int size)
        {
            var end = body + size;
            var n = (int)U32(body);
            if (n is <= 0 or > 512 || body + 16 + n > end) return;
            var t = new TypeDef(Encoding.ASCII.GetString(b, body + 4, n), U64(body + 4 + n), (int)U32(body + 12 + n), []);
            for (var p = body + 16 + n; p + 8 < end; p++)
            {
                var l = (int)U32(p);
                if (l is < 2 or > 100 || p + 4 + l + 12 > end || !(char.IsAsciiLetter((char)b[p + 4]) || b[p + 4] == '_')) continue;
                if (!b.AsSpan(p + 4, l).ToArray().All(c => char.IsAsciiLetterOrDigit((char)c) || c == '_')) continue;
                var q = p + 4 + l + 8;
                var tl = (int)U32(q);
                if (tl is < 1 or > 300 || q + 4 + tl + 24 > end) continue;
                var r = q + 4 + tl + 8;
                var f = new Field(Encoding.ASCII.GetString(b, q + 4, tl), (int)U32(r), (int)U32(r + 4), U32(r + 8) != 0 ? (int)U32(r + 12) : 1);
                if (f.Offset < 0 || f.Size < 0 || f.Count < 0 || (long)f.Offset + (long)f.Size * f.Count > Math.Max(t.Size, 1) && t.Size > 0) continue;
                t.Fields.TryAdd(Encoding.ASCII.GetString(b, p + 4, l), f);
                p = r + 15;
            }
            types.TryAdd(t.Name, t);
        }

        void Check((int Start, int End) image, long at, long size)
        {
            if (at < image.Start || size < 0 || at + size > image.End) throw new InvalidDataException("a pointer leaves its image");
        }

        long Ptr((int Start, int End) image, long at)
        {
            Check(image, at, 8);
            var o = I64(at);
            if (o == 0) return 0;
            var p = at + o;
            Check(image, p, 0);
            return p;
        }

        (long Start, int Count) Array((int Start, int End) image, long at)
        {
            var p = Ptr(image, at);
            Check(image, at + 8, 4);
            var n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)at + 8));
            if (n < 0 || n > 1 << 22 || n > 0 && p == 0) throw new InvalidDataException("array count out of range");
            return (p, n);
        }

        List<long> Pointers((int Start, int End) image, long at)
        {
            var (s, n) = Array(image, at);
            Check(image, s, 8L * n);
            return Enumerable.Range(0, n).Select(i => Ptr(image, s + 8L * i)).Where(p => p != 0).ToList();
        }

        string Str((int Start, int End) image, long at)
        {
            var (p, n) = Array(image, at);
            if (n == 0) return "";
            Check(image, p, n);
            return Encoding.ASCII.GetString(b, (int)p, n);
        }

        uint U32(long at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)at));
        ulong U64(long at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)at));
        long I64(long at) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan((int)at));
    }
}
