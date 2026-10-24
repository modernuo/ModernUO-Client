using System.Runtime.InteropServices;
using static SDL3.SDL;

namespace Uo.Render;

/// <summary>SDL_GPU device, pipeline and persistent buffers. One draw call per frame for the whole world pass.</summary>
public sealed unsafe class Renderer : IDisposable
{
    public readonly IntPtr Device, Window;
    public readonly bool Offscreen;
    public readonly SDL_GPUTextureFormat ColorFormat;
    readonly IntPtr _pipeline;
    IntPtr _depth;
    uint _depthW, _depthH;
    readonly IntPtr _samplerPoint, _samplerLinear, _samplerHue;
    readonly IntPtr _hueTex;
    readonly IntPtr _paletteBuf, _dummyAnimTex;
    public readonly IntPtr InstanceBuffer;
    readonly IntPtr _chunkTableBuf, _pageTableBuf;
    readonly IntPtr _frameTransfer, _pageTableTransfer;
    public readonly uint FrameTransferSize;
    public readonly int ChunkTableCapacity;
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

    /// <summary><paramref name="driver"/>: "direct3d12", "vulkan", or null for SDL's choice among drivers we have shaders for.</summary>
    public Renderer(IntPtr window, bool offscreen, PackSet pack, int instanceCapacity, int maxVisibleChunks, string driver = "direct3d12")
    {
        Window = window;
        Offscreen = offscreen;
        var formats = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL;
        if (typeof(Renderer).Assembly.GetManifestResourceInfo("sprite_vs.spv") != null) formats |= SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV;
        Device = SDL_CreateGPUDevice(formats, false, driver);
        if (Device == IntPtr.Zero) throw new Exception($"SDL_CreateGPUDevice({driver ?? "default"}): " + SDL_GetError());
        ShaderFormat = (SDL_GetGPUShaderFormats(Device) & formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL) != 0
            ? SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL : SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV;

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

        _pipeline = CreatePipeline();

        _samplerPoint = CreateSampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST, SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST, 0);
        // zoomed out: trilinear, limited to mips the 2-px gutter still protects
        _samplerLinear = CreateSampler(SDL_GPUFilter.SDL_GPU_FILTER_LINEAR, SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR, 1.5f);
        _samplerHue = CreateSampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST, SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST, 0);

        InstanceBuffer = CreateBuffer((uint)(instanceCapacity * sizeof(SpriteInstance)), "instances");
        ChunkTableCapacity = maxVisibleChunks;
        _chunkTableBuf = CreateBuffer((uint)(maxVisibleChunks * sizeof(ChunkDraw)), "chunk table");
        _pageTableBytes = Math.Max(1, pack.TablePageCount) * 4;
        _pageTableBuf = CreateBuffer((uint)_pageTableBytes, "page table");

        FrameTransferSize = 8u << 20;
        _frameTransfer = CreateTransfer(FrameTransferSize, true);
        _pageTableTransfer = CreateTransfer((uint)_pageTableBytes, true);

        _hueTex = CreateHueTexture(pack);
        _paletteBuf = CreatePaletteBuffer(pack);
        _dummyAnimTex = SDL_CreateGPUTexture(Device, new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D_ARRAY, format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8_UINT,
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER, width = 1, height = 1, layer_count_or_depth = 1, num_levels = 1,
        });
    }

    /// <summary>palettes.bin's u16 table as one storage buffer (18.5 MiB for all 37.9k palettes), uploaded once.</summary>
    IntPtr CreatePaletteBuffer(PackSet pack)
    {
        var pal = pack.Palettes ?? new ushort[2];
        uint bytes = (uint)((pal.Length * 2 + 3) & ~3);
        var buf = CreateBuffer(bytes, "palettes");
        var tb = CreateTransfer(bytes, true);
        var ptr = SDL_MapGPUTransferBuffer(Device, tb, false);
        fixed (ushort* src = pal) Buffer.MemoryCopy(src, (void*)ptr, bytes, pal.Length * 2);
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

    IntPtr CreatePipeline()
    {
        var vs = CreateShader("sprite_vs", "VSMain", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 3, 1);
        var ps = CreateShader("sprite_ps", "PSMain", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 3, 1, 0);
        var color = new SDL_GPUColorTargetDescription { format = ColorFormat };
        var ci = new SDL_GPUGraphicsPipelineCreateInfo
        {
            vertex_shader = vs,
            fragment_shader = ps,
            primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
            rasterizer_state = new SDL_GPURasterizerState { fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL, cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE },
            depth_stencil_state = new SDL_GPUDepthStencilState
            {
                compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_LESS,
                enable_depth_test = true,
                enable_depth_write = true,
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
        if (p == IntPtr.Zero) throw new Exception("pipeline: " + SDL_GetError());
        SDL_ReleaseGPUShader(Device, vs);
        SDL_ReleaseGPUShader(Device, ps);
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
        var tex = SDL_CreateGPUTexture(Device, in ci);
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
        if (Offscreen) return;
        var mode = VSync ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC
            : ImmediateSupported ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_IMMEDIATE
            : MailboxSupported ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX
            : SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC;
        SDL_SetGPUSwapchainParameters(Device, Window, SDL_GPUSwapchainComposition.SDL_GPU_SWAPCHAINCOMPOSITION_SDR, mode);
    }

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
        _depth = SDL_CreateGPUTexture(Device, in ci);
        _depthW = w; _depthH = h;
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
        _offscreen = SDL_CreateGPUTexture(Device, in ci);
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
    }

    public int LastChunkUploads, DrawCalls;
    public long AcquireWaitTicks;
    public bool ForcePoint;
    public SDL_FColor ClearColor = new() { r = 0.02f, g = 0.02f, b = 0.03f, a = 1 };
    public uint TargetW, TargetH;

    /// <summary>Records and submits one frame. Returns false if the swapchain was unavailable (minimized).</summary>
    public bool RenderFrame(ref FrameInput f)
    {
        var cmd = SDL_AcquireGPUCommandBuffer(Device);
        if (cmd == IntPtr.Zero) return false;

        // ---- stage instance uploads + chunk table into the frame transfer buffer
        var mapped = (byte*)SDL_MapGPUTransferBuffer(Device, _frameTransfer, true);
        uint tableBytes = (uint)(f.ChunkCount * sizeof(ChunkDraw));
        uint dynBytes = (uint)(f.DynamicCount * sizeof(SpriteInstance));
        uint offset = 0;
        LastChunkUploads = f.Chunks.StageUploads(mapped, ref offset, FrameTransferSize - tableBytes - dynBytes - 512, f.ChunkUploadBudget);
        offset = (offset + 255) & ~255u;
        uint tableOffset = offset;
        if (tableBytes > 0)
            fixed (ChunkDraw* src = f.Table) Buffer.MemoryCopy(src, mapped + tableOffset, tableBytes, tableBytes);
        uint dynOffset = (tableOffset + tableBytes + 255) & ~255u;
        if (dynBytes > 0)
            fixed (SpriteInstance* src = f.Dynamic) Buffer.MemoryCopy(src, mapped + dynOffset, dynBytes, dynBytes);
        SDL_UnmapGPUTransferBuffer(Device, _frameTransfer);

        var copy = SDL_BeginGPUCopyPass(cmd);
        var ch = f.Chunks;
        for (int i = 0; i < ch.UploadCount; i++)
        {
            var src = new SDL_GPUTransferBufferLocation { transfer_buffer = _frameTransfer, offset = ch.UploadSrc[i] };
            var dst = new SDL_GPUBufferRegion { buffer = InstanceBuffer, offset = ch.UploadDst[i], size = ch.UploadSize[i] };
            SDL_UploadToGPUBuffer(copy, in src, in dst, false);
        }
        if (tableBytes > 0)
        {
            var src = new SDL_GPUTransferBufferLocation { transfer_buffer = _frameTransfer, offset = tableOffset };
            var dst = new SDL_GPUBufferRegion { buffer = _chunkTableBuf, offset = 0, size = tableBytes };
            SDL_UploadToGPUBuffer(copy, in src, in dst, true);
        }
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

        // ---- target
        IntPtr target;
        uint tw, th;
        if (Offscreen)
        {
            target = _offscreen; tw = _offW; th = _offH;
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

        var color = new SDL_GPUColorTargetInfo
        {
            texture = target,
            clear_color = ClearColor,
            load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
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
        if (f.ChunkCount > 0 && f.Instances > 0)
        {
            SDL_BindGPUGraphicsPipeline(pass, _pipeline);
            var vp = new SDL_GPUViewport { x = 0, y = 0, w = tw, h = th, min_depth = 0, max_depth = 1 };
            SDL_SetGPUViewport(pass, in vp);
            Span<IntPtr> bufs = stackalloc IntPtr[3] { InstanceBuffer, _chunkTableBuf, _pageTableBuf };
            SDL_BindGPUVertexStorageBuffers(pass, 0, bufs, 3);
            Span<SDL_GPUTextureSamplerBinding> samp = stackalloc SDL_GPUTextureSamplerBinding[3];
            samp[0] = new SDL_GPUTextureSamplerBinding { texture = f.Pages.Texture, sampler = ForcePoint && !f.LinearFilter ? _samplerPoint : _samplerLinear }; // sharp-bilinear in the shader when scale >= 1
            samp[1] = new SDL_GPUTextureSamplerBinding { texture = _hueTex, sampler = _samplerHue };
            samp[2] = new SDL_GPUTextureSamplerBinding { texture = f.AnimPages?.Texture ?? _dummyAnimTex, sampler = _samplerHue }; // Load only
            SDL_BindGPUFragmentSamplers(pass, 0, samp, 3);
            Span<IntPtr> fbufs = stackalloc IntPtr[1] { _paletteBuf };
            SDL_BindGPUFragmentStorageBuffers(pass, 0, fbufs, 1);
            var u = f.Uniforms;
            u.InvViewportX = 1f / tw;
            u.InvViewportY = 1f / th;
            u.Sharp = f.LinearFilter || ForcePoint ? 0u : 1u;
            u.AtlasSize = 1f / u.InvAtlas;
            if (f.AnimPages != null) { u.AnimAtlasSize = f.AnimPages.PageSize; u.InvAnimAtlas = 1f / f.AnimPages.PageSize; }
            SDL_PushGPUVertexUniformData(cmd, 0, (IntPtr)(&u), (uint)sizeof(FrameUniforms));
            SDL_DrawGPUPrimitives(pass, 6, (uint)f.Instances, 0, 0);
            DrawCalls = 1;
        }
        SDL_EndGPURenderPass(pass);

        if (f.ScreenshotPath != null && Offscreen)
            return SubmitAndSave(cmd, f.ScreenshotPath, tw, th);

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

        SDL_SubmitGPUCommandBuffer(cmd);
        return true;
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

    bool SubmitAndSave(IntPtr cmd, string path, uint w, uint h)
    {
        uint bytes = w * h * 4;
        var tb = CreateTransfer(bytes, false);
        var copy = SDL_BeginGPUCopyPass(cmd);
        var src = new SDL_GPUTextureRegion { texture = _offscreen, w = w, h = h, d = 1 };
        var dst = new SDL_GPUTextureTransferInfo { transfer_buffer = tb, pixels_per_row = w, rows_per_layer = h };
        SDL_DownloadFromGPUTexture(copy, in src, in dst);
        SDL_EndGPUCopyPass(copy);
        var fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
        Span<IntPtr> fences = stackalloc IntPtr[1] { fence };
        SDL_WaitForGPUFences(Device, true, fences, 1);
        SDL_ReleaseGPUFence(Device, fence);
        var p = (byte*)SDL_MapGPUTransferBuffer(Device, tb, false);
        var span = new ReadOnlySpan<byte>(p, (int)bytes);
        if (ColorFormat == SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM)
        {
            var copyBytes = span.ToArray();
            for (int i = 0; i < copyBytes.Length; i += 4) (copyBytes[i], copyBytes[i + 2]) = (copyBytes[i + 2], copyBytes[i]);
            Png.Write(path, (int)w, (int)h, copyBytes);
        }
        else Png.Write(path, (int)w, (int)h, span);
        SDL_UnmapGPUTransferBuffer(Device, tb);
        SDL_ReleaseGPUTransferBuffer(Device, tb);
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
        SDL_ReleaseGPUBuffer(Device, _chunkTableBuf);
        SDL_ReleaseGPUBuffer(Device, _pageTableBuf);
        SDL_ReleaseGPUTransferBuffer(Device, _frameTransfer);
        SDL_ReleaseGPUTransferBuffer(Device, _pageTableTransfer);
        SDL_ReleaseGPUSampler(Device, _samplerPoint);
        SDL_ReleaseGPUSampler(Device, _samplerLinear);
        SDL_ReleaseGPUSampler(Device, _samplerHue);
        SDL_ReleaseGPUGraphicsPipeline(Device, _pipeline);
        if (!Offscreen) SDL_ReleaseWindowFromGPUDevice(Device, Window);
        SDL_DestroyGPUDevice(Device);
    }
}
