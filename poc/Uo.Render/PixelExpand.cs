using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Uo.Render;

/// <summary>
/// B5G5R5A1 → R8G8B8A8 expansion for GPUs without a sampleable 16-bit format (WebGPU, Intel Macs).
/// Channels expand as round(c·255/31) = (c·527 + 23) >> 6 for c in 0..31, the same value GPUs produce when they sample
/// B5G5R5A1_UNORM natively, so the expanded and native paths render identically. Computed in 16-bit lanes with no
/// lookup. Alpha bit → 0x00/0xFF. (ClassicUO's HuesHelper table is floor(c·255/31), at most 1 lower per channel.)
/// Uses only portable Vector128/256 ops: SSE/AVX on x64, AdvSimd on ARM64, PackedSimd (SIMD128) on WASM.
/// </summary>
public static class PixelExpand
{
    const ushort Mul = 527;
    const ushort Bias = 23;
    const int Shift = 6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Expand(ushort c)
    {
        uint r = (uint)(((c >> 10) & 0x1F) * Mul + Bias) >> Shift;
        uint g = (uint)(((c >> 5) & 0x1F) * Mul + Bias) >> Shift;
        uint b = (uint)((c & 0x1F) * Mul + Bias) >> Shift;
        uint a = (uint)-(c >> 15) & 0xFF;
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    /// <summary>Expands <paramref name="src"/> into <paramref name="dst"/> (RGBA8 as little-endian uints, R in the low byte).</summary>
    public static void Expand(ReadOnlySpan<ushort> src, Span<uint> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("destination too small", nameof(dst));

        ref ushort s = ref MemoryMarshal.GetReference(src);
        ref uint d = ref MemoryMarshal.GetReference(dst);
        nuint n = (nuint)src.Length;
        nuint i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= n; i += 16)
            {
                var v = Vector256.LoadUnsafe(ref s, i);
                var (lo, hi) = Expand(v);
                lo.StoreUnsafe(ref d, i);
                hi.StoreUnsafe(ref d, i + 8);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 8 <= n; i += 8)
            {
                var v = Vector128.LoadUnsafe(ref s, i);
                var (lo, hi) = Expand(v);
                lo.StoreUnsafe(ref d, i);
                hi.StoreUnsafe(ref d, i + 4);
            }
        }

        for (; i < n; i++)
            Unsafe.Add(ref d, i) = Expand(Unsafe.Add(ref s, i));
    }

    /// <summary>Byte-span overload for staging buffers: <paramref name="src"/> holds little-endian u16 texels.</summary>
    public static void Expand(ReadOnlySpan<byte> src, Span<byte> dst)
        => Expand(MemoryMarshal.Cast<byte, ushort>(src), MemoryMarshal.Cast<byte, uint>(dst));

    /// <summary>Exhaustive check of all 65536 texels against round(c·255/31) computed in floating point, through the scalar path and the span path
    /// (vector + scalar tail) at several start offsets. Returns the number of mismatching texels.</summary>
    public static int SelfTest()
    {
        var src = new ushort[65536 + 64];
        var dst = new uint[src.Length];
        int bad = 0;
        for (int c = 0; c < 65536; c++)
        {
            if (Expand((ushort)c) != Want(c)) bad++;
        }
        foreach (int off in (ReadOnlySpan<int>)[0, 1, 3, 7, 8, 15, 17, 31])
        {
            for (int c = 0; c < 65536; c++) src[off + c] = (ushort)c;
            int n = 65536 + (off & 7); // odd lengths exercise the scalar tail
            Array.Clear(dst);
            Expand(src.AsSpan(off, n), dst.AsSpan(off, n));
            for (int k = 0; k < n; k++)
                if (dst[off + k] != Want(src[off + k])) bad++;
        }
        return bad;

        static uint Want(int c)
        {
            static uint Ch(int v) => (uint)Math.Round(v * 255.0 / 31.0); // no exact .5 ties for v in 0..31
            return Ch((c >> 10) & 0x1F) | (Ch((c >> 5) & 0x1F) << 8) | (Ch(c & 0x1F) << 16) | ((c & 0x8000) != 0 ? 0xFF000000u : 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (Vector128<uint> lo, Vector128<uint> hi) Expand(Vector128<ushort> v)
    {
        var m5 = Vector128.Create((ushort)0x1F);
        var bias = Vector128.Create(Bias);
        var r = (((v >>> 10) & m5) * Mul + bias) >>> Shift;
        var g = (((v >>> 5) & m5) * Mul + bias) >>> Shift;
        var b = ((v & m5) * Mul + bias) >>> Shift;
        var a = (v.AsInt16() >> 15).AsUInt16() & Vector128.Create((ushort)0xFF);
        var rg = r | (g << 8);
        var ba = b | (a << 8);
        var (rgLo, rgHi) = Vector128.Widen(rg);
        var (baLo, baHi) = Vector128.Widen(ba);
        return (rgLo | (baLo << 16), rgHi | (baHi << 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (Vector256<uint> lo, Vector256<uint> hi) Expand(Vector256<ushort> v)
    {
        var m5 = Vector256.Create((ushort)0x1F);
        var bias = Vector256.Create(Bias);
        var r = (((v >>> 10) & m5) * Mul + bias) >>> Shift;
        var g = (((v >>> 5) & m5) * Mul + bias) >>> Shift;
        var b = ((v & m5) * Mul + bias) >>> Shift;
        var a = (v.AsInt16() >> 15).AsUInt16() & Vector256.Create((ushort)0xFF);
        var rg = r | (g << 8);
        var ba = b | (a << 8);
        var (rgLo, rgHi) = Vector256.Widen(rg);
        var (baLo, baHi) = Vector256.Widen(ba);
        return (rgLo | (baLo << 16), rgHi | (baHi << 16));
    }
}
