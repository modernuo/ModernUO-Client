using ClassicUO.Assets;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Buffers;
using System.Diagnostics;

namespace ClassicUO.Renderer.Arts
{
    public sealed class Art
    {
        private readonly SpriteInfo[] _spriteInfos;
        private readonly TextureAtlas _atlas;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly Rectangle[] _realArtBounds;
        private readonly ArtLoader _artLoader;
        private readonly HuesLoader _huesLoader;

        public Art(ArtLoader artLoader, HuesLoader huesLoader, GraphicsDevice device)
        {
            _artLoader = artLoader;
            _huesLoader = huesLoader;
            _atlas = new TextureAtlas(device, 4096, 4096, SurfaceFormat.Color);
            _spriteInfos = new SpriteInfo[_artLoader.Count];
            _realArtBounds = new Rectangle[_spriteInfos.Length];
            _decoder = new FirstSightDecoder("art", _spriteInfos.Length, DecodeOnWorker, UploadDecoded,
                idx => _artLoader.PixelCount(idx) * 4L);
        }

        /// <summary>
        /// First-sight art requested ahead of use (<see cref="RequestLand"/>, <see cref="RequestArt"/>, by the chunk loader)
        /// decodes on a worker; its atlas upload waits for <see cref="DrainReady"/>. CUO_ART_SYNC=1 turns requests off:
        /// every first lookup decodes on the spot, as before.
        /// </summary>
        public static readonly bool AsyncFirstSight = Environment.GetEnvironmentVariable("CUO_ART_SYNC") != "1";

        private readonly FirstSightDecoder _decoder;
        private static readonly bool DiagSyncLog = Environment.GetEnvironmentVariable("CUO_ART_SYNC_LOG") == "1";

        /// <summary>Main-thread first-sight decodes (a lookup that had to decode) and their time; FrameStats resets them.</summary>
        public static int FrameSyncLoads;
        public static long FrameSyncTicks;
        /// <summary>Worker decodes uploaded by <see cref="DrainReady"/> and the time spent there; FrameStats resets them.</summary>
        public static int FrameAsyncUploads;
        public static long FrameUploadTicks;

        /// <summary>Worker decodes requested and not uploaded yet.</summary>
        public int PendingDecodes => _decoder.Pending;

        /// <summary>Worker decodes uploaded so far: a caller waiting on requests only needs to look again when it changed.</summary>
        public int CompletedDecodes => _decoder.Completed;

        /// <summary>
        /// True when land tile <paramref name="idx"/> is in the atlas, or has no image (a lookup would not decode it);
        /// otherwise queues its worker decode, once, and returns false. Main thread.
        /// </summary>
        public bool RequestLand(uint idx) => Request(idx & ~0x4000u);

        /// <summary>As <see cref="RequestLand"/>, for static art <paramref name="idx"/>.</summary>
        public bool RequestArt(uint idx) => Request(idx + 0x4000);

        private bool Request(uint idx)
        {
            // a sprite the worker could not decode counts as ready: the request was speculative, and a real lookup decodes
            // (or fails) on the spot exactly as before
            if (idx >= _spriteInfos.Length || _spriteInfos[idx].Texture != null || !AsyncFirstSight || !_artLoader.Exists((int)idx)
                || _decoder.IsFailed(idx))
            {
                return true;
            }

            _decoder.Request(idx);

            return false;
        }

        /// <summary>Uploads finished worker decodes until <paramref name="deadline"/> (Stopwatch ticks), at least one. Main thread.</summary>
        public void DrainReady(long deadline)
        {
            if (_decoder.HasReady)
            {
                long t0 = Stopwatch.GetTimestamp();
                _decoder.Drain(deadline);
                FrameUploadTicks += Stopwatch.GetTimestamp() - t0;
            }
        }

