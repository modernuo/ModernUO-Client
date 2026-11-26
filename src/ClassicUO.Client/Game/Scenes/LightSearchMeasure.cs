// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using Microsoft.Xna.Framework;
using Uo.Buffers;

namespace ClassicUO.Game.Scenes
{
    // Measurement only. A structural hit says nothing about animated art, alpha or resolved sprite revisions.
    internal sealed class LightSearchMeasure : IDisposable
    {
        internal readonly record struct ProfileInputs(bool Highlight, bool NoColorRange, bool DeadGray, bool Stumps,
            int Fields, int RangeX, int RangeY, int ViewRange, BaseGameObject Selected, Item Container);
        internal readonly record struct Scene(Matrix Transform, Rectangle Bounds, Vector2 Min, Vector2 Max, Point Offset,
            float Zoom, int MaxZ, bool Roof, int Ceiling, bool Cot, int CotType, int CotRadius, int PlayerZ,
            bool AlphaFade, bool Occlusion, bool AltLights, bool Colored, bool Range, int Budget, uint PackEpoch,
            int TargetWidth = 0, int TargetHeight = 0, ProfileInputs Profile = default);
        internal readonly record struct Target(ushort X, ushort Y, sbyte Z, ushort Graphic, int DrawX, int DrawY,
            int Px, int Py, int Left, int Top, int Right, int Bottom, int SourceTop, byte Facing);
        private struct Search
        {
            internal GameObject Source;
            internal uint Life, Generation;
            internal byte Id;
            internal Target Target;
        }
        private const int CandidateCapacity = 131072, SearchCapacity = 8192, ChunkCapacity = 4096;
        private GameObject[] _candidates = BufferPool.Rent<GameObject>(CandidateCapacity);
        private Search[] _previous = BufferPool.Rent<Search>(SearchCapacity), _current = BufferPool.Rent<Search>(SearchCapacity);
        private Chunk[] _chunks = BufferPool.Rent<Chunk>(ChunkCapacity);
        private uint[] _versions = BufferPool.Rent<uint>(ChunkCapacity);
        private int _chunkCount;
        private Scene _scene;
        private uint _generation;
        private bool _hasPrevious, _chunksFit;
        internal int Reads, Unique, Queries, Hits, Overflow;

        internal LightSearchMeasure()
        {
            Array.Clear(_candidates); Array.Clear(_previous); Array.Clear(_current); Array.Clear(_chunks);
        }

        internal void Begin(in Scene scene, List<Chunk> chunks, bool consecutive)
        {
            Reads = Unique = Queries = Hits = Overflow = 0;
            Array.Clear(_candidates);
            (_previous, _current) = (_current, _previous);
            Array.Clear(_current);
            bool same = _hasPrevious && consecutive && scene == _scene && chunks.Count == _chunkCount;
            _chunksFit = chunks.Count <= ChunkCapacity;
            if (_chunksFit)
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    Chunk c = chunks[i];
                    same &= ReferenceEquals(c, _chunks[i]) && !c.IsDestroyed && c.OccluderVersion == _versions[i];
                    _chunks[i] = c;
                    _versions[i] = c.OccluderVersion;
                }
                if (chunks.Count < _chunkCount) Array.Clear(_chunks, chunks.Count, _chunkCount - chunks.Count);
                _chunkCount = chunks.Count;
            }
            else { same = false; Overflow++; }
            if (!same) _generation++;
            _scene = scene;
            _hasPrevious = true;
        }

        internal void Read(GameObject obj)
        {
            Reads++;
            int start = RuntimeHelpers.GetHashCode(obj) & (CandidateCapacity - 1);
            for (int n = 0; n < CandidateCapacity; n++)
            {
                int i = (start + n) & (CandidateCapacity - 1);
                if (ReferenceEquals(_candidates[i], obj)) return;
                if (_candidates[i] == null) { _candidates[i] = obj; Unique++; return; }
            }
            Overflow++;
        }

        internal void Query(GameObject source, byte id, in Target target)
        {
            int start = (RuntimeHelpers.GetHashCode(source) ^ id) & (SearchCapacity - 1);
            for (int n = 0; n < SearchCapacity; n++)
            {
                int i = (start + n) & (SearchCapacity - 1);
                ref Search cur = ref _current[i];
                if (ReferenceEquals(cur.Source, source) && cur.Id == id && cur.Life == source.Incarnation) return;
                if (cur.Source != null) continue;
                Queries++;
                for (int p = 0; p < SearchCapacity; p++)
                {
                    ref Search old = ref _previous[(start + p) & (SearchCapacity - 1)];
                    if (old.Source == null) break;
                    if (ReferenceEquals(old.Source, source) && old.Id == id && old.Life == source.Incarnation)
                    {
                        if (_chunksFit && old.Generation == _generation && old.Target == target) Hits++;
                        break;
                    }
                }
                cur = new Search { Source = source, Id = id, Life = source.Incarnation, Generation = _generation, Target = target };
                return;
            }
            Overflow++;
        }

        public void Dispose()
        {
            BufferPool.Return(_candidates, true); BufferPool.Return(_previous, true); BufferPool.Return(_current, true);
            BufferPool.Return(_chunks, true); BufferPool.Return(_versions);
            _candidates = null; _previous = _current = null; _chunks = null; _versions = null;
        }
    }
}
