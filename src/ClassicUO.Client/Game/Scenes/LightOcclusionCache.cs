// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// light_occlusion_cache (with light_occlusion): a surface glow's occluder search walks the drawn chunks and their tiles
    /// once, and keeps the objects that can hide the glow whatever their drawn state (LightOcclusion.Hides's terms that
    /// depend only on position, graphic and the source: in front, not above, an occluder by tiledata). Later frames test only
    /// those candidates with the full occluder test (alpha, roofs and max Z, circle of transparency, animated art, the item
    /// sprite select), so the occluders found are the ones the full search finds. Positions are relative to the source, so
    /// camera scroll keeps an entry. An entry is rebuilt when the light's source, light point or quad changes, when a chunk the
    /// search reached changes its <see cref="Chunk.OccluderVersion"/> (statics, multi pieces or items placed, moved, removed or
    /// changed in place), or when the set of drawn chunks changes. Entries idle for <see cref="IdleFrames"/> light passes are
    /// dropped, and the table holds at most <see cref="MaxEntries"/> (least recently used evicted).
    /// CUO_LIGHT_OCCLUSION_CACHE_CHECK=1 runs the full search next to every cached one and logs each difference.
    /// </summary>
    internal static class LightOcclusionCache
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LIGHT_OCCLUSION_CACHE") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LIGHT_OCCLUSION_CACHE_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LightOcclusionCache ?? false);

        public const int MaxEntries = 1024;
        public const uint IdleFrames = 600;
    }

    /// <summary>One light's cached occluder candidates (<see cref="LightOcclusionCache"/>): its key (the source, light id,
    /// source tile and graphic, and the light target relative to the source's anchor), the chunks its search reached with
    /// their versions, and the candidates.</summary>
    internal sealed class LightOccluderEntry
    {
        public GameObject Source;
        public byte Id;
        public int SrcX, SrcY, SrcZ;
        public ushort SrcGraphic;
        // the light target relative to the source's anchor (LightTarget: light point, quad, source top, wall facing)
        public int Px, Py, Left, Top, Right, Bottom, SrcTop;
        public byte Facing;
        public uint VisibleSig;
        public Chunk[] Chunks = new Chunk[4];
        public uint[] Versions = new uint[4];
        public int ChunkCount;
        public GameObject[] Candidates = new GameObject[8];
        public int CandidateCount;
        public uint UsedPass;
        // the entry's index in the table's list
        public int Slot;
        // light_occlusion_bake: the bake's key, the atlas generation it was baked in (-1: none) and its atlas rectangle
        public ulong BakeKey;
        public int BakeGen = -1;
        public int BakeX, BakeY, BakeW, BakeH;
        // CUO_LIGHT_OCC_BAKE_CHECK: the last pass's frame and world phase key terms (LightBakeWorldPhase)
        public ulong CheckFramePhase, CheckWorldPhase;

        public bool KeyMatches(int srcX, int srcY, int srcZ, ushort srcGraphic, int px, int py, int left, int top, int right, int bottom,
            int srcTop, byte facing, uint visibleSig) =>
            SrcX == srcX && SrcY == srcY && SrcZ == srcZ && SrcGraphic == srcGraphic && Px == px && Py == py && Left == left &&
            Top == top && Right == right && Bottom == bottom && SrcTop == srcTop && Facing == facing && VisibleSig == visibleSig;

        /// <summary>Every chunk the search reached is still live with the version it was built at.</summary>
        public bool ChunksValid()
        {
            for (int i = 0; i < ChunkCount; i++)
            {
                Chunk c = Chunks[i];

                if (c.IsDestroyed || c.OccluderVersion != Versions[i])
                {
                    return false;
                }
            }

            return true;
        }

        public void SetKey(int srcX, int srcY, int srcZ, ushort srcGraphic, int px, int py, int left, int top, int right, int bottom,
            int srcTop, byte facing, uint visibleSig)
        {
            SrcX = srcX;
            SrcY = srcY;
            SrcZ = srcZ;
            SrcGraphic = srcGraphic;
            Px = px;
            Py = py;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            SrcTop = srcTop;
            Facing = facing;
            VisibleSig = visibleSig;
            Clear();
        }

        public void AddChunk(Chunk chunk)
        {
            if (ChunkCount == Chunks.Length)
            {
                Array.Resize(ref Chunks, ChunkCount * 2);
                Array.Resize(ref Versions, ChunkCount * 2);
            }

            Chunks[ChunkCount] = chunk;
            Versions[ChunkCount++] = chunk.OccluderVersion;
        }

        public void AddCandidate(GameObject o)
        {
            if (CandidateCount == Candidates.Length)
            {
                Array.Resize(ref Candidates, CandidateCount * 2);
            }

            Candidates[CandidateCount++] = o;
        }

        /// <summary>No object or chunk kept: the candidates and chunks are dropped (arrays kept at their size).</summary>
        public void Clear()
        {
            Array.Clear(Chunks, 0, ChunkCount);
            Array.Clear(Candidates, 0, CandidateCount);
            ChunkCount = CandidateCount = 0;
        }

        /// <summary>Bytes the entry holds (object header and fields, its arrays).</summary>
        public int Bytes => 96 + 24 + Chunks.Length * 8 + 24 + Versions.Length * 4 + 24 + Candidates.Length * 8;
    }

    /// <summary>The <see cref="LightOccluderEntry"/> table: by source object, least recently used evicted past
    /// <see cref="LightOcclusionCache.MaxEntries"/>, idle entries dropped by <see cref="Sweep"/>. Entries are recycled, so a
    /// warm scene allocates nothing.</summary>
    internal sealed class LightOccluderTable
    {
        private readonly Dictionary<GameObject, LightOccluderEntry> _bySource;
        private readonly List<LightOccluderEntry> _live = new List<LightOccluderEntry>();
        private readonly Stack<LightOccluderEntry> _free = new Stack<LightOccluderEntry>();
        private readonly int _max;
        private readonly uint _idle;

        public LightOccluderTable(int max = LightOcclusionCache.MaxEntries, uint idle = LightOcclusionCache.IdleFrames)
        {
            _max = max;
            _idle = idle;
            _bySource = new Dictionary<GameObject, LightOccluderEntry>(Math.Min(max, 256), ReferenceEqualityComparer.Instance);
        }

        public int Count => _live.Count;

        public long Evictions, Dropped;

        public LightOccluderEntry Find(GameObject source, byte id, uint pass)
        {
            if (_bySource.TryGetValue(source, out LightOccluderEntry e) && e.Id == id)
            {
                e.UsedPass = pass;

                return e;
            }

            return null;
        }

        /// <summary>The entry for (source, id), new or reused, its old content cleared; the least recently used entry is
        /// evicted when the table is full.</summary>
        public LightOccluderEntry Take(GameObject source, byte id, uint pass)
        {
            if (!_bySource.TryGetValue(source, out LightOccluderEntry e))
            {
                if (_live.Count >= _max)
                {
                    int oldest = 0;

                    for (int i = 1; i < _live.Count; i++)
                    {
                        if (unchecked(pass - _live[i].UsedPass) > unchecked(pass - _live[oldest].UsedPass))
                        {
                            oldest = i;
                        }
                    }

                    Remove(_live[oldest]);
                    Evictions++;
                }

                e = _free.Count > 0 ? _free.Pop() : new LightOccluderEntry();
                e.Source = source;
                e.BakeGen = -1;
                e.Slot = _live.Count;
                _live.Add(e);
                _bySource.Add(source, e);
            }

            e.Id = id;
            e.UsedPass = pass;
            e.Clear();

            return e;
        }

        /// <summary>Drops the entries not used in the last idle passes (their objects are released).</summary>
        public void Sweep(uint pass)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (unchecked(pass - _live[i].UsedPass) > _idle)
                {
                    Remove(_live[i]);
                    Dropped++;
                }
            }
        }

        public void Reset()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Remove(_live[i]);
            }
        }

        /// <summary>Bytes the live and free entries hold, and the table's own arrays (estimate).</summary>
        public long Bytes()
        {
            long b = 64 + _live.Capacity * 8L + _bySource.Count * 24L + 64;

            foreach (LightOccluderEntry e in _live)
            {
                b += e.Bytes;
            }

            foreach (LightOccluderEntry e in _free)
            {
                b += e.Bytes;
            }

            return b;
        }

        private void Remove(LightOccluderEntry e)
        {
            _bySource.Remove(e.Source);
            int last = _live.Count - 1;
            LightOccluderEntry moved = _live[last];
            _live[e.Slot] = moved;
            moved.Slot = e.Slot;
            _live.RemoveAt(last);
            e.Clear();
            e.Source = null;
            _free.Push(e);
        }
    }
}
