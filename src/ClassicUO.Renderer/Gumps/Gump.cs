using System;
using System.Buffers;
using System.Diagnostics;
using ClassicUO.Assets;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Gumps
{
    /// <summary>
    /// Gump textures. <see cref="GetGump"/> is synchronous: gump controls take their size and their existence from it
    /// at construction, so a gump the client asks for is always decoded by the time the call returns. With a pack the
    /// decode reads one record (pooled buffers, no copies) and the size and existence of any gump are known without
    /// decoding (<see cref="TryGetSize"/>, <see cref="Exists"/>). Gumps that will be needed soon can be decoded ahead on
    /// worker threads (<see cref="Request"/>, uploaded by <see cref="DrainReady"/>); a <see cref="GetGump"/> that comes
    /// first decodes on the spot and the worker's result is dropped. CUO_GUMP_SYNC=1 turns requests off.
    /// </summary>
    public sealed class Gump
    {
        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[] _spriteInfos;
        private readonly PixelPicker _picker;
        private readonly GumpsLoader _gumpsLoader;
        private readonly FirstSightDecoder _decoder;

        public static readonly bool AsyncRequests = Environment.GetEnvironmentVariable("CUO_GUMP_SYNC") != "1";

        /// <summary>Main-thread first-sight decodes and their time; FrameStats resets them.</summary>
        public static int FrameSyncLoads;
        public static long FrameSyncTicks;
        /// <summary>Worker decodes uploaded by <see cref="DrainReady"/> and the time spent there; FrameStats resets them.</summary>
        public static int FrameAsyncUploads;
        public static long FrameUploadTicks;
        /// <summary>Main-thread decodes and their time since start (never reset; for scripted measurements).</summary>
        public static int TotalSyncLoads;
        public static long TotalSyncTicks;

        public Gump(GumpsLoader gumpsLoader, GraphicsDevice device)
        {
            _gumpsLoader = gumpsLoader;
            _atlas = new TextureAtlas(device, 4096, 4096, SurfaceFormat.Color);
            _spriteInfos = new SpriteInfo[gumpsLoader.Count];
            _picker = new PixelPicker(_spriteInfos.Length);
            _decoder = new FirstSightDecoder("gump", _spriteInfos.Length, DecodeOnWorker, UploadDecoded);
        }

        public void Reload()
        {
            _gumpsLoader.LoadOurs();
            System.Array.Clear(_spriteInfos);
            _picker.Clear();
        }

        /// <summary>Worker decodes requested and not uploaded yet.</summary>
        public int PendingDecodes => _decoder.Pending;

        /// <summary>
        /// Same answer as <c>GetGump(idx).Texture != null</c>. With a pack it comes from the size table without decoding;
        /// on the MUL/UOP path it is that lookup (an index entry alone doesn't say whether it decodes).
        /// </summary>
        public bool Exists(uint idx)
        {
            if (idx >= _spriteInfos.Length)
            {
                return false;
            }

            if (_spriteInfos[idx].Texture != null)
            {
                return true;
            }

            return _gumpsLoader.FromPack ? _gumpsLoader.PixelCount(idx) > 0 : GetGump(idx).Texture != null;
        }

        /// <summary>Width and height of gump <paramref name="idx"/> without decoding it. False = no such gump.</summary>
        public bool TryGetSize(uint idx, out int width, out int height)
        {
            if (idx < _spriteInfos.Length && _spriteInfos[idx].Texture != null)
            {
                width = _spriteInfos[idx].UV.Width;
                height = _spriteInfos[idx].UV.Height;

                return true;
            }

            if (idx < _spriteInfos.Length)
            {
                return _gumpsLoader.TryGetSize(idx, out width, out height);
            }

            width = height = 0;

            return false;
        }

        /// <summary>
        /// Queues a worker decode of gump <paramref name="idx"/> if it exists and isn't in the atlas (pack only; main
        /// thread). Returns true when nothing needs to wait for it.
        /// </summary>
        public bool Request(uint idx)
        {
            if (idx >= _spriteInfos.Length || _spriteInfos[idx].Texture != null || !AsyncRequests || !_gumpsLoader.CanDecodeOnWorker
                || _decoder.IsFailed(idx) || _gumpsLoader.PixelCount(idx) == 0)
            {
                return true;
            }

            _decoder.Request(idx);

            return false;
        }

        /// <summary>Gumps decoded by <see cref="Prefetch"/> batches, and their wall time (never reset; for scripted measurements).</summary>
        public static int TotalPrefetched;
        public static long TotalPrefetchTicks;

        /// <summary>Most gumps one <see cref="Prefetch"/> batch queues, and most decoded RGBA bytes it puts in flight.</summary>
        public const int PrefetchMaxBatch = 64;
        public const long PrefetchMaxBytes = 64L << 20;

        /// <summary>
        /// A window about to be built from many gumps (a server gump's layout): decodes the ones not in the atlas on
        /// worker threads at once and waits for them, so the controls' synchronous <see cref="GetGump"/> calls find them
        /// uploaded. The main thread then waits for the slowest decode instead of doing every decode in turn. Main thread;
        /// pack only. Fewer than two missing gumps: nothing to gain, nothing is queued.
        /// <list type="bullet">
        /// <item>The batch is bounded: at most <see cref="PrefetchMaxBatch"/> distinct gumps and <see cref="PrefetchMaxBytes"/>
        /// of decoded pixels; the rest decode on the spot when the controls ask, as before.</item>
        /// <item><paramref name="timeoutMs"/> bounds queueing and waiting. The wait uploads finished decodes only before the
        /// deadline; one atlas upload (a GPU copy) can't be interrupted, so the call can overrun by one upload.</item>
        /// <item>After a timeout, decodes still running keep their jobs: a <see cref="GetGump"/> for one of them takes the
        /// worker's result if it has finished by then, else decodes on the spot and the worker's result is dropped (at most
        /// one duplicate decode per gump of the batch).</item>
        /// </list>
        /// </summary>
        public int Prefetch(ReadOnlySpan<uint> ids, int timeoutMs = 20)
        {
            if (!AsyncRequests || !_gumpsLoader.CanDecodeOnWorker)
            {
                return 0;
            }

            long t0 = Stopwatch.GetTimestamp();
            long deadline = t0 + Stopwatch.Frequency * timeoutMs / 1000;
            Span<uint> batch = stackalloc uint[PrefetchMaxBatch];
            int n = 0;
            long bytes = 0;

            foreach (uint id in ids)
            {
                if (n == batch.Length)
                {
                    break;
                }

                if (id >= _spriteInfos.Length || _spriteInfos[id].Texture != null || _decoder.IsRequested(id) || _decoder.IsFailed(id)
                    || batch.Slice(0, n).Contains(id))
                {
                    continue;
                }

                long b = _gumpsLoader.PixelCount(id) * 4L;

                if (b == 0 || bytes + b > PrefetchMaxBytes)
                {
                    continue;
                }

                batch[n++] = id;
                bytes += b;
            }

            if (n < 2)
            {
                return 0;
            }

            int queued = 0;

            for (int i = 0; i < n && Stopwatch.GetTimestamp() < deadline; i++)
            {
                if (!Request(batch[i]))
                {
                    queued++;
                }
            }

            while (true)
            {
                bool waiting = false;

                for (int i = 0; i < n; i++)
                {
                    if (_decoder.IsRequested(batch[i]))
                    {
                        waiting = true;

                        break;
                    }
                }

                if (!waiting || Stopwatch.GetTimestamp() >= deadline)
                {
                    break;
                }

                if (_decoder.HasReady)
                {
                    _decoder.Drain(deadline);
                }
                else
                {
                    System.Threading.Thread.Yield();
                }
            }

            TotalPrefetched += queued;
            TotalPrefetchTicks += Stopwatch.GetTimestamp() - t0;

            return queued;
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

        /// <summary>Drops finished worker decodes and returns their buffers (scene teardown).</summary>
        public void CancelPending() => _decoder.Cancel();

        // Worker thread: touches only the loader's pack store and size table (immutable after Load, reads thread-safe),
        // the hue table (immutable) and ArrayPool, plus the job.
        private bool DecodeOnWorker(FirstSightDecoder.Job job)
        {
            int count = _gumpsLoader.PixelCount(job.Index);

            if (count == 0)
            {
                return false;
            }

            job.Pixels = ArrayPool<uint>.Shared.Rent(count);
            var info = _gumpsLoader.GetGump(job.Index, job.Pixels);

            if (info.Pixels.IsEmpty)
            {
                return false;
            }

            job.Width = info.Width;
            job.Height = info.Height;

            return true;
        }

        // main thread: what GetGump does after its decode
        private void UploadDecoded(FirstSightDecoder.Job job)
        {
            ref var spriteInfo = ref _spriteInfos[job.Index];

            if (spriteInfo.Texture != null)
            {
                return; // a lookup needed it first and decoded it itself
            }

            Upload(job.Index, ref spriteInfo, job.Pixels.AsSpan(0, job.Width * job.Height), job.Width, job.Height);
            FrameAsyncUploads++;
        }

        private void Upload(uint idx, ref SpriteInfo spriteInfo, ReadOnlySpan<uint> pixels, int width, int height)
        {
            spriteInfo.Texture = _atlas.AddSprite(pixels, width, height, out spriteInfo.UV);
            // the whole image is the "trim rect": SetTrimmed gives Set's mask with the SIMD run scan
            _picker.SetTrimmed(idx, width, height, 0, 0, width, height, pixels);
        }

        public ref readonly SpriteInfo GetGump(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null && _decoder.IsRequested(idx))
            {
                // a worker is on it (a prefetch that timed out, a request): take its result if it has finished instead
                // of decoding the same gump again (Drain uploads one finished job per call)
                while (spriteInfo.Texture == null && _decoder.IsRequested(idx) && _decoder.HasReady)
                {
                    _decoder.Drain(0);
                }
            }

            if (spriteInfo.Texture == null)
            {
                long t0 = Stopwatch.GetTimestamp();

                if (_gumpsLoader.FromPack)
                {
                    int count = _gumpsLoader.PixelCount(idx);

                    if (count > 0)
                    {
                        FrameSyncLoads++;
                        // the image only lives until the atlas upload: decode into a pooled buffer
                        var buffer = ArrayPool<uint>.Shared.Rent(count);

                        try
                        {
                            var gumpInfo = _gumpsLoader.GetGump(idx, buffer);

                            if (!gumpInfo.Pixels.IsEmpty)
                            {
                                Upload(idx, ref spriteInfo, gumpInfo.Pixels, gumpInfo.Width, gumpInfo.Height);
                            }
                        }
                        finally
                        {
                            ArrayPool<uint>.Shared.Return(buffer);
                        }
                    }
                }
                else
                {
                    var gumpInfo = _gumpsLoader.GetGump(idx);

                    if (!gumpInfo.Pixels.IsEmpty)
                    {
                        FrameSyncLoads++;
                        Upload(idx, ref spriteInfo, gumpInfo.Pixels, gumpInfo.Width, gumpInfo.Height);
                    }
                }

                if (spriteInfo.Texture != null)
                {
                    long dt = Stopwatch.GetTimestamp() - t0;
                    FrameSyncTicks += dt;
                    TotalSyncTicks += dt;
                    TotalSyncLoads++;
                }
            }

            return ref spriteInfo;
        }

        public bool PixelCheck(uint idx, int x, int y) => _picker.Get(idx, x, y);
    }
}
