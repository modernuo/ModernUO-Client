#define NONE 0
#define HUED 1
#define PARTIAL_HUED 2
#define HUE_TEXT_NO_BLACK 3
#define HUE_TEXT 4
#define LAND 5
#define LAND_COLOR 6
#define SPECTRAL 7
#define SHADOW 8
#define LIGHTS 9
#define EFFECT_HUED 10
#define GUMP 20

const static float3 LIGHT_DIRECTION = float3(0.0f, 1.0f, 1.0f);

const static float HUE_ROWS = 1024;
const static float HUE_COLUMNS = 16;
const static float HUE_WIDTH = 32;
const static float HUES_PER_TEXTURE = HUE_ROWS * HUE_COLUMNS;

float4x4 MatrixTransform;
float4x4 WorldMatrix;
float2 Viewport;
float Brightlight;
float CircleOfTransparencyRadius;

sampler DrawSampler : register(s0);
sampler HueSampler0 : register(s1);
sampler HueSampler1 : register(s2);

struct VS_INPUT
{
	float4 Position : POSITION0;
	float3 Normal	: NORMAL0;
	float3 TexCoord : TEXCOORD0;
	float3 Hue		: TEXCOORD1;
};

struct PS_INPUT
{
	float4 Position : POSITION0;
	float3 TexCoord : TEXCOORD0;
	float3 Normal	: TEXCOORD1;
	float3 Hue		: TEXCOORD2;
	float3 PixelPos : TEXCOORD3;
};

float3 get_rgb(float gray, float hue)
{
    float halfPixelX = (1.0f / (HUE_COLUMNS * HUE_WIDTH)) * 0.5f;
    float hueColumnWidth = 1.0f / HUE_COLUMNS;
    float hueStart = frac(hue / HUE_COLUMNS);
    
    float xPos = hueStart + gray / HUE_COLUMNS;
    xPos = clamp(xPos, hueStart + halfPixelX, hueStart + hueColumnWidth - halfPixelX);
    float yPos = (hue % HUES_PER_TEXTURE) / (HUES_PER_TEXTURE - 1);
    return tex2D(HueSampler0, float2(xPos, yPos)).rgb;
}

float get_light(float3 norm)
{
	float3 light = normalize(LIGHT_DIRECTION);
	float3 normal = normalize(norm);
	float base = (max(dot(normal, light), 0.0f) / 2.0f) + 0.5f;

	// At 45 degrees (the angle the flat tiles are lit at) it must come out
	// to (cos(45) / 2) + 0.5 or 0.85355339...
	return base + ((Brightlight * (base - 0.85355339f)) - (base - 0.85355339f));
}

float3 get_colored_light(float shader, float gray)
{
	float2 texcoord = float2(gray, (shader - 0.5) / 63);

	return tex2D(HueSampler1, texcoord).rgb;
}

PS_INPUT VertexShaderFunction(VS_INPUT IN)
{
	PS_INPUT OUT;
	
	OUT.Position = mul(mul(IN.Position, WorldMatrix), MatrixTransform);
	
	OUT.Position.x -= 0.5 / Viewport.x;
	OUT.Position.y += 0.5 / Viewport.y;

	OUT.TexCoord = IN.TexCoord;
	OUT.Normal = IN.Normal;
	OUT.Hue = IN.Hue;
	OUT.PixelPos = OUT.Position.xyz;

	return OUT;
}

float4 PixelShader_Hue(PS_INPUT IN) : COLOR0
{	
	float4 color = tex2D(DrawSampler, IN.TexCoord.xy);
		
	if (color.a == 0.0f)
		discard;

	int mode = int(IN.Hue.y);
	float alpha = IN.Hue.z;

	bool useTrans = false;
	if (alpha > 1.0f)
	{
		useTrans = true;
		alpha -= 1.0f;
	}

	if (alpha == 0.0f)
		discard;

	float hue = IN.Hue.x;

	if (mode >= GUMP)
	{
		mode -= GUMP;

		if (color.r < 0.02f)
		{
			hue = 0;
		}
	}

	if (mode == HUED || (mode == PARTIAL_HUED && color.r == color.g && color.r == color.b))
	{
		color.rgb = get_rgb(color.r, hue);
	}
	else if (mode == HUE_TEXT_NO_BLACK)
	{
		if (color.r > 0.04f || color.g > 0.04f || color.b > 0.04f)
		{
			color.rgb = get_rgb(1.0f, hue);
		}
	}
	else if (mode == HUE_TEXT)
	{
		// 31 is max red, so this is just selecting the color of the darkest pixel in the hue
		color.rgb = get_rgb(1.0f, hue);
	}
	else if (mode == LAND)
	{
		color.rgb *= get_light(IN.Normal);
	}
	else if (mode == LAND_COLOR)
	{
		color.rgb = get_rgb(color.r, hue) * get_light(IN.Normal);
	}
	else if (mode == SPECTRAL)
	{
		alpha = 1.0f - (color.r * 1.5f);
		color.r = 0;
		color.g = 0;
		color.b = 0;
	}
	else if (mode == SHADOW)
	{
		alpha = 0.4f;
		color.r = 0;
		color.g = 0;
		color.b = 0;
	}
	else if (mode == LIGHTS)
	{
		color.rgb = get_colored_light(IN.Hue.x - 1, color.r);
	}
	else if (mode == EFFECT_HUED)
	{
		color.rgb = get_rgb(color.g, hue);
	}

	if (useTrans && CircleOfTransparencyRadius > 0)
	{
		float2 pixelDist = IN.PixelPos.xy * Viewport * 0.5;
		float ratio = length(pixelDist) / CircleOfTransparencyRadius;

		if (ratio < 0.85f)
			discard;

		if (ratio < 1.0f)
		{
			float t = (ratio - 0.85f) / 0.15f;
			alpha *= t * t * t;

			if (alpha < 0.02f)
				discard;
		}
	}

	return color * alpha;
}

