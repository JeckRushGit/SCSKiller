using System.Runtime.InteropServices;

namespace SCSKiller.Core.Anvil;

/// <summary>
/// LZO decompression for <see cref="AnvilReader"/>, via a native LZO library (lzo1x/lzo2a/lzo1c). This is the
/// decompressor the .forge format needs; it was validated against AC Unity's real archives (3.3M resources,
/// ~14.8k shaders extracted). Call <see cref="Install"/> once at startup to wire it into the reader.
///
/// DEPLOYMENT: ship a native LZO as "lzo64.dll" next to the app (and the warm/CLI exes). LZO (liblzo2 /
/// minilzo) is GPL-2.0-or-later, compatible with SCSKiller's GPL-3.0-or-later. A clean alternative is to
/// compile minilzo into the C++ proxy and P/Invoke that instead of a separate DLL — a later cleanup.
///
/// NOTE on the return code: lzo1x_decompress returns -8 (LZO_E_INPUT_NOT_CONSUMED) when the output is
/// complete but the block had trailing bytes (padding/hash the block size includes). The output is valid,
/// so we accept 0 and -8 and throw only on other codes.
/// </summary>
public static class AnvilLzo
{
    const string Dll = "lzo64.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int lzo1x_decompress(byte[] src, ref ushort srcLen, byte[] dst, ref ushort dstLen, IntPtr wrkmem);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int lzo2a_decompress(byte[] src, ref ushort srcLen, byte[] dst, ref ushort dstLen, IntPtr wrkmem);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int lzo1c_decompress(byte[] src, ref ushort srcLen, byte[] dst, ref ushort dstLen, IntPtr wrkmem);

    /// <summary>Wires this decompressor into <see cref="AnvilReader.Lzo"/>. Safe to call more than once.</summary>
    public static void Install() => AnvilReader.Lzo = Decompress;

    static byte[] Decompress(int mode, ReadOnlySpan<byte> src, int dstLen)
    {
        var s = src.ToArray();
        var d = new byte[dstLen];
        ushort sl = checked((ushort)s.Length);
        ushort dl = checked((ushort)dstLen);

        int rc = mode switch
        {
            0 or 1 => lzo1x_decompress(s, ref sl, d, ref dl, IntPtr.Zero),
            2 => lzo2a_decompress(s, ref sl, d, ref dl, IntPtr.Zero),
            5 => lzo1c_decompress(s, ref sl, d, ref dl, IntPtr.Zero),
            _ => throw new NotSupportedException($"LZO mode {mode} not supported"),
        };
        if (rc != 0 && rc != -8) throw new InvalidDataException($"LZO mode {mode}: decompress returned {rc}");
        return d;
    }
}
