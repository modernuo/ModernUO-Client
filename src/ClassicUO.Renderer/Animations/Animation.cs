using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using ClassicUO.Assets;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;

namespace ClassicUO.Renderer.Animations
{
    public sealed class Animations
    {
        private readonly TextureAtlas _atlas;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly AnimationsLoader _animationLoader;

        private SpriteInfo[][][][] _cache;

        public Animations(AnimationsLoader animationLoader, GraphicsDevice device)
        {
            _animationLoader = animationLoader;
            int page = 4096;
#if CUO_DEVTOOLS
            // CLEANUP(release): smaller anim atlas pages so the anim_atlas_lru gates evict within a short scene
            if (int.TryParse(Environment.GetEnvironmentVariable("CUO_ANIM_ATLAS_PAGE"), out int p) && p >= 1024 && p <= 4096)
            {
                page = p;
            }
#endif
            _atlas = new TextureAtlas(device, page, page, SurfaceFormat.Color);
            _cache = new SpriteInfo[animationLoader.BodyCount][][][];
            _queue = new DecodeQueue<DirectionJob>(DecodeBudget.Shared, job => job.Buffers.ReturnAll());
            _retire = Retire;
        }

        public int MaxAnimationCount => _animationLoader.BodyCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationGroupsType GetAnimType(ushort graphic) =>
            graphic < _animationLoader.BodyCount ? (AnimationGroupsType)_animationLoader.GetBody(graphic).Type : 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationFlags GetAnimFlags(ushort graphic) =>
            graphic < _animationLoader.BodyCount ? (AnimationFlags)(_animationLoader.GetBody(graphic).Flags & ~AnimBodyEntry.MountAbsoluteFlag) : 0;

        /// <summary>
        /// The pack's rider seat for <paramref name="graphic"/> as a mount (AnimBodyEntry.MountSeat): a shift added to the
        /// Mounts table's item offset (the mount-height difference of the drawn art; 0 on default packs and packs from
        /// before the field), or, for a body drawn as a Body.def stand-in it doesn't draw by default, the stand-in's
        /// absolute offset for every item.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MountSeat GetMountSeat(ushort graphic) =>
            graphic < _animationLoader.BodyCount ? _animationLoader.GetBody(graphic).MountSeat : default;