technique HueTechnique
{
	pass p0
	{
		VertexShader = compile vs_3_0 VertexShaderFunction();
		PixelShader = compile ps_3_0 PixelShader_Hue();
	}
}

// clip-4 depth model (switch depth_model; GeoDepth.cs and sprite.hlsl GeoDepth are the same maths): HueGeoTechnique writes
// the geometric per-pixel depth, so it loses early depth rejection; HueTechnique above is unchanged and drawn when the switch
// is off. The shape class rides in TexCoord.z (0: the depth key; else class * 64 + 32 + base - key, LegacyGeo.Encode) and
// the class's reference column/row in Normal.xy, in the vertex's own coordinates (LegacyGeo.Apply).
float GeoCam; // the integer tile sum the frame's depths are relative to (LegacyGeo.CamSum)

static const float GEO_SCALE = 1.0 / 2500.0; // WorldRenderer.GeoScale: tile sums -> depth, +-1250 around GeoCam
static const float GEO_KEY = -0.77;          // the key's tile sum: Gpu's (D - Dcam) / 100 + 0.5 for D = 100 key

struct PS_GEO_INPUT
{
	float4 Position : POSITION0;
	float3 TexCoord : TEXCOORD0;
	float3 Normal	: TEXCOORD1;
	float3 Hue		: TEXCOORD2;
	float3 PixelPos : TEXCOORD3;
	float4 Geo		: TEXCOORD4; // x, y: pixel - reference column/row; z: key - GeoCam; w: TexCoord.z
};

PS_GEO_INPUT VertexShaderGeo(VS_INPUT IN)
{
	PS_INPUT v = VertexShaderFunction(IN);
	PS_GEO_INPUT OUT;

	OUT.Position = v.Position;
	OUT.TexCoord = v.TexCoord;
	OUT.Normal = v.Normal;
	OUT.Hue = v.Hue;
	OUT.PixelPos = v.PixelPos;
	OUT.Geo = float4(IN.Position.x - IN.Normal.x, IN.Position.y - IN.Normal.y, IN.Position.z - GeoCam, IN.TexCoord.z);

	return OUT;
}

// the tile sum of the visible surface, relative to GeoCam (LegacyGeo.PixelSum is the same)
float GeoSum(float4 g)
{
	if (g.w < 32.0)
		return g.z + GEO_KEY;

	float cls = floor(g.w / 64.0);
	float b = g.z + (g.w - cls * 64.0 - 32.0);
	float s = abs(g.x) / 22.0;
	float v = g.y / 22.0;

	if (cls == 2.0) // box: max(back edges, min(top plane, tile centre))
		return b + max(s, min(v, 1.0));
	if (cls == 3.0) // wall: the back edges
		return b + s;
	if (cls == 4.0) // flat: its plane
		return b + v;
	if (cls == 5.0) // mobile composite: billboard, ground plane below the centre row, own tile's front faces
		return b + max(0.0, min(v, 1.0 - s));

	return b; // 1: linear (land corners)
}

float4 PixelShader_HueGeo(PS_GEO_INPUT IN, out float depth : DEPTH0) : COLOR0
{
	depth = saturate(0.5 - GeoSum(IN.Geo) * GEO_SCALE);

	PS_INPUT p;
	p.Position = IN.Position;
	p.TexCoord = IN.TexCoord;
	p.Normal = IN.Normal;
	p.Hue = IN.Hue;
	p.PixelPos = IN.PixelPos;

	return PixelShader_Hue(p);
}

technique HueGeoTechnique
{
	pass p0
	{
		VertexShader = compile vs_3_0 VertexShaderGeo();
		PixelShader = compile ps_3_0 PixelShader_HueGeo();
	}
}
