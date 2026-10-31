using static SDL3.SDL;

namespace Uo.Render;

/// <summary>How the renderer gets its device and colour target.</summary>
public enum RendererMode
{
    /// <summary>Own device; claims the window and draws into the swapchain.</summary>
    Window,
    /// <summary>Own device; draws into its own offscreen texture (screenshots, benches).</summary>
    Offscreen,
    /// <summary>A device owned by someone else (FNA3D); draws into the texture passed with each frame. Never touches the
    /// window or swapchain.</summary>
    External,
}

/// <summary>SDL_GPU device, pipelines and persistent buffers. The world pass is one draw call for opaque sprites plus
/// one for the (usually few) translucent ones.</summary>
public sealed unsafe class Renderer : IDisposable
{
    public readonly IntPtr Device, Window;
    public readonly RendererMode Mode;
    public readonly bool OwnsDevice;
    public bool Offscreen => Mode == RendererMode.Offscreen;
    /// <summary>Swapchain / offscreen format, or the external target's format at creation (pipelines for other target
    /// formats are built on first use).</summary>
    public readonly SDL_GPUTextureFormat ColorFormat;
    IntPtr _vs, _ps;
    readonly List<(SDL_GPUTextureFormat Format, IntPtr Opaque, IntPtr Translucent)> _pipelines = new(2);
    // translucent-pass pipelines for the effect blends other than Normal/Screen, built on first use
    readonly List<(SDL_GPUTextureFormat Format, InstanceBlend Blend, IntPtr Pipeline)> _blendPipelines = new();
    IntPtr _depth;
    uint _depthW, _depthH;
    readonly IntPtr _samplerPoint, _samplerLinear, _samplerHue;
    readonly IntPtr _hueTex;
    readonly IntPtr _paletteBuf, _dummyAnimTex;
    public readonly IntPtr InstanceBuffer;
    IntPtr _frameTableBuf;
    readonly IntPtr _pageTableBuf;
    readonly IntPtr _frameTransfer, _pageTableTransfer;
    public readonly uint FrameTransferSize;
    /// <summary>Frame table layout (8-byte entries): visible chunks [0, ChunkTableCapacity), translucent list
    /// [TransBase, + MaxTranslucent), override hash [OverrideBase, + OverrideSlots).</summary>
    public readonly int ChunkTableCapacity, OverrideSlots;
    /// <summary>Translucent list entries the frame table holds; grows (<see cref="EnsureTranslucentCapacity"/>).</summary>
    public int MaxTranslucent { get; private set; }
    /// <summary>The most translucent entries <see cref="EnsureTranslucentCapacity"/> grows to (2 MB of frame table; the
    /// 8 MB frame transfer buffer keeps room for chunk uploads).</summary>
    public const int TranslucentLimit = 1 << 18;
    /// <summary>Times the frame table was regrown for more translucent entries.</summary>
    public int TranslucentGrowths;
    public uint TransBase => (uint)ChunkTableCapacity;
    public uint OverrideBase => (uint)(ChunkTableCapacity + MaxTranslucent);
    readonly int _pageTableBytes;
    public bool VSync = true;
    public readonly bool ImmediateSupported, MailboxSupported;
    public readonly SDL_GPUShaderFormat ShaderFormat;

    // offscreen target (screenshot mode)
    IntPtr _offscreen;
    uint _offW, _offH;

    // Key step = 2^-22 in NDC z: >= 4 float ULPs around 0.5, so rasterizer z interpolation can't merge adjacent keys.
    // Range: |key - camKey| < 2^21 covers a 4K view at zoom 0.5 (about ±136 diagonals × 100 × 128).
    public const float DepthScale = 1f / (1 << 22);

    static SDL_GPUShaderFormat EmbeddedFormats()
    {
        var formats = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL;
        if (typeof(Renderer).Assembly.GetManifestResourceInfo("sprite_vs.spv") != null) formats |= SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV;
        return formats;
    }

    static IntPtr CreateDevice(string driver)
    {
        var d = SDL_CreateGPUDevice(EmbeddedFormats(), false, driver);
        if (d == IntPtr.Zero) throw new Exception($"SDL_CreateGPUDevice({driver ?? "default"}): " + SDL_GetError());
        return d;
    }

    /// <summary>Own device. <paramref name="driver"/>: "direct3d12", "vulkan", or null for SDL's choice among drivers we
    /// have shaders for.</summary>
    public Renderer(IntPtr window, bool offscreen, PackSet pack, int instanceCapacity, int maxVisibleChunks, string driver = "direct3d12",
        int maxTranslucent = 0, int overrideSlots = 0)
        : this(CreateDevice(driver), true, window, offscreen ? RendererMode.Offscreen : RendererMode.Window, pack, instanceCapacity, maxVisibleChunks, maxTranslucent, overrideSlots)
    {
        if (!offscreen)
        {
            if (!SDL_ClaimWindowForGPUDevice(Device, window)) throw new Exception("claim window: " + SDL_GetError());
            ImmediateSupported = SDL_WindowSupportsGPUPresentMode(Device, window, SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_IMMEDIATE);
            MailboxSupported = SDL_WindowSupportsGPUPresentMode(Device, window, SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX);
            ColorFormat = SDL_GetGPUSwapchainTextureFormat(Device, window);
        }
        else
        {
            ColorFormat = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;
        }
    }

