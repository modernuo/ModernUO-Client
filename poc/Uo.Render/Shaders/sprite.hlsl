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
//   uint light     4 x u8 per-corner light (stretched land), 170 = 1.0
//   uint corners   4 x s8 per-corner z (stretched land), corner order top,right,left,bottom

#define FLAG_HUED     1u
#define FLAG_PARTIAL  2u
#define FLAG_STRETCH  4u
#define FLAG_LIT      8u
#define FLAG_SHARP    0x100u  // set by the vertex shader from the Sharp uniform
#define FLAG_UNPREMUL 0x200u  // FormatFlags bit 0 << 9
#define FLAG_GRAYTOL  0x400u  // FormatFlags bit 1 << 9
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
StructuredBuffer<uint2>    ChunkTable : register(t1, space0); // x = first instance in Instances, y = running instance count before this chunk
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
};

[[vk::combinedImageSampler]] Texture2DArray<float4> Atlas    : register(t0, space2);
[[vk::combinedImageSampler]] SamplerState           AtlasSmp : register(s0, space2);
[[vk::combinedImageSampler]] Texture2D<float4>      HueLut   : register(t1, space2);
[[vk::combinedImageSampler]] SamplerState           HueSmp   : register(s1, space2);

struct VSOut
{
    float4 pos : SV_Position;
    float3 uvw : TEXCOORD0;
    nointerpolation uint2 hueFlags : TEXCOORD1;
    float  light : TEXCOORD2;
    nointerpolation float atlasSize : TEXCOORD3; // the fragment stage can't read the vertex cbuffer (space1)
    nointerpolation float grayTol : TEXCOORD4;
};

static const uint Corner[6] = { 0, 1, 2, 2, 1, 3 };
static const float2 Diamond[4] = { float2(22, 0), float2(44, 22), float2(0, 22), float2(22, 44) };

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    // Which visible chunk does this instance belong to? Binary search the running counts.
    uint lo = 0, hi = ChunkCount;
    while (hi - lo > 1)
    {
        uint mid = (lo + hi) >> 1;
        if (ChunkTable[mid].y <= iid) lo = mid; else hi = mid;
    }
    uint2 ce = ChunkTable[lo];
    Instance inst = Instances[ce.x + (iid - ce.y)];

    VSOut o;
    uint layer = PageTable[inst.pageHue & 0xFFFF];
    if (layer == 0xFFFFFFFFu)
    {
        // Page not resident: collapse to a degenerate triangle.
        o.pos = float4(-2, -2, 0, 1);
        o.uvw = 0; o.hueFlags = 0; o.light = 0; o.atlasSize = 1; o.grayTol = 0;
        return o;
    }

    uint c = Corner[vid];
    float2 cxy = float2(c & 1, c >> 1);
    float w = inst.whf & 0xFFF;
    float h = (inst.whf >> 12) & 0xFFF;
    uint flags = inst.whf >> 24;

    float2 local;
    float light = 1.0;
    if (flags & FLAG_STRETCH)
    {
        int z = (int)(inst.corners << (24 - 8 * c)) >> 24; // sign-extend byte c
        local = Diamond[c] - float2(0, z * 4);
    }
    else
    {
        local = cxy * float2(w, h);
    }
    if (flags & FLAG_LIT)
        light = ((inst.light >> (8 * c)) & 0xFF) / 170.0;

    float2 world = float2(int2(inst.x, inst.y) - CamPx) - CamFrac + local;
    float2 ndc = world * Scale * 2.0 * InvViewport;
    float depth = 0.5 - (float)(int)(inst.depth - CamDepth) * DepthScale;

    o.pos = float4(ndc.x, -ndc.y, saturate(depth), 1);
    float2 uv = float2(inst.uv & 0xFFFF, inst.uv >> 16) + cxy * float2(w, h);
    o.uvw = float3(uv * InvAtlas, layer);
    o.hueFlags = uint2(inst.pageHue >> 16, flags | (Sharp != 0 ? FLAG_SHARP : 0u) | ((FormatFlags & 3u) << 9));
    o.light = light;
    o.atlasSize = AtlasSize;
    o.grayTol = GrayTolerance;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
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

    bool gray;
    if (flags & FLAG_GRAYTOL)
        gray = max(abs(c.r - c.g), max(abs(c.r - c.b), abs(c.g - c.b))) <= i.grayTol;
    else
        gray = c.r == c.g && c.r == c.b;
    if ((flags & FLAG_HUED) != 0 && ((flags & FLAG_PARTIAL) == 0 || gray))
    {
        // ClassicUO get_rgb: grayscale (red channel) selects one of 32 shades of the hue row.
        int shade = min(31, (int)(c.r * 32.0));
        c.rgb = HueLut.Load(int3(shade, i.hueFlags.x, 0)).rgb;
    }
    c.rgb *= i.light;
    return float4(c.rgb, 1);
}
