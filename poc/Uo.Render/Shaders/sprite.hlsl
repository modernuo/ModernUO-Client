// Uo.Render sprite shader: vertex pulling from a storage buffer of 32-byte instances.
//
// SDL_GPU (D3D12/DXIL) register conventions, see SDL_CreateGPUShader in SDL_gpu.h:
//   vertex:   t[n] space0 = sampled textures, then storage textures, then storage buffers
//             s[n] space0 = samplers, b[n] space1 = uniform buffers
//   fragment: t[n] space2 / s[n] space2, b[n] space3
// SPIR-V (Vulkan) uses the same numbers as descriptor set = space, binding = register; sampled textures are combined
// image samplers, so each texture and its sampler share a binding ([[vk::combinedImageSampler]], ignored for DXIL).
//
// Instance layout (must match SpriteInstance in GpuTypes.cs):
//   int  x, y      world pixel of the quad's top-left (for stretched land: tile anchor)
//   uint whf       w (12 bits) | h (12 bits) << 12 | flags (8 bits) << 24
//   uint uv        u | v << 16 (atlas pixels)
//   uint pageHue   logical page | hue row << 16
//   uint depth     integer depth key (bigger = nearer); camera-relative in the shader
//   uint light     FLAG_LIT: 4 x u8 per-corner light (stretched land), 170 = 1.0;
//                  otherwise byte 0 = 255 - alpha (0 = opaque), byte 1 = hue mode (MODE_*)
//   uint corners   4 x s8 per-corner z (stretched land), corner order top,right,left,bottom; FLAG_INDEXED: palette id
//
// Two draws share the instance buffer: pass 0 (opaque, depth write) walks the visible chunk table and skips translucent
// instances; pass 1 (premultiplied blend, depth test only) draws the frame table's back-to-front list of translucent
// instance indices. A per-frame override hash (instance index -> alpha / hidden / highlight hue) applies to both.

#define FLAG_HUED     1u
#define FLAG_PARTIAL  2u
#define FLAG_STRETCH  4u
#define FLAG_LIT      8u
#define FLAG_MIRROR   16u     // flip the rect horizontally (mirrored anim directions)
#define FLAG_INDEXED  32u     // Indexed8 anim page: AnimIdx array + palette lookup, manual filtering
#define FLAG_SHARP    0x100u  // set by the vertex shader from the Sharp uniform
#define FLAG_UNPREMUL 0x200u  // FormatFlags bit 0 << 9
#define FLAG_GRAYTOL  0x400u  // FormatFlags bit 1 << 9
// hueFlags.y bits 12-15: mode, 16-23: alpha
#define MODE_NORMAL   0u
#define MODE_SPECTRAL 1u
#define MODE_SHADOW   2u
#define MODE_EFFECT   3u
#define OV_HIDDEN     1u
#define OV_HIGHLIGHT  2u
// BC7 perturbs exact grays by a few levels; ClassicUO's partial-hue test is r == g == b on 5-bit source colours,
// so BC7 packs use a small tolerance (GrayTolerance uniform, set by the renderer).

struct Instance
{
    int  x;
    int  y;
    uint whf;
    uint uv;
    uint pageHue;
    uint depth;
    uint light;
    uint corners;
};

StructuredBuffer<Instance> Instances  : register(t0, space0);
// Frame table: [0, ChunkCount) chunk entries (x = first instance in Instances, y = running instance count before this
// chunk); [TransBase, ...) translucent pass (x = instance index); [OverrideBase, + OverrideMask + 1) override hash
// (x = instance index + 1, 0 = empty; y = alpha | flags << 8 | hue row << 16), linear probing.
StructuredBuffer<uint2>    FrameTable : register(t1, space0);
StructuredBuffer<uint>     PageTable  : register(t2, space0); // logical page -> physical layer (0xFFFFFFFF = not resident)

cbuffer Frame : register(b0, space1)
{
    int2   CamPx;
    float2 CamFrac;
    float2 Scale;
    float2 InvViewport;
    uint   CamDepth;
    uint   ChunkCount;
    float  InvAtlas;
    float  DepthScale;
    uint   Sharp;      // 1 = scale >= 1: sharp-bilinear (stable at fractional scales); 0 = trilinear (zoomed out)
    float  AtlasSize;
    uint   FormatFlags; // 1 = un-premultiply filtered edges (RGBA8/16-bit), 2 = partial-hue gray tolerance (BC7)
    float  GrayTolerance; // partial-hue gray test tolerance when FLAG_GRAYTOL (BC7)
    float  AnimAtlasSize;  // Indexed8 anim page side
    float  InvAnimAtlas;
    uint   OverrideMask;   // hash slots - 1; 0 = no overrides
    uint   OverrideBase;
    uint   TransBase;
    uint   Pass;
};