        // Worker thread. Touches only the loader's sprite table (immutable after Load), the pack pages (thread-safe) and
        // ArrayPool, plus the job; the atlas, the picker, the bounds and the sprite infos stay on the main thread.
        private bool DecodeOnWorker(FirstSightDecoder.Job job)
        {
            int count = _artLoader.PixelCount(job.Index);

            if (count == 0)
            {
                return false;
            }

            job.Pixels = ArrayPool<uint>.Shared.Rent(count);
            var artInfo = _artLoader.GetArt(job.Index, job.Pixels);

            if (artInfo.Pixels.IsEmpty)
            {
                return false;
            }

            job.Width = artInfo.Width;
            job.Height = artInfo.Height;
            job.TrimX = artInfo.TrimX;
            job.TrimY = artInfo.TrimY;
            job.TrimW = artInfo.TrimW;
            job.TrimH = artInfo.TrimH;

            if (job.Index > 0x4000)
            {
                PixelPicker.VisibleBounds(artInfo.Pixels, artInfo.Width, artInfo.Height, artInfo.TrimX, artInfo.TrimY, artInfo.TrimW, artInfo.TrimH,
                    out int minX, out int minY, out int maxX, out int maxY);
                job.Bounds = new Rectangle(minX, minY, maxX - minX, maxY - minY);
            }

            return true;
        }

        // main thread: what Get does after its decode
        private void UploadDecoded(FirstSightDecoder.Job job)
        {
            uint idx = job.Index;
            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture != null)
            {
                return; // a lookup needed it first and decoded it itself
            }

            var pixels = job.Pixels.AsSpan(0, job.Width * job.Height);
            spriteInfo.Texture = _atlas.AddSprite(pixels, job.Width, job.Height, out spriteInfo.UV);
            PackSpriteMap.Register(spriteInfo.Texture, spriteInfo.UV, idx);

            if (idx > 0x4000)
            {
                _picker.SetTrimmed(idx - 0x4000, job.Width, job.Height, job.TrimX, job.TrimY, job.TrimW, job.TrimH, pixels);
                _realArtBounds[idx - 0x4000] = job.Bounds;
            }

            FrameAsyncUploads++;
        }

        public void Reload()
        {
            Array.Clear(_spriteInfos);
            Array.Clear(_realArtBounds);
            _picker.Clear();
            Generation++;
        }

        /// <summary>
        /// Bumped whenever sprites that were in the atlas may be gone again (<see cref="Reload"/>): a caller that remembered
        /// "these sprites are ready" must look again.
        /// </summary>
        public int Generation { get; private set; }

        /// <summary>Scene teardown: drops finished worker decodes and returns their buffers (see FirstSightDecoder.Cancel).</summary>
        public void CancelPending() => _decoder.Cancel();

        public ref readonly SpriteInfo GetLand(uint idx)
            => ref Get((uint)(idx & ~0x4000));

        public ref readonly SpriteInfo GetArt(uint idx)
            => ref Get(idx + 0x4000);

        private ref readonly SpriteInfo Get(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                long syncStart = Stopwatch.GetTimestamp();
                FrameSyncLoads++;
                if (DiagSyncLog)
                {
                    var st = new StackTrace(1, false);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < Math.Min(6, st.FrameCount); i++) { var m = st.GetFrame(i)?.GetMethod(); sb.Append(m?.DeclaringType?.Name).Append('.').Append(m?.Name).Append(" < "); }
                    Log.Info($"[artsync] idx 0x{idx:X} {(idx >= 0x4000 ? "art 0x" + (idx - 0x4000).ToString("X") : "land")} {sb}");
                }

                // the untrimmed image only lives until the atlas upload: decode into a pooled buffer
                var buffer = ArrayPool<uint>.Shared.Rent(Math.Max(1, _artLoader.PixelCount(idx)));

