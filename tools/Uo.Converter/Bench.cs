using System.Diagnostics;
using System.Runtime.InteropServices;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>--bench-bc7 &lt;rgba8 page&gt;: times BC7 encoding strategies on one page's mip0.</summary>
public static class Bench
{
    public static void Bc7(string page)
    {
        var chain = new byte[AtlasPage.ChainBytes(2048, 6)];
        AtlasPage.Read(page, chain);
        var mip0 = chain.AsSpan(0, 2048 * 2048 * 4).ToArray();
        var src = MemoryMarshal.Cast<byte, uint>(mip0.AsSpan()).ToArray();
        foreach (var q in new[] { CompressionQuality.Fast, CompressionQuality.Balanced })
        {
            ConvertOptions.Bc7Quality = q;
            var sw = Stopwatch.StartNew();
            var bc = PageCodec.EncodeBc7(mip0, 2048, 1, fresh: true);
            var t = sw.Elapsed.TotalSeconds;
            var dec = PageCodec.DecodeBc7(bc, 2048);
            double se = 0; long n = 0;
            for (int i = 0; i < src.Length; i++)
            {
                if ((src[i] >> 24) == 0) continue;
                for (int sh = 0; sh < 24; sh += 8) { int d = (int)((src[i] >> sh) & 0xFF) - (int)((dec[i] >> sh) & 0xFF); se += d * d; }
                n += 3;
            }
            Console.WriteLine($"block-level {q}: {t:F1}s, PSNR rgb (visible texels) {10 * Math.Log10(255.0 * 255 / (se / n)):F2} dB");
        }
    }
}