[[vk::combinedImageSampler]] Texture2DArray<float4> Atlas    : register(t0, space2);
[[vk::combinedImageSampler]] SamplerState           AtlasSmp : register(s0, space2);
[[vk::combinedImageSampler]] Texture2D<float4>      HueLut   : register(t1, space2);
[[vk::combinedImageSampler]] SamplerState           HueSmp   : register(s1, space2);
// Indexed8 anim pages (R8_UINT palette indices; Load only, the sampler is unused) and the palettes.bin table
// (u16 B5G5R5A1 entries, palette p entry i at byte (p*256 + i)*2). Storage buffers follow the sampled textures.
[[vk::combinedImageSampler]] Texture2DArray<uint>   AnimIdx  : register(t2, space2);
[[vk::combinedImageSampler]] SamplerState           AnimSmp  : register(s2, space2);
ByteAddressBuffer                                    Palettes : register(t3, space2);

struct VSOut
{
    float4 pos : SV_Position;
    float3 uvw : TEXCOORD0;
    nointerpolation uint2 hueFlags : TEXCOORD1; // x = hue row | palette id << 16, y = flags | mode << 12 | alpha << 16
    float  light : TEXCOORD2;
    nointerpolation float atlasSize : TEXCOORD3; // the fragment stage can't read the vertex cbuffer (space1)
    nointerpolation float grayTol : TEXCOORD4;
};

static const uint Corner[6] = { 0, 1, 2, 2, 1, 3 };
static const float2 Diamond[4] = { float2(22, 0), float2(44, 22), float2(0, 22), float2(22, 44) };

// Must match OverrideEntry.Hash in GpuTypes.cs.
uint OverrideHash(uint i)
{
    i ^= i >> 16;
    i *= 0x7FEB352Du;
    i ^= i >> 15;
    return i;
}

VSOut Collapsed()
{
    VSOut o;
    o.pos = float4(-2, -2, 0, 1);
    o.uvw = 0; o.hueFlags = 0; o.light = 0; o.atlasSize = 1; o.grayTol = 0;
    return o;
}

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    uint gi;
    if (Pass != 0)
    {
        gi = FrameTable[TransBase + iid].x;
    }
    else
    {
        // Which visible chunk does this instance belong to? Binary search the running counts.
        uint lo = 0, hi = ChunkCount;
        while (hi - lo > 1)
        {
            uint mid = (lo + hi) >> 1;
            if (FrameTable[mid].y <= iid) lo = mid; else hi = mid;
        }
        uint2 ce = FrameTable[lo];
        gi = ce.x + (iid - ce.y);
    }
    Instance inst = Instances[gi];

    uint flags = inst.whf >> 24;
    uint hueRow = inst.pageHue >> 16;
    bool lit = (flags & FLAG_LIT) != 0;
    uint alpha = lit ? 255u : 255u - (inst.light & 0xFFu);
    uint mode = lit ? MODE_NORMAL : (inst.light >> 8) & 0xFu;
    if (OverrideMask != 0)
    {
        uint h = OverrideHash(gi);
        [loop] for (uint p = 0; p < 8; p++)
        {
            uint2 e = FrameTable[OverrideBase + ((h + p) & OverrideMask)];
            if (e.x == 0) break;
            if (e.x == gi + 1)
            {
                uint ovFlags = (e.y >> 8) & 0xFFu;
                alpha = (ovFlags & OV_HIDDEN) != 0 ? 0u : alpha * (e.y & 0xFFu) / 255u;
                if (ovFlags & OV_HIGHLIGHT)
                {
                    hueRow = e.y >> 16;
                    flags = (flags | FLAG_HUED) & ~FLAG_PARTIAL;
                }
                break;
            }
        }
    }
    bool translucent = alpha < 255u || mode == MODE_SPECTRAL || mode == MODE_SHADOW;
    uint layer = PageTable[inst.pageHue & 0xFFFF];
    // Not resident, hidden, or the other pass's: collapse to a degenerate triangle.
    if (layer == 0xFFFFFFFFu || alpha == 0u || translucent != (Pass != 0))
        return Collapsed();

    VSOut o;
    uint c = Corner[vid];
    float2 cxy = float2(c & 1, c >> 1);
    float w = inst.whf & 0xFFF;
    float h = (inst.whf >> 12) & 0xFFF;

    float2 local;
    float light = 1.0;
    if (flags & FLAG_STRETCH)
    {
        int z = (int)(inst.corners << (24 - 8 * c)) >> 24; // sign-extend byte c
        local = Diamond[c] - float2(0, z * 4);
    }
    else if (mode == MODE_SHADOW)
    {
        // Batcher2D.DrawShadow: half height, top edge sheared right by the half height, raised 10 px.
        float hh = h * 0.5;
        local = float2(cxy.x * w + (1.0 - cxy.y) * hh, hh - 10.0 + cxy.y * hh);
    }
    else
    {
        local = cxy * float2(w, h);
    }
    if (lit)
        light = ((inst.light >> (8 * c)) & 0xFF) / 170.0;
    bool indexed = (flags & FLAG_INDEXED) != 0;

    float2 world = float2(int2(inst.x, inst.y) - CamPx) - CamFrac + local;
    float2 ndc = world * Scale * 2.0 * InvViewport;
    float depth = 0.5 - (float)(int)(inst.depth - CamDepth) * DepthScale;

    o.pos = float4(ndc.x, -ndc.y, saturate(depth), 1);
    float2 tc = cxy;
    if (flags & FLAG_MIRROR) tc.x = 1.0 - tc.x;
    float2 uv = float2(inst.uv & 0xFFFF, inst.uv >> 16) + tc * float2(w, h);
    o.uvw = float3(uv * (indexed ? InvAnimAtlas : InvAtlas), layer);
    uint pal = indexed ? (inst.corners & 0xFFFF) : 0u;
    o.hueFlags = uint2(hueRow | (pal << 16),
        flags | (Sharp != 0 ? FLAG_SHARP : 0u) | ((FormatFlags & 3u) << 9) | (mode << 12) | (alpha << 16));
    o.light = light;
    o.atlasSize = indexed ? AnimAtlasSize : AtlasSize;
    o.grayTol = GrayTolerance;
    return o;
}