    /// <summary>Renders with a device owned by the host (FNA3D's SDL_GPUDevice*). The host keeps the window and
    /// swapchain; each frame passes its colour target in <see cref="FrameInput"/>. Shaders are picked from the formats
    /// the device accepts (DXIL on D3D12, SPIR-V on Vulkan).</summary>
    public static Renderer CreateExternal(IntPtr device, SDL_GPUTextureFormat colorFormat, PackSet pack, int instanceCapacity, int maxVisibleChunks,
        int maxTranslucent = 4096, int overrideSlots = 8192)
    {
        if (device == IntPtr.Zero) throw new ArgumentException("no SDL_GPUDevice", nameof(device));
        var r = new Renderer(device, false, IntPtr.Zero, RendererMode.External, pack, instanceCapacity, maxVisibleChunks, maxTranslucent, overrideSlots, colorFormat);
        try
        {
            r.Pipelines(colorFormat);
            // the effect-blend pipelines too, so the first such effect doesn't build one mid-frame
            foreach (var b in new[] { InstanceBlend.Multiply, InstanceBlend.ScreenLess, InstanceBlend.NormalHalf, InstanceBlend.ShadowBlue })
                r.BlendPipeline(colorFormat, b);
        }
        catch
        {
            // the caller never gets r: release its buffers, textures and the pipelines built so far
            r.Dispose();
            throw;
        }
        return r;
    }

