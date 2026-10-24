using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Uo.Render;

/// <summary>
/// B5G5R5A1 → R8G8B8A8 expansion for GPUs without a sampleable 16-bit format (WebGPU, Intel Macs).
/// Channels expand with ClassicUO's HuesHelper table, which is exactly floor(c·255/31) = (c·1053) >> 7 for c in 0..31,
/// so the vector paths compute it in 16-bit lanes with no lookup. Alpha bit → 0x00/0xFF.
/// Uses only portable Vector128/256 ops: SSE/AVX on x64, AdvSimd on ARM64, PackedSimd (SIMD128) on WASM.
/// </summary>
public static class PixelExpand
{
    const ushort Mul = 1053;
    const int Shift = 7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Expand(ushort c)
    {
        uint r = (uint)(((c >> 10) & 0x1F) * Mul) >> Shift;
        uint g = (uint)(((c >> 5) & 0x1F) * Mul) >> Shift;
        uint b = (uint)((c & 0x1F) * Mul) >> Shift;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (Vector128<uint> lo, Vector128<uint> hi) Expand(Vector128<ushort> v)
    {
        var m5 = Vector128.Create((ushort)0x1F);
        var r = ((v >>> 10) & m5) * Mul >>> Shift;
        var g = ((v >>> 5) & m5) * Mul >>> Shift;
        var b = (v & m5) * Mul >>> Shift;
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
        var r = ((v >>> 10) & m5) * Mul >>> Shift;
        var g = ((v >>> 5) & m5) * Mul >>> Shift;
        var b = (v & m5) * Mul >>> Shift;
        var a = (v.AsInt16() >> 15).AsUInt16() & Vector256.Create((ushort)0xFF);
        var rg = r | (g << 8);
        var ba = b | (a << 8);
        var (rgLo, rgHi) = Vector256.Widen(rg);
        var (baLo, baHi) = Vector256.Widen(ba);
        return (rgLo | (baLo << 16), rgHi | (baHi << 16));
    }
}