// Indexed8 tap: palette colour as 5-bit integers, hued per tap (exact r5 == g5 == b5 gray test, shade = r5, or g5 for
// effect hues). Spectral and shadow ignore the hue.
float3 IndexedTap(uint idx, uint pal, uint hueRow, uint flags, uint mode)
{
    uint addr = (pal * 256u + idx) * 2u;
    uint c = (Palettes.Load(addr & ~3u) >> ((addr & 2u) * 8u)) & 0xFFFFu;
    uint r5 = (c >> 10) & 31u, g5 = (c >> 5) & 31u, b5 = c & 31u;
    if ((flags & FLAG_HUED) != 0)
    {
        if (mode == MODE_EFFECT && (flags & FLAG_PARTIAL) == 0)
            return HueLut.Load(int3(g5, hueRow, 0)).rgb;
        if (mode == MODE_NORMAL && ((flags & FLAG_PARTIAL) == 0 || (r5 == g5 && r5 == b5)))
            return HueLut.Load(int3(r5, hueRow, 0)).rgb;
    }
    // canonical 5->8 expansion: bit replication c<<3|c>>2 (matches native B5G5R5A1 sampling), as k/255 so an
    // 8-bit target stores k exactly
    uint3 c5 = uint3(r5, g5, b5);
    return float3((c5 << 3) | (c5 >> 2)) / 255.0;
}

// Spectral / shadow / alpha, premultiplied (the opaque pass always has alpha 1).
float4 Finish(float3 rgb, uint packed)
{
    uint mode = (packed >> 12) & 0xFu;
    float a = ((packed >> 16) & 0xFFu) / 255.0;
    if (mode == MODE_SPECTRAL) { a = saturate(1.0 - rgb.r * 1.5); rgb = 0; }
    else if (mode == MODE_SHADOW) { a = 0.4; rgb = 0; }
    return float4(rgb * a, a);
}