    Renderer(IntPtr device, bool ownsDevice, IntPtr window, RendererMode mode, PackSet pack, int instanceCapacity, int maxVisibleChunks,
        int maxTranslucent, int overrideSlots, SDL_GPUTextureFormat colorFormat = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_INVALID)
    {
        Device = device;
        OwnsDevice = ownsDevice;
        Window = window;
        Mode = mode;
        ColorFormat = colorFormat;
        if (overrideSlots != 0 && (overrideSlots & (overrideSlots - 1)) != 0) throw new ArgumentException("override slots must be a power of two");
        var have = SDL_GetGPUShaderFormats(Device) & EmbeddedFormats();
        if ((have & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL) != 0) ShaderFormat = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL;
        else if ((have & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV) != 0) ShaderFormat = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV;
        else throw new NotSupportedException($"device {SDL_GetGPUDeviceDriver(Device)} accepts shader formats {SDL_GetGPUShaderFormats(Device)}; embedded: {EmbeddedFormats()}");

        _vs = CreateShader("sprite_vs", "VSMain", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 3, 1);
        _ps = CreateShader("sprite_ps", "PSMain", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 3, 1, 0);

        _samplerPoint = CreateSampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST, SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST, 0);
        // zoomed out: trilinear, limited to mips the 2-px gutter still protects (sprite.hlsl LINEAR_MAX_LOD must match)
        _samplerLinear = CreateSampler(SDL_GPUFilter.SDL_GPU_FILTER_LINEAR, SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR, 1.5f);
        _samplerHue = CreateSampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST, SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST, 0);

        InstanceBuffer = CreateBuffer((uint)(instanceCapacity * sizeof(SpriteInstance)), "instances");
        ChunkTableCapacity = maxVisibleChunks;
        MaxTranslucent = maxTranslucent;
        OverrideSlots = overrideSlots;
        _frameTableBuf = CreateBuffer((uint)((maxVisibleChunks + maxTranslucent + overrideSlots) * sizeof(ChunkDraw)), "frame table");
        _pageTableBytes = Math.Max(1, pack.TablePageCount) * 4;
        _pageTableBuf = CreateBuffer((uint)_pageTableBytes, "page table");

        FrameTransferSize = 8u << 20;
        _frameTransfer = CreateTransfer(FrameTransferSize, true);
        _pageTableTransfer = CreateTransfer((uint)_pageTableBytes, true);

        _hueTex = CreateHueTexture(pack);
        _paletteBuf = CreatePaletteBuffer(pack);
        _dummyAnimTex = CreateNamedTexture(Device, new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D_ARRAY, format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8_UINT,
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER, width = 1, height = 1, layer_count_or_depth = 1, num_levels = 1,
        }, "Uo.Render dummy anim pages");
    }

    /// <summary>palettes.bin's u16 table as one storage buffer (18.5 MiB for all 37.9k palettes), uploaded once. The file is
    /// read straight into the mapped transfer buffer: no CPU copy of the palettes is allocated or kept (the GPU buffer is
    /// their only reader; a new renderer reads the file again).</summary>
    IntPtr CreatePaletteBuffer(PackSet pack)
    {
        long dataBytes = pack.PalettePath != null ? (long)pack.PaletteCount * Uo.Pack.AnimPaletteFile.Entries * 2 : 0;
        uint bytes = (uint)Math.Max(4, (dataBytes + 3) & ~3);
        var buf = CreateBuffer(bytes, "palettes");
        var tb = CreateTransfer(bytes, true);
        var ptr = SDL_MapGPUTransferBuffer(Device, tb, false);
        new Span<byte>((void*)ptr, (int)bytes).Clear();
        if (dataBytes > 0)
        {
            using var fs = new FileStream(pack.PalettePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            fs.Position = pack.PaletteOffset;
            fs.ReadExactly(new Span<byte>((void*)ptr, (int)dataBytes));
        }
        SDL_UnmapGPUTransferBuffer(Device, tb);
        var cmd = SDL_AcquireGPUCommandBuffer(Device);
        var copy = SDL_BeginGPUCopyPass(cmd);
        var src2 = new SDL_GPUTransferBufferLocation { transfer_buffer = tb };
        var dst = new SDL_GPUBufferRegion { buffer = buf, size = bytes };
        SDL_UploadToGPUBuffer(copy, in src2, in dst, false);
        SDL_EndGPUCopyPass(copy);
        SDL_SubmitGPUCommandBuffer(cmd);
        SDL_ReleaseGPUTransferBuffer(Device, tb);
        return buf;
    }

    /// <summary>Makes room for <paramref name="count"/> translucent entries (at most <see cref="TranslucentLimit"/>) by
    /// recreating the frame table at the next power of two; call between frames. The old buffer is released (SDL_GPU
    /// destroys it after the command buffers using it finish), and the table is uploaded in full every frame. Returns
    /// false when <paramref name="count"/> exceeds the limit (the capacity is then the limit).</summary>
    public bool EnsureTranslucentCapacity(int count)
    {
        if (count <= MaxTranslucent) return true;
        int cap = (int)Math.Min(TranslucentLimit, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, 1024)));
        if (cap <= MaxTranslucent) return false;
        var buf = CreateBuffer((uint)((ChunkTableCapacity + cap + OverrideSlots) * sizeof(ChunkDraw)), "frame table");
        SDL_ReleaseGPUBuffer(Device, _frameTableBuf);
        _frameTableBuf = buf;
        MaxTranslucent = cap;
        TranslucentGrowths++;
        return count <= cap;
    }

    IntPtr CreateBuffer(uint size, string name)
    {
        var ci = new SDL_GPUBufferCreateInfo { usage = SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ, size = size };
        var b = SDL_CreateGPUBuffer(Device, in ci);
        if (b == IntPtr.Zero) throw new Exception($"buffer {name}: " + SDL_GetError());
        SDL_SetGPUBufferName(Device, b, name);
        return b;
    }

    IntPtr CreateTransfer(uint size, bool upload)
    {
        var ci = new SDL_GPUTransferBufferCreateInfo
        {
            usage = upload ? SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD : SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD,
            size = size,
        };
        var t = SDL_CreateGPUTransferBuffer(Device, in ci);
        if (t == IntPtr.Zero) throw new Exception("transfer buffer: " + SDL_GetError());
        return t;
    }

    IntPtr CreateSampler(SDL_GPUFilter filter, SDL_GPUSamplerMipmapMode mip, float maxLod)
    {
        var ci = new SDL_GPUSamplerCreateInfo
        {
            min_filter = filter, mag_filter = filter, mipmap_mode = mip,
            address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
            address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
            address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
            min_lod = 0, max_lod = maxLod,
        };
        return SDL_CreateGPUSampler(Device, in ci);
    }

    static byte[] LoadResource(string name)
    {
        using var s = typeof(Renderer).Assembly.GetManifestResourceStream(name) ?? throw new Exception("missing shader resource " + name);
        var b = new byte[s.Length];
        s.ReadExactly(b);
        return b;
    }

    IntPtr CreateShader(string name, string entry, SDL_GPUShaderStage stage, uint samplers, uint storageBuffers, uint uniforms)
    {
        string resource = name + (ShaderFormat == SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL ? ".dxil" : ".spv");
        var code = LoadResource(resource);
        var ep = System.Text.Encoding.UTF8.GetBytes(entry + "\0");
        fixed (byte* c = code)
        fixed (byte* e = ep)
        {
            var ci = new SDL_GPUShaderCreateInfo
            {
                code_size = (UIntPtr)code.Length, code = c, entrypoint = e,
                format = ShaderFormat, stage = stage,
                num_samplers = samplers, num_storage_buffers = storageBuffers, num_uniform_buffers = uniforms,
            };
            var sh = SDL_CreateGPUShader(Device, in ci);
            if (sh == IntPtr.Zero) throw new Exception($"shader {resource}: " + SDL_GetError());
            return sh;
        }
    }

    /// <summary>Opaque and translucent pipelines for a colour target format, built on first use.</summary>
    (IntPtr Opaque, IntPtr Translucent) Pipelines(SDL_GPUTextureFormat format)
    {
        foreach (var p in _pipelines)
            if (p.Format == format) return (p.Opaque, p.Translucent);
        var opaque = CreatePipeline(format, false);
        IntPtr translucent;
        try { translucent = CreatePipeline(format, true); }
        catch { SDL_ReleaseGPUGraphicsPipeline(Device, opaque); throw; }
        var entry = (format, opaque, translucent);
        _pipelines.Add(entry);
        return (entry.Item2, entry.Item3);
    }

    /// <summary>The translucent pipeline for an effect blend (ClassicUO GameEffect's FNA BlendState: the colour factors as
    /// there, alpha One/Zero as FNA's defaults). Normal and Screen use the premultiplied pipeline.</summary>
    IntPtr BlendPipeline(SDL_GPUTextureFormat format, InstanceBlend blend)
    {
        foreach (var p in _blendPipelines)
            if (p.Format == format && p.Blend == blend) return p.Pipeline;
        if (blend == InstanceBlend.NormalOrdered) return Pipelines(format).Translucent;
        (SDL_GPUBlendFactor src, SDL_GPUBlendFactor dst, SDL_GPUBlendOp op) = blend switch
        {
            InstanceBlend.Multiply => (SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ZERO, SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_COLOR, SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD),
            InstanceBlend.ScreenLess => (SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_DST_COLOR, SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA, SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD),
            InstanceBlend.NormalHalf => (SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_DST_COLOR, SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_COLOR, SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD),
            InstanceBlend.ShadowBlue => (SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_COLOR, SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_COLOR, SDL_GPUBlendOp.SDL_GPU_BLENDOP_REVERSE_SUBTRACT),
            _ => throw new ArgumentOutOfRangeException(nameof(blend)),
        };
        var state = new SDL_GPUColorTargetBlendState
        {
            enable_blend = true,
            src_color_blendfactor = src,
            dst_color_blendfactor = dst,
            color_blend_op = op,
            src_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
            dst_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ZERO,
            alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
        };
        var pipeline = CreatePipeline(format, true, state);
        _blendPipelines.Add((format, blend, pipeline));
        return pipeline;
    }

    IntPtr CreatePipeline(SDL_GPUTextureFormat format, bool translucent, SDL_GPUColorTargetBlendState? blend = null)
    {
        var color = new SDL_GPUColorTargetDescription { format = format };
        if (blend.HasValue)
        {
            color.blend_state = blend.Value;
        }
        else if (translucent)
        {
            // premultiplied output (ClassicUO's AlphaBlend)
            color.blend_state = new SDL_GPUColorTargetBlendState
            {
                enable_blend = true,
                src_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
                dst_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                color_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
                src_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
                dst_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
            };
        }
        var ci = new SDL_GPUGraphicsPipelineCreateInfo
        {
            vertex_shader = _vs,
            fragment_shader = _ps,
            primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
            // Depth clip, not clamp: SDL maps enable_depth_clip=false to Vulkan depthClampEnable, which needs the depthClamp
            // feature, and FNA3D creates its device without it (VUID-VkPipelineRasterizationStateCreateInfo-depthClampEnable-00782).
            // The vertex shader saturates depth, so clip and clamp draw the same.
            rasterizer_state = new SDL_GPURasterizerState
            {
                fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL, cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE, enable_depth_clip = true,
            },
            depth_stencil_state = new SDL_GPUDepthStencilState
            {
                compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_LESS,
                enable_depth_test = true,
                enable_depth_write = !translucent,
            },
            target_info = new SDL_GPUGraphicsPipelineTargetInfo
            {
                color_target_descriptions = &color,
                num_color_targets = 1,
                depth_stencil_format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
                has_depth_stencil_target = true,
            },
        };
        var p = SDL_CreateGPUGraphicsPipeline(Device, in ci);
        if (p == IntPtr.Zero) throw new Exception($"pipeline ({format}): " + SDL_GetError());
        return p;
    }

    IntPtr CreateHueTexture(PackSet pack)
    {
        int rows = Math.Max(1, pack.HueCount);
        var ci = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
            format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER,
            width = 32, height = (uint)rows, layer_count_or_depth = 1, num_levels = 1,
        };
        var tex = CreateNamedTexture(Device, ci, "Uo.Render hues");
        uint bytes = (uint)(rows * 32 * 4);
        var tb = CreateTransfer(bytes, true);
        var ptr = SDL_MapGPUTransferBuffer(Device, tb, false);
        if (pack.HueCount > 0)
            fixed (uint* src = pack.Hues) Buffer.MemoryCopy(src, (void*)ptr, bytes, bytes);
        SDL_UnmapGPUTransferBuffer(Device, tb);
        var cmd = SDL_AcquireGPUCommandBuffer(Device);
        var copy = SDL_BeginGPUCopyPass(cmd);
        var src2 = new SDL_GPUTextureTransferInfo { transfer_buffer = tb, pixels_per_row = 32, rows_per_layer = (uint)rows };
        var dst = new SDL_GPUTextureRegion { texture = tex, w = 32, h = (uint)rows, d = 1 };
        SDL_UploadToGPUTexture(copy, in src2, in dst, false);
        SDL_EndGPUCopyPass(copy);
        SDL_SubmitGPUCommandBuffer(cmd);
        SDL_ReleaseGPUTransferBuffer(Device, tb);
        return tex;
    }

    public void ApplyPresentMode()
    {
        if (Mode != RendererMode.Window) return;
        var mode = VSync ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC
            : ImmediateSupported ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_IMMEDIATE
            : MailboxSupported ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX
            : SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC;
        SDL_SetGPUSwapchainParameters(Device, Window, SDL_GPUSwapchainComposition.SDL_GPU_SWAPCHAINCOMPOSITION_SDR, mode);
    }

    /// <summary>Our depth texture follows the colour target's size (recreated when the host recreates its target).</summary>
    void EnsureDepth(uint w, uint h)
    {
        if (_depth != IntPtr.Zero && _depthW == w && _depthH == h) return;
        if (_depth != IntPtr.Zero) SDL_ReleaseGPUTexture(Device, _depth);
        var ci = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
            format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
            width = w, height = h, layer_count_or_depth = 1, num_levels = 1,
        };
        _depth = CreateNamedTexture(Device, ci, $"Uo.Render depth {w}x{h}");
        _depthW = w; _depthH = h;
        DepthRecreations++;
    }

    public int DepthRecreations;

    /// <summary>SDL_CreateGPUTexture with a debug name. SDL applies names only on a debug-mode device (FNA3D: Debug builds),
    /// where they show up in validation messages and GPU crash dumps.</summary>
    internal static IntPtr CreateNamedTexture(IntPtr device, SDL_GPUTextureCreateInfo ci, string name)
    {
        uint props = SDL_CreateProperties();
        SDL_SetStringProperty(props, SDL_PROP_GPU_TEXTURE_CREATE_NAME_STRING, name);
        ci.props = props;
        var tex = SDL_CreateGPUTexture(device, in ci);
        SDL_DestroyProperties(props);
        return tex;
    }

    public void EnsureOffscreen(uint w, uint h)
    {
        if (_offscreen != IntPtr.Zero && _offW == w && _offH == h) return;
        if (_offscreen != IntPtr.Zero) SDL_ReleaseGPUTexture(Device, _offscreen);
        var ci = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
            format = ColorFormat,
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER,
            width = w, height = h, layer_count_or_depth = 1, num_levels = 1,
        };
        _offscreen = CreateNamedTexture(Device, ci, $"Uo.Render offscreen {w}x{h}");
        _offW = w; _offH = h;
    }

    public struct FrameInput
    {
        public ChunkStore Chunks;
        public PageResidency Pages;
        /// <summary>Indexed8 anim pages (null: none); its table is uploaded at <see cref="PackSet.AnimPageBase"/>.</summary>
        public PageResidency AnimPages;
        public int AnimTableBase;
        public ChunkDraw[] Table;
        public int ChunkCount, Instances;
        public FrameUniforms Uniforms;
        public bool LinearFilter;
        public long Frame;
        public int ChunkUploadBudget;
        public string ScreenshotPath; // offscreen only
        public bool GpuSample;        // offscreen bench: submit and wait, timing this frame's GPU work
        // Dynamic instances (mobiles, items): uploaded every frame to a reserved range of the instance buffer starting at
        // DynamicFirst. The caller appends a chunk-table entry for the range, so they draw in the same depth-tested call.
        public SpriteInstance[] Dynamic;
        public int DynamicCount;
        public uint DynamicFirst;
        /// <summary>Translucent pass: instance indices, back to front (ascending depth key); at most MaxTranslucent.</summary>
        public uint[] Translucent;
        public int TranslucentCount;
        /// <summary>When the list holds effect blends with their own pipeline: the list as runs of one blend each, in order
        /// (0 runs = one premultiplied draw).</summary>
        public TransRun[] TransRuns;
        public int TransRunCount;
        /// <summary>Override hash (OverrideSlots entries) or null; uploaded only when non-null.</summary>
        public OverrideEntry[] Overrides;
        /// <summary>External mode: the host's colour target this frame (re-fetched every frame: hosts recreate it on resize).</summary>
        public IntPtr Target;
        public uint TargetWidth, TargetHeight;
        public SDL_GPUTextureFormat TargetFormat;
        /// <summary>Keep the target's contents (LOAD) instead of clearing it, so host layers drawn earlier stay underneath.</summary>
        public bool LoadTarget;
        /// <summary>Run the copy pass (chunk and page uploads, tables) but draw nothing: no target is touched.</summary>
        public bool StreamOnly;
        /// <summary>Viewport inside the target in pixels; width 0 = the whole target.</summary>
        public int ViewportX, ViewportY, ViewportWidth, ViewportHeight;
    }

    public int LastChunkUploads, DrawCalls;
    public long AcquireWaitTicks;
    public bool ForcePoint;
    public SDL_FColor ClearColor = new() { r = 0.02f, g = 0.02f, b = 0.03f, a = 1 };
    public uint TargetW, TargetH;
    public int ViewportW, ViewportH;

    /// <summary>Records and submits one frame on its own command buffer. Returns false if the swapchain was unavailable
    /// (minimized) or the external target is missing. In external mode call it before the host submits the command
    /// buffer that samples the target: queue order then has the world finished first.</summary>
    public bool RenderFrame(ref FrameInput f)
    {
        if (Mode == RendererMode.External && !f.StreamOnly && (f.Target == IntPtr.Zero || f.TargetWidth == 0 || f.TargetHeight == 0)) return false;
        var cmd = SDL_AcquireGPUCommandBuffer(Device);
        if (cmd == IntPtr.Zero)
        {
            SubmitFailures++;
            LastSubmitError = SDL_GetError();
            return false;
        }

        // ---- stage instance uploads + frame table into the frame transfer buffer
        var mapped = (byte*)SDL_MapGPUTransferBuffer(Device, _frameTransfer, true);
        uint tableBytes = (uint)(f.ChunkCount * sizeof(ChunkDraw));
        uint dynBytes = (uint)(f.DynamicCount * sizeof(SpriteInstance));
        int transCount = Math.Min(f.TranslucentCount, MaxTranslucent);
        uint transBytes = (uint)(transCount * sizeof(ChunkDraw));
        uint ovBytes = f.Overrides != null && OverrideSlots > 0 ? (uint)(OverrideSlots * sizeof(OverrideEntry)) : 0;
        uint offset = 0;
        LastChunkUploads = f.Chunks.StageUploads(mapped, ref offset, FrameTransferSize - tableBytes - dynBytes - transBytes - ovBytes - 1024, f.ChunkUploadBudget);
        offset = (offset + 255) & ~255u;
        uint tableOffset = offset;
        if (tableBytes > 0)
            fixed (ChunkDraw* src = f.Table) Buffer.MemoryCopy(src, mapped + tableOffset, tableBytes, tableBytes);
        uint dynOffset = (tableOffset + tableBytes + 255) & ~255u;
        if (dynBytes > 0)
            fixed (SpriteInstance* src = f.Dynamic) Buffer.MemoryCopy(src, mapped + dynOffset, dynBytes, dynBytes);
        uint transOffset = (dynOffset + dynBytes + 255) & ~255u;
        var tdst = (uint*)(mapped + transOffset);
        for (int i = 0; i < transCount; i++) { tdst[2 * i] = f.Translucent[i]; tdst[2 * i + 1] = 0; }
        uint ovOffset = (transOffset + transBytes + 255) & ~255u;
        if (ovBytes > 0)
            fixed (OverrideEntry* src = f.Overrides) Buffer.MemoryCopy(src, mapped + ovOffset, ovBytes, ovBytes);
        SDL_UnmapGPUTransferBuffer(Device, _frameTransfer);

        var copy = SDL_BeginGPUCopyPass(cmd);
        var ch = f.Chunks;
        for (int i = 0; i < ch.UploadCount; i++)
        {
            var src = new SDL_GPUTransferBufferLocation { transfer_buffer = _frameTransfer, offset = ch.UploadSrc[i] };
            var dst = new SDL_GPUBufferRegion { buffer = InstanceBuffer, offset = ch.UploadDst[i], size = ch.UploadSize[i] };
            SDL_UploadToGPUBuffer(copy, in src, in dst, false);
        }
        // The first frame-table upload cycles the buffer (the previous frame may still read it); later ones fill the new one.
        bool cycle = true;
        if (tableBytes > 0) UploadFrameTable(copy, tableOffset, 0, tableBytes, ref cycle);
        if (transBytes > 0) UploadFrameTable(copy, transOffset, TransBase * 8, transBytes, ref cycle);
        if (ovBytes > 0) UploadFrameTable(copy, ovOffset, OverrideBase * 8, ovBytes, ref cycle);
        if (dynBytes > 0)
        {
            var src = new SDL_GPUTransferBufferLocation { transfer_buffer = _frameTransfer, offset = dynOffset };
            var dst = new SDL_GPUBufferRegion { buffer = InstanceBuffer, offset = f.DynamicFirst * (uint)sizeof(SpriteInstance), size = dynBytes };
            SDL_UploadToGPUBuffer(copy, in src, in dst, false);
        }
        f.Pages.Upload(copy, f.Frame);
        f.AnimPages?.Upload(copy, f.Frame);
        if (f.Pages.TableDirty || f.AnimPages is { TableDirty: true })
        {
            var p = (byte*)SDL_MapGPUTransferBuffer(Device, _pageTableTransfer, true);
            int artBytes = Math.Min(f.Pages.Table.Length * 4, _pageTableBytes);
            fixed (uint* src = f.Pages.Table) Buffer.MemoryCopy(src, p, _pageTableBytes, artBytes);
            if (f.AnimPages != null)
            {
                int animBytes = Math.Min(f.AnimPages.Table.Length * 4, _pageTableBytes - f.AnimTableBase * 4);
                fixed (uint* src = f.AnimPages.Table) Buffer.MemoryCopy(src, p + f.AnimTableBase * 4, animBytes, animBytes);
                f.AnimPages.TableDirty = false;
            }
            SDL_UnmapGPUTransferBuffer(Device, _pageTableTransfer);
            var src2 = new SDL_GPUTransferBufferLocation { transfer_buffer = _pageTableTransfer };
            var dst = new SDL_GPUBufferRegion { buffer = _pageTableBuf, size = (uint)_pageTableBytes };
            SDL_UploadToGPUBuffer(copy, in src2, in dst, true);
            f.Pages.TableDirty = false;
        }
        SDL_EndGPUCopyPass(copy);

        if (f.StreamOnly)
        {
            // uploads only: the host keeps showing its previous frame while the view streams in
            DrawCalls = 0;
            if (SDL_SubmitGPUCommandBuffer(cmd)) return true;
            SubmitFailures++;
            LastSubmitError = SDL_GetError();
            return false;
        }

        // ---- target
        IntPtr target;
        uint tw, th;
        var format = ColorFormat;
        if (Mode == RendererMode.Offscreen)
        {
            target = _offscreen; tw = _offW; th = _offH;
        }
        else if (Mode == RendererMode.External)
        {
            target = f.Target; tw = f.TargetWidth; th = f.TargetHeight; format = f.TargetFormat;
        }
        else
        {
            long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ok = SDL_WaitAndAcquireGPUSwapchainTexture(cmd, Window, out target, out tw, out th);
            AcquireWaitTicks = System.Diagnostics.Stopwatch.GetTimestamp() - w0;
            if (!ok || target == IntPtr.Zero)
            {
                SDL_SubmitGPUCommandBuffer(cmd);
                return false;
            }
        }
        TargetW = tw; TargetH = th;
        EnsureDepth(tw, th);
        var (opaque, translucentPipe) = Pipelines(format);

        int vx = 0, vy = 0, vw = (int)tw, vh = (int)th;
        if (f.ViewportWidth > 0 && f.ViewportHeight > 0) { vx = f.ViewportX; vy = f.ViewportY; vw = f.ViewportWidth; vh = f.ViewportHeight; }
        ViewportW = vw; ViewportH = vh;

        var color = new SDL_GPUColorTargetInfo
        {
            texture = target,
            clear_color = ClearColor,
            load_op = f.LoadTarget ? SDL_GPULoadOp.SDL_GPU_LOADOP_LOAD : SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
        };
        var depth = new SDL_GPUDepthStencilTargetInfo
        {
            texture = _depth, clear_depth = 1, load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE,
            stencil_load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE, stencil_store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE,
            cycle = true,
        };
        var pass = Native.SDL_BeginGPURenderPass(cmd, &color, 1, &depth);
        DrawCalls = 0;
        var u = f.Uniforms;
        u.InvViewportX = 1f / vw;
        u.InvViewportY = 1f / vh;
        u.Sharp = f.LinearFilter || ForcePoint ? 0u : 1u;
        u.AtlasSize = 1f / u.InvAtlas;
        if (f.AnimPages != null) { u.AnimAtlasSize = f.AnimPages.PageSize; u.InvAnimAtlas = 1f / f.AnimPages.PageSize; }
        u.OverrideMask = ovBytes > 0 ? (uint)(OverrideSlots - 1) : 0;
        u.OverrideBase = OverrideBase;
        u.TransBase = TransBase;
        if (f.ChunkCount > 0 && f.Instances > 0)
        {
            Bind(pass, opaque, ref f, vx, vy, vw, vh);
            u.Pass = 0;
            SDL_PushGPUVertexUniformData(cmd, 0, (IntPtr)(&u), (uint)sizeof(FrameUniforms));
            SDL_DrawGPUPrimitives(pass, 6, (uint)f.Instances, 0, 0);
            DrawCalls++;
        }
        if (transCount > 0 && f.TransRunCount == 0)
        {
            Bind(pass, translucentPipe, ref f, vx, vy, vw, vh);
            u.Pass = 1;
            SDL_PushGPUVertexUniformData(cmd, 0, (IntPtr)(&u), (uint)sizeof(FrameUniforms));
            SDL_DrawGPUPrimitives(pass, 6, (uint)transCount, 0, 0);
            DrawCalls++;
        }
        else if (transCount > 0)
        {
            // effect blends: one draw per run, its first list entry passed as TransBase (SV_InstanceID restarts at 0;
            // first_instance isn't portable with it)
            u.Pass = 1;
            IntPtr bound = IntPtr.Zero;
            for (int r = 0; r < f.TransRunCount; r++)
            {
                var run = f.TransRuns[r];
                int count = Math.Min(run.Count, transCount - run.Start);
                if (count <= 0) continue;
                var pipe = run.Blend is InstanceBlend.Normal or InstanceBlend.NormalOrdered or InstanceBlend.Screen ? translucentPipe : BlendPipeline(format, run.Blend);
                if (pipe != bound) { Bind(pass, pipe, ref f, vx, vy, vw, vh); bound = pipe; }
                u.TransBase = TransBase + (uint)run.Start;
                SDL_PushGPUVertexUniformData(cmd, 0, (IntPtr)(&u), (uint)sizeof(FrameUniforms));
                SDL_DrawGPUPrimitives(pass, 6, (uint)count, 0, 0);
                DrawCalls++;
            }
        }
        SDL_EndGPURenderPass(pass);

        if (f.ScreenshotPath != null && Offscreen)
            return SubmitAndSave(cmd, f.ScreenshotPath, _offscreen, tw, th, ColorFormat);

        if (Offscreen && Throttle)
        {
            // No swapchain to pace us: keep at most 2 frames in flight with fences, so the frame interval
            // becomes max(CPU, GPU) and a GPU-bound run measures GPU frame time.
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            var fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
            if (f.GpuSample)
            {
                WaitFence(fence);
                GpuSampleTicks = System.Diagnostics.Stopwatch.GetTimestamp() - ts;
                SDL_ReleaseGPUFence(Device, fence);
                return true;
            }
            if (_fences[_fenceHead] != IntPtr.Zero) { WaitFence(_fences[_fenceHead]); SDL_ReleaseGPUFence(Device, _fences[_fenceHead]); }
            _fences[_fenceHead] = fence;
            _fenceHead = (_fenceHead + 1) % _fences.Length;
            return true;
        }

        if (!SDL_SubmitGPUCommandBuffer(cmd))
        {
            // e.g. VK_ERROR_DEVICE_LOST after a driver reset: every later frame fails too
            SubmitFailures++;
            LastSubmitError = SDL_GetError();
            return false;
        }
        return true;
    }

    /// <summary>Frames whose command buffer could not be acquired or submitted, and SDL's error for the last one (a lost
    /// device, typically). SDL_GPU doesn't recover from that, and its Vulkan backend leaks every failed submit.</summary>
    public int SubmitFailures;
    public string LastSubmitError;

    void UploadFrameTable(IntPtr copy, uint srcOffset, uint dstOffset, uint bytes, ref bool cycle)
    {
        var src = new SDL_GPUTransferBufferLocation { transfer_buffer = _frameTransfer, offset = srcOffset };
        var dst = new SDL_GPUBufferRegion { buffer = _frameTableBuf, offset = dstOffset, size = bytes };
        SDL_UploadToGPUBuffer(copy, in src, in dst, cycle);
        cycle = false;
    }

    void Bind(IntPtr pass, IntPtr pipeline, ref FrameInput f, int vx, int vy, int vw, int vh)
    {
        SDL_BindGPUGraphicsPipeline(pass, pipeline);
        var vp = new SDL_GPUViewport { x = vx, y = vy, w = vw, h = vh, min_depth = 0, max_depth = 1 };
        SDL_SetGPUViewport(pass, in vp);
        var sc = new SDL_Rect { x = vx, y = vy, w = vw, h = vh };
        SDL_SetGPUScissor(pass, in sc);
        Span<IntPtr> bufs = stackalloc IntPtr[3] { InstanceBuffer, _frameTableBuf, _pageTableBuf };
        SDL_BindGPUVertexStorageBuffers(pass, 0, bufs, 3);
        Span<SDL_GPUTextureSamplerBinding> samp = stackalloc SDL_GPUTextureSamplerBinding[3];
        samp[0] = new SDL_GPUTextureSamplerBinding { texture = f.Pages.Texture, sampler = ForcePoint && !f.LinearFilter ? _samplerPoint : _samplerLinear }; // sharp-bilinear in the shader when scale >= 1
        samp[1] = new SDL_GPUTextureSamplerBinding { texture = _hueTex, sampler = _samplerHue };
        samp[2] = new SDL_GPUTextureSamplerBinding { texture = f.AnimPages?.Texture ?? _dummyAnimTex, sampler = _samplerHue }; // Load only
        SDL_BindGPUFragmentSamplers(pass, 0, samp, 3);
        Span<IntPtr> fbufs = stackalloc IntPtr[1] { _paletteBuf };
        SDL_BindGPUFragmentStorageBuffers(pass, 0, fbufs, 1);
    }

    /// <summary>Offscreen bench: fence-throttle submissions (see RenderFrame).</summary>
    public bool Throttle;
    readonly IntPtr[] _fences = new IntPtr[2];
    int _fenceHead;
    /// <summary>Submit → fence-signalled time of the last GpuSample frame (taken after the GPU was idle).</summary>
    public long GpuSampleTicks;

    void WaitFence(IntPtr fence)
    {
        Span<IntPtr> one = stackalloc IntPtr[1] { fence };
        SDL_WaitForGPUFences(Device, true, one, 1);
    }

    /// <summary>Median submit -> fence-signalled time of an empty command buffer on an idle GPU: the fixed overhead
    /// included in every GpuSample, subtracted to estimate net GPU frame time.</summary>
    public double MeasureSubmitOverheadMs(int n = 64)
    {
        DrainInFlight();
        var t = new double[n];
        for (int i = 0; i < n; i++)
        {
            var cmd = SDL_AcquireGPUCommandBuffer(Device);
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            var fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
            WaitFence(fence);
            t[i] = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            SDL_ReleaseGPUFence(Device, fence);
        }
        Array.Sort(t);
        return t[n / 2];
    }

    /// <summary>Waits for all in-flight frames; used before a GPU timing sample so the sample measures one frame alone.</summary>
    public void DrainInFlight()
    {
        for (int i = 0; i < _fences.Length; i++)
        {
            if (_fences[i] == IntPtr.Zero) continue;
            WaitFence(_fences[i]);
            SDL_ReleaseGPUFence(Device, _fences[i]);
            _fences[i] = IntPtr.Zero;
        }
        SDL_WaitForGPUIdle(Device);
    }

    /// <summary>RGBA8 pixels of the last screenshot (top row first).</summary>
    public byte[] LastScreenshot;

    /// <summary>Downloads a colour texture (RGBA8 or BGRA8) and writes it as PNG (null path: only <see cref="LastScreenshot"/>).
    /// Waits for the GPU. Used by test harnesses on targets they own.</summary>
    public bool SaveTexture(IntPtr texture, uint w, uint h, SDL_GPUTextureFormat format, string path)
        => SubmitAndSave(SDL_AcquireGPUCommandBuffer(Device), path, texture, w, h, format);

    bool SubmitAndSave(IntPtr cmd, string path, IntPtr texture, uint w, uint h, SDL_GPUTextureFormat format)
    {
        uint bytes = w * h * 4;
        var tb = CreateTransfer(bytes, false);
        var copy = SDL_BeginGPUCopyPass(cmd);
        var src = new SDL_GPUTextureRegion { texture = texture, w = w, h = h, d = 1 };
        var dst = new SDL_GPUTextureTransferInfo { transfer_buffer = tb, pixels_per_row = w, rows_per_layer = h };
        SDL_DownloadFromGPUTexture(copy, in src, in dst);
        SDL_EndGPUCopyPass(copy);
        var fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
        Span<IntPtr> fences = stackalloc IntPtr[1] { fence };
        SDL_WaitForGPUFences(Device, true, fences, 1);
        SDL_ReleaseGPUFence(Device, fence);
        var p = (byte*)SDL_MapGPUTransferBuffer(Device, tb, false);
        var px = new ReadOnlySpan<byte>(p, (int)bytes).ToArray();
        SDL_UnmapGPUTransferBuffer(Device, tb);
        SDL_ReleaseGPUTransferBuffer(Device, tb);
        if (format == SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM)
            for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        LastScreenshot = px;
        if (path != null) Png.Write(path, (int)w, (int)h, px);
        return true;
    }

    public void Dispose()
    {
        DrainInFlight();
        if (_depth != IntPtr.Zero) SDL_ReleaseGPUTexture(Device, _depth);
        if (_offscreen != IntPtr.Zero) SDL_ReleaseGPUTexture(Device, _offscreen);
        SDL_ReleaseGPUTexture(Device, _hueTex);
        SDL_ReleaseGPUTexture(Device, _dummyAnimTex);
        SDL_ReleaseGPUBuffer(Device, _paletteBuf);
        SDL_ReleaseGPUBuffer(Device, InstanceBuffer);
        SDL_ReleaseGPUBuffer(Device, _frameTableBuf);
        SDL_ReleaseGPUBuffer(Device, _pageTableBuf);
        SDL_ReleaseGPUTransferBuffer(Device, _frameTransfer);
        SDL_ReleaseGPUTransferBuffer(Device, _pageTableTransfer);
        SDL_ReleaseGPUSampler(Device, _samplerPoint);
        SDL_ReleaseGPUSampler(Device, _samplerLinear);
        SDL_ReleaseGPUSampler(Device, _samplerHue);
        foreach (var p in _pipelines)
        {
            SDL_ReleaseGPUGraphicsPipeline(Device, p.Opaque);
            SDL_ReleaseGPUGraphicsPipeline(Device, p.Translucent);
        }
        foreach (var p in _blendPipelines) SDL_ReleaseGPUGraphicsPipeline(Device, p.Pipeline);
        SDL_ReleaseGPUShader(Device, _vs);
        SDL_ReleaseGPUShader(Device, _ps);
        if (Mode == RendererMode.Window) SDL_ReleaseWindowFromGPUDevice(Device, Window);
        if (OwnsDevice) SDL_DestroyGPUDevice(Device);
    }
}