                try
                {
                    var artInfo = _artLoader.GetArt(idx, buffer);

                    if (artInfo.Pixels.IsEmpty && idx > 0)
                    {
                        // Trying to load a texture that does not exist in the client MULs
                        // Degrading gracefully and only crash if not even the fallback ItemID exists
                        Log.Error(
                            $"Texture not found for sprite: idx: {idx}; itemid: {(idx > 0x4000 ? idx - 0x4000 : '-')}"
                        );
                        return ref Get(0); // ItemID of "UNUSED" placeholder
                    }

                    spriteInfo.Texture = _atlas.AddSprite(
                        artInfo.Pixels,
                        artInfo.Width,
                        artInfo.Height,
                        out spriteInfo.UV
                    );

                    PackSpriteMap.Register(spriteInfo.Texture, spriteInfo.UV, idx);

                    if (idx > 0x4000)
                    {
                        idx -= 0x4000;

                        // texels outside the pack trim rect are 0: the picker mask and the visible bounds only scan inside it
                        _picker.SetTrimmed(idx, artInfo.Width, artInfo.Height, artInfo.TrimX, artInfo.TrimY, artInfo.TrimW, artInfo.TrimH, artInfo.Pixels);
                        PixelPicker.VisibleBounds(artInfo.Pixels, artInfo.Width, artInfo.Height, artInfo.TrimX, artInfo.TrimY, artInfo.TrimW, artInfo.TrimH,
                            out int minX, out int minY, out int maxX, out int maxY);
                        _realArtBounds[idx] = new Rectangle(minX, minY, maxX - minX, maxY - minY);
                    }
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(buffer);
                    FrameSyncTicks += Stopwatch.GetTimestamp() - syncStart;
                }
            }

            return ref spriteInfo;
        }

        public unsafe IntPtr CreateCursorSurfacePtr(
            int index,
            ushort customHue,
            out int hotX,
            out int hotY,
            float dpiScale
        )
        {
            hotX = hotY = 0;

            var artInfo = _artLoader.GetArt((uint)(index + 0x4000));

            if (artInfo.Pixels.IsEmpty)
            {
                return IntPtr.Zero;
            }

            int srcWidth = artInfo.Width;
            int srcHeight = artInfo.Height;

            // Make a copy of pixels to avoid modifying the original
            var rentedBuffer = ArrayPool<uint>.Shared.Rent(artInfo.Pixels.Length);
            try
            {
                var pixelsCopy = rentedBuffer.AsSpan(0, artInfo.Pixels.Length);
                artInfo.Pixels.CopyTo(pixelsCopy);

                // Process the copy: find hotX/Y and clear marker pixels
                for (int y = 0; y < srcHeight; y++)
                {
                    for (int x = 0; x < srcWidth; x++)
                    {
                        int idx = y * srcWidth + x;
                        uint pixel = pixelsCopy[idx];

                        if (pixel == 0)
                            continue;

                        // Clear black marker pixels
                        if (pixel == 0xFF_00_00_00)
                        {
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Check for green hotspot marker in first row/column
                        if (pixel == 0xFF_00_FF_00)
                        {
                            if (x == 0)
                                hotY = y;
                            if (y == 0)
                                hotX = x;
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Clear edge pixels (first/last row and column)
                        if (x == 0 || y == 0 || x == srcWidth - 1 || y == srcHeight - 1)
                        {
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Apply custom hue if needed
                        if (customHue > 0)
                        {
                            Color c = default;
                            c.PackedValue = pixel;
                            pixelsCopy[idx] = HuesHelper.Color16To32(
                                _huesLoader.GetColor16(
                                    HuesHelper.ColorToHue(c),
                                    customHue
                                )
                            ) | 0xFF_00_00_00;
                        }
                    }
                }

                // Scale hotX/Y by dpiScale
                hotX = (int)(hotX * dpiScale);
                hotY = (int)(hotY * dpiScale);

                // Now create the surface from cleaned pixels
                fixed (uint* ptr = pixelsCopy)
                {
                    SDL.SDL_Surface* surface = (SDL.SDL_Surface*)
                        SDL.SDL_CreateSurfaceFrom(
                            srcWidth,
                            srcHeight,
                            SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888,
                            (IntPtr)ptr,
                            4 * srcWidth);

                    if (dpiScale != 1f)
                    {
                        int width = (int)(srcWidth * dpiScale);
                        int height = (int)(srcHeight * dpiScale);

                        SDL.SDL_Surface* newSurface = (SDL.SDL_Surface*)SDL.SDL_ScaleSurface(
                            (nint)surface,
                            width,
                            height,
                            SDL.SDL_ScaleMode.SDL_SCALEMODE_NEAREST);

                        SDL.SDL_DestroySurface((nint)surface);
                        surface = newSurface;
                    }

                    return (IntPtr)surface;
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(rentedBuffer);
            } 
        }

        public Rectangle GetRealArtBounds(uint idx) =>
            idx < 0 || idx >= _realArtBounds.Length
                ? Rectangle.Empty
                : _realArtBounds[idx];

        public bool PixelCheck(uint idx, int x, int y) => _picker.Get(idx, x, y);
    }
}