float4 PSIndexed(VSOut i, uint flags)
{
    // Manual bilinear over palette-resolved taps, all at mip0 (the 2-px gutter keeps the footprint in the frame's cell,
    // so the instance's palette is right for every tap). Sharp mode remaps the fraction as the hardware path does:
    // at integer scales the fragment sits on texel centres and only one tap has weight.
    float2 t = i.uvw.xy * i.atlasSize;
    float2 q;
    if (flags & FLAG_SHARP)
    {
        float2 fw = max(fwidth(t), 1e-4);
        float2 d = frac(t) - 0.5;
        float2 off = sign(d) * 0.5 * saturate((abs(d) - 0.5) / (fw * 0.5) + 1.0);
        q = floor(t) + off;
    }
    else q = t - 0.5;
    int2 p0 = (int2)floor(q);
    float2 f = q - floor(q);
    int layer = (int)i.uvw.z;
    uint hueRow = i.hueFlags.x & 0xFFFFu, pal = i.hueFlags.x >> 16;
    uint mode = (flags >> 12) & 0xFu;
    // One tap holds (nearly) all the weight, as in sharp mode away from texel edges (zoom 1 and other integer scales):
    // one index and one palette load. The interpolated position is rarely exact, so snap within 1/256 of a texel; the
    // dropped weights stay under 0.4%, below one 8-bit step of the blended colour.
    float2 fr = round(f);
    if (all(abs(f - fr) < 1.0 / 256))
    {
        uint i0 = AnimIdx.Load(int4(p0 + (int2)fr, layer, 0));
        if (i0 == 0u)
            discard;
        return Finish(IndexedTap(i0, pal, hueRow, flags, mode) * i.light, flags);
    }
    float4 wq = float4((1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y);
    uint4 idx = uint4(AnimIdx.Load(int4(p0, layer, 0)), AnimIdx.Load(int4(p0 + int2(1, 0), layer, 0)),
                      AnimIdx.Load(int4(p0 + int2(0, 1), layer, 0)), AnimIdx.Load(int4(p0 + int2(1, 1), layer, 0)));
    float3 rgb = 0;
    float a = 0;
    [unroll] for (int k = 0; k < 4; k++)
    {
        if (idx[k] == 0u || wq[k] <= 0) continue; // index 0 = transparent
        rgb += wq[k] * IndexedTap(idx[k], pal, hueRow, flags, mode);
        a += wq[k];
    }
    if (a < 0.5)
        discard;
    return Finish(rgb / a * i.light, flags);
}

float4 PSMain(VSOut i) : SV_Target
{
    if (i.hueFlags.y & FLAG_INDEXED)
        return PSIndexed(i, i.hueFlags.y);
    float4 c;
    if (i.hueFlags.y & FLAG_SHARP)
    {
        // Sharp bilinear: hold the texel centre, blend only across the half screen pixel on each side of a
        // texel edge. Identical to point sampling at integer scales; at fractional scales (wheel zoom, HiDPI
        // density 1.25/1.5) it avoids the texel-pattern crawl that point sampling shows while panning.
        float2 t = i.uvw.xy * i.atlasSize;
        float2 fw = max(fwidth(t), 1e-4);
        float2 d = frac(t) - 0.5;
        float2 off = sign(d) * 0.5 * saturate((abs(d) - 0.5) / (fw * 0.5) + 1.0);
        float2 uv = (floor(t) + 0.5 + off) / i.atlasSize;
        c = Atlas.SampleLevel(AtlasSmp, float3(uv, i.uvw.z), 0);
    }
    else
    {
        c = Atlas.Sample(AtlasSmp, i.uvw);
    }
    // UO art is 1-bit alpha: alpha test. Transparent texels are black, so a filtered edge texel is
    // effectively premultiplied; un-premultiply to avoid dark fringes when zoomed out.
    if (c.a < 0.5)
        discard;
    uint flags = i.hueFlags.y;
    if (flags & FLAG_UNPREMUL)
        c.rgb /= c.a;

    uint mode = (flags >> 12) & 0xFu;
    if ((flags & FLAG_HUED) != 0)
    {
        bool gray;
        if (flags & FLAG_GRAYTOL)
            gray = max(abs(c.r - c.g), max(abs(c.r - c.b), abs(c.g - c.b))) <= i.grayTol;
        else
            gray = c.r == c.g && c.r == c.b;
        // ClassicUO get_rgb: the gray level (red channel; green for EFFECT_HUED) selects one of 32 shades of the hue row.
        if (mode == MODE_EFFECT && (flags & FLAG_PARTIAL) == 0)
            c.rgb = HueLut.Load(int3(min(31, (int)(c.g * 32.0)), i.hueFlags.x & 0xFFFFu, 0)).rgb;
        else if (mode == MODE_NORMAL && ((flags & FLAG_PARTIAL) == 0 || gray))
            c.rgb = HueLut.Load(int3(min(31, (int)(c.r * 32.0)), i.hueFlags.x & 0xFFFFu, 0)).rgb;
    }
    c.rgb *= i.light;
    return Finish(c.rgb, flags);
}