        public bool PixelCheck(
            ushort animID,
            byte group,
            byte direction,
            bool uop,
            int frame,
            int x,
            int y
        )
        {
            if (_picker.Get(PickerKey(animID, group, direction, frame), x, y))
            {
                return true;
            }

            ushort resolved = _animationLoader.ResolveBody(animID, false, out _);

            return resolved != animID && _picker.Get(PickerKey(resolved, group, direction, frame), x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong PickerKey(ushort body, byte action, byte dir, int frame) =>
            body | ((ulong)(uint)frame << 16) | ((ulong)action << 32) | ((ulong)dir << 40);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetAnimDirection(ref byte dir, ref bool mirror)
        {
            switch (dir)
            {
                case 2:
                case 4:
                    mirror = dir == 2;
                    dir = 1;

                    break;

                case 1:
                case 5:
                    mirror = dir == 1;
                    dir = 2;

                    break;

                case 0:
                case 6:
                    mirror = dir == 0;
                    dir = 3;

                    break;

                case 3:
                    dir = 0;

                    break;

                case 7:
                    dir = 4;

                    break;
            }
        }

        public void GetAnimationDimensions(
            byte animIndex,
            ushort graphic,
            byte dir,
            byte animGroup,
            bool ismounted,
            byte frameIndex,
            out int centerX,
            out int centerY,
            out int width,
            out int height
        )
        {
            dir &= 0x7F;
            bool mirror = false;
            GetAnimDirection(ref dir, ref mirror);

            if (frameIndex == 0xFF)
            {
                frameIndex = (byte)animIndex;
            }

            var frames = GetAnimationFrames(graphic, animGroup, dir, out _, out _, false);

            // UV size is set for every non-empty frame, also while its direction is still decoding on a worker
            if (!frames.IsEmpty && frameIndex < frames.Length && frames[frameIndex].UV.Width != 0)
            {
                centerX = frames[frameIndex].Center.X;
                centerY = frames[frameIndex].Center.Y;
                width = frames[frameIndex].UV.Width;
                height = frames[frameIndex].UV.Height;
                return;
            }

            centerX = 0;
            centerY = 0;
            width = 0;
            height = ismounted ? 100 : 60;
        }

        /// <summary>
        /// Frames of (body, action, stored direction 0..4). The pack already applied Bodyconv.def and the UOP
        /// AnimationSequence group replacement; Body.def and Corpse.def aliases are followed here.
        /// </summary>
        public Span<SpriteInfo> GetAnimationFrames(
            ushort id,
            byte action,
            byte dir,
            out ushort hue,
            out bool useUOP,
            bool isEquip = false,
            bool isCorpse = false,
            bool forceUOP = false,
            bool sync = false
        )
        {
            hue = 0;
            useUOP = false;

            if (_queue.HasReady)
            {
                DrainReady();
            }

            if (action >= AnimationsLoader.MAX_ACTIONS || dir >= AnimationsLoader.MAX_DIRECTIONS || id >= _animationLoader.BodyCount)
            {
                return Span<SpriteInfo>.Empty;
            }

            id = _animationLoader.ResolveBody(id, isCorpse, out hue);
            ref readonly var body = ref _animationLoader.GetBody(id);
            useUOP = body.Source != AnimSource.None && (body.Source & AnimSource.Uop) != 0;

            // When we are searching for an equipment item we must ignore any other animation which is not equipment
            var currentAnimType = (AnimationGroupsType)body.Type;

            if (isEquip && currentAnimType != AnimationGroupsType.Equipment && currentAnimType != AnimationGroupsType.Human)
            {
                return Span<SpriteInfo>.Empty;
            }

            ref var actions = ref _cache[id];
            actions ??= new SpriteInfo[AnimationsLoader.MAX_ACTIONS][][];
            ref var dirs = ref actions[action];
            dirs ??= new SpriteInfo[AnimationsLoader.MAX_DIRECTIONS][];
            ref var sprites = ref dirs[dir];

            if (sprites == null)
            {
                if (_lru != null && _evictedAt.Count != 0 && _evictedAt.Remove(DirKey(id, action, dir), out long evictedAt))
                {
                    // anim_atlas_lru: an evicted direction loads again through the async first-sight path
                    _lru.OnReload(evictedAt, AtlasPageClock.NowMs);
                    sprites = LoadDirection(id, action, dir, sync, evicted: true);
#if CUO_DEVTOOLS
                    if (LruCheck)
                    {
                        CheckReloaded(DirKey(id, action, dir), sprites, "reload");
                    }
#endif
                }
                else
                {
                    sprites = LoadDirection(id, action, dir, sync);
                }
            }
            else if (sync && _pendingSprites.Count != 0 && _pendingSprites.Contains(sprites))
            {
                // queued for a worker (e.g. by GetAnimationDimensions): a sync caller (picking) needs the textures and
                // masks now, so decode here and supersede the job; Complete drops its result without uploading
                var loaded = LoadDirection(id, action, dir, true);
                _pendingSprites.Remove(sprites);
                sprites = loaded;
            }

            if (_lru != null && sprites.Length != 0)
            {
                TouchPages(sprites);
            }

            return sprites;
        }

        /// <summary>Uploads the directions the workers finished (within the drain budget), as every GetAnimationFrames call
        /// does first: for callers that no longer call it every frame (anim_frame_count_tables).</summary>
        public void DrainFinished()
        {
            if (_queue.HasReady)
            {
                DrainReady();
            }
        }

        /// <summary>
        /// The first direction of a body never seen before decodes on a worker thread when its pack chunks are not cached
        /// (a chunk decompress is about 3 ms, a direction up to about 12 ms). CUO_ANIM_SYNC=1 restores the synchronous load.
        /// </summary>
        public static bool AsyncFirstSight = Environment.GetEnvironmentVariable("CUO_ANIM_SYNC") != "1";

        private readonly DecodeQueue<DirectionJob> _queue;
        private readonly Action<DirectionJob> _retire;
        private bool[] _bodyDrawn; // the body has at least one direction uploaded (it may be on screen)
        // main thread: the frames of the directions submitted to a worker and not yet completed, retired or superseded by a
        // sync load
        private readonly HashSet<SpriteInfo[]> _pendingSprites = new HashSet<SpriteInfo[]>(ReferenceEqualityComparer.Instance);
        private int _pendingJobs;

        /// <summary>Directions decoding on worker threads (drawn as nothing until they are ready).</summary>
        public int PendingDirections => Volatile.Read(ref _pendingJobs);

        private SpriteInfo[] LoadDirection(ushort body, byte action, byte dir, bool sync, bool evicted = false)
        {
            // stored client direction d is UO direction d + 3, the only records that are not mirrored copies
            if (!_animationLoader.TryGetDirection(body, action, dir + 3, out var entry))
            {
                return Array.Empty<SpriteInfo>();
            }

            var sprites = new SpriteInfo[entry.FrameCount];
            _bodyDrawn ??= new bool[_animationLoader.BodyCount];

            // A body with nothing uploaded yet (first sight, or its first directions still on a worker): decode on a worker
            // unless the chunks are already cached. Until the job lands, the frames exist (count, centre and size, so
            // animation timing and dimensions are unchanged) but have no texture, and callers draw nothing for them, as for
            // an empty frame. Once a body has a direction uploaded, later directions and actions stay synchronous, so a
            // visible mobile never blinks when it turns or changes action; the worker prefetches the chunks of the other
            // directions of the action and of the first actions (walk, stand, run) so those loads rarely decompress.
            // anim_atlas_lru: a direction loaded again after its page was evicted takes the same path (not drawn for a while)
            if (AsyncFirstSight && !sync && (!_bodyDrawn[body] || evicted) && !IsResident(entry))
            {
                long heldBytes = 0;

                for (int i = 0; i < sprites.Length; i++)
                {
                    ref readonly var frame = ref _animationLoader.GetFrame(entry.FirstFrame + (uint)i);

                    if (frame.Sprite.OrigW != 0 && frame.Sprite.OrigH != 0)
                    {
                        heldBytes += DecodeBudget.PooledBytes(frame.Sprite.OrigW * frame.Sprite.OrigH);
                        sprites[i].Center.X = frame.CenterX;
                        sprites[i].Center.Y = frame.CenterY;
                        sprites[i].UV = new Rectangle(0, 0, frame.Sprite.OrigW, frame.Sprite.OrigH);
                    }
                }

                var job = new DirectionJob(this, body, action, dir, entry, sprites);
                Interlocked.Increment(ref _pendingJobs);
                _pendingSprites.Add(sprites);
                _queue.Submit(job, heldBytes, counted: true);

                return sprites;
            }

            _bodyDrawn[body] = true;
            uint[] buffer = null;

            try
            {
                for (int i = 0; i < sprites.Length; i++)
                {
                    uint index = entry.FirstFrame + (uint)i;
                    ref readonly var frame = ref _animationLoader.GetFrame(index);

                    if (frame.Sprite.OrigW == 0 || frame.Sprite.OrigH == 0)
                    {
                        continue;
                    }

                    int count = frame.Sprite.OrigW * frame.Sprite.OrigH;

                    if (buffer == null || buffer.Length < count)
                    {
                        if (buffer != null)
                        {
                            ArrayPool<uint>.Shared.Return(buffer);
                        }

                        buffer = ArrayPool<uint>.Shared.Rent(count);
                    }

                    _animationLoader.DecodeFrame(index, buffer);
                    Upload(body, action, dir, i, index, buffer.AsSpan(0, count), ref sprites[i]);
                }
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<uint>.Shared.Return(buffer);
                }
            }

            return sprites;
        }

        private bool IsResident(in AnimDirEntry entry)
        {
            for (int i = 0; i < entry.FrameCount; i++)
            {
                if (!_animationLoader.IsFrameResident(entry.FirstFrame + (uint)i))
                {
                    return false;
                }
            }

            return true;
        }

        private void Upload(ushort body, byte action, byte dir, int i, uint index, ReadOnlySpan<uint> pixels, ref SpriteInfo spriteInfo)
        {
            ref readonly var frame = ref _animationLoader.GetFrame(index);
            var trim = PackSprites.Trim(in frame.Sprite);

            // texels outside the pack trim rect are 0, so the picker mask only scans the trim rect
            _picker.SetTrimmed(PickerKey(body, action, dir, i), frame.Sprite.OrigW, frame.Sprite.OrigH, trim.X, trim.Y, trim.W, trim.H, pixels);

            spriteInfo.Center.X = frame.CenterX;
            spriteInfo.Center.Y = frame.CenterY;
            spriteInfo.Texture = _atlas.AddSprite(pixels, frame.Sprite.OrigW, frame.Sprite.OrigH, out spriteInfo.UV);
            PackSpriteMap.Register(spriteInfo.Texture, spriteInfo.UV, PackSpriteMap.AnimFrame | index);

            if (_lru != null && spriteInfo.Texture.Tag is AtlasPage page)
            {
                uint key = DirKey(body, action, dir);

                if (page.LastKey != key)
                {
                    page.LastKey = key;
                    page.Directions.Add(key);
                }

                page.LastUseMs = AtlasPageClock.NowMs;
            }
        }

        // ---- anim_atlas_lru: a budgeted LRU over the atlas pages (Tick, once per frame) ----

        private AnimAtlasLruPolicy _lru;
        // (body, action, dir) of the evicted directions not loaded again yet, with the eviction time
        private readonly Dictionary<uint, long> _evictedAt = new Dictionary<uint, long>();
        private long[] _lruUse = Array.Empty<long>();
        private int[] _lruVictims = Array.Empty<int>();
        private long _lruLogAt, _lruLogged = -1;

        /// <summary>Bumped when a page is freed: cached draws that hold atlas textures (MobileFrameCache's Legacy replay) are
        /// stale.</summary>
        public static int AtlasEpoch;

        public AnimAtlasLruPolicy Lru => _lru;

        public int AtlasPages => _atlas.TexturesCount;

        public long AtlasPageBytes => _atlas.PageBytes;

        /// <summary>anim_atlas_lru on: call before the first direction loads.</summary>
        public void EnableLru(AnimAtlasLruPolicy policy)
        {
            _lru = policy;
            _atlas.TrackPages = true;

            for (int i = 0; i < _atlas.TexturesCount; i++)
            {
                var texture = _atlas.GetPage(i);
                texture.Tag ??= new AtlasPage(-1 - i, AtlasPageClock.NowMs);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint DirKey(ushort body, byte action, byte dir) => body | ((uint)action << 16) | ((uint)dir << 24);

        // a direction's frames are uploaded in order, so they sit on one page or two consecutive ones: its first and last
        // frames with a texture name them
        private static void TouchPages(SpriteInfo[] sprites)
        {
            for (int i = 0; i < sprites.Length; i++)
            {
                if (sprites[i].Texture != null)
                {
                    AtlasPageClock.Touch(sprites[i].Texture);

                    break;
                }
            }

            for (int i = sprites.Length - 1; i >= 0; i--)
            {
                if (sprites[i].Texture != null)
                {
                    AtlasPageClock.Touch(sprites[i].Texture);

                    break;
                }
            }
#if CUO_DEVTOOLS
            if (LruCheck)
            {
                for (int i = 0; i < sprites.Length; i++)
                {
                    LruCheckUse(sprites[i].Texture, "frame");
                }
            }
#endif
        }

        /// <summary>
        /// Once per frame, before the world is drawn (no batch holds atlas textures): sets the page clock, and with the LRU on
        /// moves the budget and evicts pages above it. <paramref name="unfocused"/>: the window is unfocused or minimized.
        /// </summary>
        public void Tick(long nowMs, bool unfocused)
        {
            AtlasPageClock.NowMs = nowMs;

            if (_lru == null)
            {
                return;
            }

            _lru.Tick(nowMs, unfocused);
            int count = _atlas.TexturesCount;

            if (count > _lru.BudgetPages)
            {
                if (_lruUse.Length < count)
                {
                    _lruUse = new long[count * 2];
                    _lruVictims = new int[count * 2];
                }

                for (int i = 0; i < count; i++)
                {
                    _lruUse[i] = _atlas.GetPage(i).Tag is AtlasPage page ? page.LastUseMs : nowMs;
                }

                int n = _lru.SelectVictims(_lruUse.AsSpan(0, count), count - 1, nowMs, _lruVictims);

                if (n > 0)
                {
                    // highest index first: removing a page shifts the ones after it
                    Array.Sort(_lruVictims, 0, n);

                    for (int k = n - 1; k >= 0; k--)
                    {
                        EvictPage(_lruVictims[k], nowMs);
                    }
                }
            }

            LogLru(nowMs);
        }

        private void EvictPage(int index, long nowMs)
        {
            Texture2D texture = _atlas.GetPage(index);

            if (texture.Tag is AtlasPage page)
            {
                foreach (uint key in page.Directions)
                {
                    ushort body = (ushort)key;
                    byte action = (byte)(key >> 16), dir = (byte)(key >> 24);
                    var dirs = _cache[body]?[action];
                    var sprites = dirs?[dir];

                    if (sprites == null || !UsesTexture(sprites, texture) || _pendingSprites.Contains(sprites))
                    {
                        continue;
                    }
#if CUO_DEVTOOLS
                    if (LruCheck)
                    {
                        RecordEvicted(key, sprites);
                    }
#endif
                    dirs[dir] = null;
                    _evictedAt[key] = nowMs;
                    _lru.EvictedDirections++;
                }

                page.Directions.Clear();
            }

            PackSpriteMap.Unregister(texture);
            _atlas.RemovePage(index);
            _lru.EvictedPages++;
            AtlasEpoch++;
            TextureUploads.Changed();
        }

        private static bool UsesTexture(SpriteInfo[] sprites, Texture2D texture)
        {
            for (int i = 0; i < sprites.Length; i++)
            {
                if (ReferenceEquals(sprites[i].Texture, texture))
                {
                    return true;
                }
            }

            return false;
        }

        private void LogLru(long nowMs)
        {
            long changes = _lru.EvictedPages + _lru.Reloads + _lru.Grows + _lru.Shrinks;
            bool check = false;
#if CUO_DEVTOOLS
            check = LruCheck;
#endif
            if (nowMs < _lruLogAt || (changes == _lruLogged && !check))
            {
                return;
            }

            _lruLogAt = nowMs + 5000;
            _lruLogged = changes;
            int pages = _atlas.TexturesCount;
            long mib = _atlas.PageBytes >> 20;
            Log.Info(
                $"[anim-lru] pages {pages} ({pages * mib} MiB), budget {_lru.BudgetPages} (low {_lru.LowWater}, floor {_lru.FloorPages}, ceiling {_lru.CeilingPages}; page {mib} MiB), " +
                $"evicted {_lru.EvictedPages} pages / {_lru.EvictedDirections} directions, reloads {_lru.Reloads} (thrash {_lru.ThrashReloads}), grows {_lru.Grows}, shrinks {_lru.Shrinks}"
            );
#if CUO_DEVTOOLS
            if (check)
            {
                Log.Info($"[anim-lru-check] {LruCheckCompared} reloads compared, {LruCheckUses} uses validated, {LruCheckMismatches} mismatches (anim_atlas_lru=on)");
            }
#endif
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_ANIM_LRU_CHECK: an evicted direction loads again with the same frames (count, size, centre),
        // and no frame or recorded draw names a freed page
        public static bool LruCheck = Environment.GetEnvironmentVariable("CUO_ANIM_LRU_CHECK") == "1";

        public static long LruCheckCompared, LruCheckUses, LruCheckMismatches;

        /// <summary>Tests: sees each mismatch line.</summary>
        public static Action<string> LruMismatchObserver;

        private readonly Dictionary<uint, (int W, int H, int X, int Y)[]> _evictedFrames = new Dictionary<uint, (int, int, int, int)[]>();

        private void RecordEvicted(uint key, SpriteInfo[] sprites)
        {
            var dims = new (int, int, int, int)[sprites.Length];

            for (int i = 0; i < sprites.Length; i++)
            {
                dims[i] = (sprites[i].UV.Width, sprites[i].UV.Height, sprites[i].Center.X, sprites[i].Center.Y);
            }

            _evictedFrames[key] = dims;
        }

        private void CheckReloaded(uint key, SpriteInfo[] sprites, string when)
        {
            if (!_evictedFrames.TryGetValue(key, out var dims))
            {
                return;
            }

            LruCheckCompared++;
            bool same = dims.Length == sprites.Length;

            for (int i = 0; same && i < sprites.Length; i++)
            {
                same = dims[i] == (sprites[i].UV.Width, sprites[i].UV.Height, sprites[i].Center.X, sprites[i].Center.Y);
            }

            if (!same)
            {
                LruMismatch($"[anim-lru-check] body 0x{(ushort)key:X4} action {(byte)(key >> 16)} dir {(byte)(key >> 24)} ({when}): {sprites.Length} frames, " +
                    $"first {(sprites.Length > 0 ? $"{sprites[0].UV.Width}x{sprites[0].UV.Height} c {sprites[0].Center}" : "-")}; before eviction {dims.Length} frames, " +
                    $"first {(dims.Length > 0 ? dims[0].ToString() : "-")}");
            }
        }

        /// <summary>CUO_ANIM_LRU_CHECK: a texture about to be drawn must not be a freed atlas page.</summary>
        public static void LruCheckUse(Texture2D texture, string where)
        {
            if (texture == null)
            {
                return;
            }

            LruCheckUses++;

            if (texture.IsDisposed)
            {
                LruMismatch($"[anim-lru-check] {where}: draws from a freed atlas page (serial {(texture.Tag as AtlasPage)?.Serial})");
            }
        }

        private static void LruMismatch(string line)
        {
            LruCheckMismatches++;
            LruMismatchObserver?.Invoke(line);

            if (LruCheckMismatches <= 50)
            {
                Log.Warn(line);
            }
        }
#endif

        // Upload budget: at most DrainBudgetMs of finished directions per DrainWindowMs, so a crowd's worth of directions
        // finishing together spreads over a few frames instead of landing in one (the rest stay invisible a little longer).
        private const double DrainBudgetMs = 2, DrainWindowMs = 8;
        private long _drainWindowStart, _drainSpent;

        // main thread: atlas uploads and picker masks for the directions the workers finished
        private void DrainReady()
        {
            long now = Stopwatch.GetTimestamp();

            if (now - _drainWindowStart > (long)(DrainWindowMs * Stopwatch.Frequency / 1000))
            {
                _drainWindowStart = now;
                _drainSpent = 0;
            }

            long budget = (long)(DrainBudgetMs * Stopwatch.Frequency / 1000);

            while (_drainSpent < budget && _queue.TryDequeue(out var job))
            {
                long start = Stopwatch.GetTimestamp();
                Complete(job);
                _drainSpent += Stopwatch.GetTimestamp() - start;
            }
        }

        private void Complete(DirectionJob job)
        {
            if (!_queue.IsCurrent(job))
            {
                // published after a CancelPending: the worker already returned its buffers and bytes
                Retire(job);

                return;
            }

            Interlocked.Decrement(ref _pendingJobs);

            if (!_pendingSprites.Remove(job.Sprites))
            {
                // superseded by a sync load of the same direction (already uploaded, with its masks): drop the result
                job.Buffers.ReturnAll();
                _queue.Release(job);

                return;
            }

            _bodyDrawn[job.Body] = true;

            try
            {
                CompleteFrames(job);
#if CUO_DEVTOOLS
                if (LruCheck && _lru != null)
                {
                    CheckReloaded(DirKey(job.Body, job.Action, job.Dir), job.Sprites, "uploaded");
                }
#endif
            }
            finally
            {
                _queue.Release(job);
            }
        }

        /// <summary>
        /// Scene teardown (main thread): drops the finished directions (buffers returned, bytes released) and takes the
        /// waiting ones out of the decode budget; directions still decoding return their buffers and bytes when they finish.
        /// The dropped directions load again on their next use.
        /// </summary>
        public void CancelPending() => _queue.Cancel(_retire);

        // main thread: a direction whose job was cancelled has frames without textures; forget it so it loads again
        private void Retire(DirectionJob job)
        {
            Interlocked.Decrement(ref _pendingJobs);
            job.Buffers.ReturnAll();
            _pendingSprites.Remove(job.Sprites);
            ForgetCached(job);
        }

        private void ForgetCached(DirectionJob job)
        {
            var dirs = _cache[job.Body]?[job.Action];

            if (dirs != null && ReferenceEquals(dirs[job.Dir], job.Sprites))
            {
                dirs[job.Dir] = null;
            }
        }

        private void CompleteFrames(DirectionJob job)
        {
            var loader = _animationLoader;
            uint first = job.Entry.FirstFrame;

            // the worker's buffers (or, for a failed worker, a decode here); every buffer goes back to the pool, also when an
            // upload throws (the incomplete direction is then forgotten so it loads again)
            try
            {
                job.Buffers.Upload(
                    i => FramePixels(loader, first + (uint)i),
                    i => loader.DecodeFrame(first + (uint)i),
                    (i, pixels) => Upload(job.Body, job.Action, job.Dir, i, first + (uint)i, pixels, ref job.Sprites[i])
                );
            }
            catch
            {
                ForgetCached(job);

                throw;
            }
        }

        private static int FramePixels(AnimationsLoader loader, uint index)
        {
            ref readonly var frame = ref loader.GetFrame(index);

            return frame.Sprite.OrigW == 0 || frame.Sprite.OrigH == 0 ? 0 : frame.Sprite.OrigW * frame.Sprite.OrigH;
        }

        // actions 0..4 cover walk/stand/run for monsters, animals and people
        private const int PrefetchActions = 5;

        private sealed class DirectionJob : DecodeBudget.Item
        {
            private readonly Animations _owner;
            public readonly ushort Body;
            public readonly byte Action, Dir;
            public readonly AnimDirEntry Entry;
            public readonly SpriteInfo[] Sprites;
            public readonly FrameBuffers Buffers; // rented from ArrayPool<uint>.Shared; returned by Upload, ReturnAll or Decode's failure

            public DirectionJob(Animations owner, ushort body, byte action, byte dir, AnimDirEntry entry, SpriteInfo[] sprites)
            {
                _owner = owner;
                Body = body;
                Action = action;
                Dir = dir;
                Entry = entry;
                Sprites = sprites;
                Buffers = new FrameBuffers(sprites.Length, ArrayPool<uint>.Shared);
            }

            public override void Execute()
            {
                var loader = _owner._animationLoader;

                try
                {
                    if (!_owner._queue.IsCurrent(this))
                    {
                        // cancelled while it waited in the pool queue: nothing to decode
                        _owner._queue.Publish(this);

                        return;
                    }

                    uint first = Entry.FirstFrame;
                    Buffers.Decode(i => FramePixels(loader, first + (uint)i), (i, buffer) => loader.DecodeFrame(first + (uint)i, buffer));

                    // warm the chunks of this action's other directions and of the first actions (walk, stand, run for
                    // every body type): turning or starting to move then loads synchronously without a decompress
                    for (int a = -1; a < PrefetchActions; a++)
                    {
                        int action = a < 0 ? Action : a;

                        if (a == Action)
                        {
                            continue;
                        }

                        for (int d = 0; d < AnimationsLoader.MAX_DIRECTIONS; d++)
                        {
                            if ((action != Action || d != Dir) && loader.TryGetDirection(Body, action, d + 3, out var other))
                            {
                                for (int i = 0; i < other.FrameCount; i++)
                                {
                                    loader.PrefetchFrame(other.FirstFrame + (uint)i);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Decode returned its partial buffers; the slots stay empty, so DrainReady decodes the frames on the main thread
                    ClassicUO.Utility.Logging.Log.Warn($"anim body {Body} action {Action} dir {Dir}: worker decode failed ({ex.Message})");
                }

                _owner._queue.Publish(this); // cancelled meanwhile: returns the buffers and the bytes here
            }
        }

        public void ConvertBodyIfNeeded(
            ref ushort graphic,
            bool isParent = false,
            bool forceUOP = false,
            bool isCorpse = false
        )
        {
            graphic = _animationLoader.ResolveBody(graphic, isCorpse, out _);
        }

        public bool AnimationExists(ushort graphic, byte group, bool isCorpse = false)
        {
            // From the tables, without decoding: the same answer as "frame 0 of stored direction 0 has a texture" (a frame
            // gets a texture exactly when it has a size), which used to decode that direction of every group asked about.
            if (graphic < _animationLoader.BodyCount && group < AnimationsLoader.MAX_ACTIONS)
            {
                ushort id = _animationLoader.ResolveBody(graphic, isCorpse, out _);

                if (!_animationLoader.TryGetDirection(id, group, 3, out var entry))
                {
                    return false;
                }

                ref readonly var frame = ref _animationLoader.GetFrame(entry.FirstFrame);

                return frame.Sprite.OrigW != 0 && frame.Sprite.OrigH != 0;
            }

            return false;
        }
    }
}
