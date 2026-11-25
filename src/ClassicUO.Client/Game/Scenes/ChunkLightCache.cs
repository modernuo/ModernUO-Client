// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using Uo.Buffers;

namespace ClassicUO.Game.Scenes
{
    // Membership only: drawn state, screen anchors, roof/range/shape tests and final ranking stay live.
    internal sealed class ChunkLightCache : IDisposable
    {
#if CUO_DEVTOOLS
        private static readonly bool? Env = Environment.GetEnvironmentVariable("CUO_CHUNK_LIGHT_CACHE") switch
        {
            "1" => true, "0" => false, _ => null
        };
        internal static readonly bool Check = Environment.GetEnvironmentVariable("CUO_CHUNK_LIGHT_CACHE_CHECK") == "1";
#else
        private static readonly bool? Env = null;
#endif
        [ThreadStatic] internal static bool? OverrideForTests;
        internal static bool Enabled => OverrideForTests ?? Env ?? (Settings.GlobalSettings?.ChunkLightCache ?? false);
        internal const int MaxRows = 16384;
        private GameObject[] _handles;
        internal uint[] Incarnation;
        internal byte[] Kind, Cell, LightId, Height;
        internal ushort[] X, Y, Graphic, Displayed;
        internal sbyte[] Z;
        internal int[] PriorityZ;
        internal ulong[] Flags;
        private int _capacity;
        private uint _version, _itemGen, _epoch;
        private bool _valid, _overflow;
        internal int Count, StaticCount, Rebuilds;
        internal long RetainedBytes => _capacity == 0 ? 0 : (long)_capacity * 37 + 13 * 24;

        internal GameObject Resolve(int row)
        {
            GameObject o = _handles[row];
            return o != null && !o.IsDestroyed && o.Incarnation == Incarnation[row] ? o : null;
        }

        internal bool EdgeCandidate(int row, GameObject obj, uint frame, int offX, int offY, int left, int top,
            int right, int bottom, int margin, bool noRoofs, int maxZ, int ceiling, out int lx, out int ly)
        {
            lx = ly = 0;
            if (obj.DrawnFrame == frame) return false;
            ViewMargin.Anchor(X[row], Y[row], Z[row], offX, offY, out int ax, out int ay);
            if (!ViewMargin.InBand(ax, ay, left, top, right, bottom, margin)) return false;
            var data = new StaticTiles { Flags = (TileFlag)Flags[row], Height = Height[row] };
            int priority = PriorityZ[row];
            if (obj.AllowedToDraw) GameScene.CalculateObjectHeight(ref priority, ref data);
            if (!ChunkLights.EdgeVisible(Z[row], priority, data.IsInternal, data.IsRoof, noRoofs, false, maxZ, ceiling)) return false;
            lx = ax + 22; ly = ay + 22;
            return true;
        }

        internal bool DrawsNow(int row) => Kind[row] == 0
            ? ((TileFlag)Flags[row] & TileFlag.LightSource) != 0 && LightEmitters.StaticDrawsNow(Graphic[row], LightId[row])
            : ((TileFlag)Flags[row] & TileFlag.LightSource) != 0 && LightEmitters.ItemDrawsNow(LightId[row]) ||
                Displayed[row] >= 0x3E02 && Displayed[row] <= 0x3E0B || Displayed[row] >= 0x3914 && Displayed[row] <= 0x3929;

        internal bool Refresh(Chunk chunk, StaticTiles[] data)
        {
            if (_valid && _version == chunk.OccluderVersion && _itemGen == chunk.GpuItemRenderGen && _epoch == LightEmitters.Epoch)
                return !_overflow;

            if (_handles != null) Array.Clear(_handles, 0, Count);
            Count = StaticCount = 0;
            _overflow = false;
            if (_epoch != LightEmitters.Epoch) chunk.RefreshLightSources();
            _version = chunk.OccluderVersion;
            _itemGen = chunk.GpuItemRenderGen;
            _epoch = LightEmitters.Epoch;
            _valid = true;
            Rebuilds++;
            for (int i = 0; i < chunk.LightSourceCount; i++)
            {
                if (!Add(chunk.LightSources[i], data, false)) return Overflow();
            }
            StaticCount = Count;
            DynamicTileIndex dyn = chunk.Dynamic;
            if (dyn != null)
            {
                for (ulong cells = dyn.Cells; cells != 0; cells &= cells - 1)
                {
                    int cell = System.Numerics.BitOperations.TrailingZeroCount(cells);
                    var entries = dyn.RawCell(cell, out int n);
                    for (int i = 0; i < n; i++)
                    {
                        if (entries[i].Obj is Item item && Potential(item, data) && !Add(item, data, true)) return Overflow();
                    }
                }
            }
            return true;
        }

        // All flagged items are retained across both colored modes and LightID/shape changes.
        private static bool Potential(Item item, StaticTiles[] data)
        {
            if (item.IsDestroyed || item.IsMulti || item.IsCorpse) return false;
            ushort g = item.Graphic, d = item.DisplayedGraphicOf(g);
            return g < data.Length && (data[g].IsLight || d >= 0x3E02 && d <= 0x3E0B || d >= 0x3914 && d <= 0x3929);
        }

        private bool Overflow()
        {
            Array.Clear(_handles, 0, Count);
            Count = StaticCount = 0;
            _overflow = true;
            return false;
        }

        private bool Add(GameObject obj, StaticTiles[] data, bool item)
        {
            if (Count == MaxRows) return false;
            if (Count == _capacity) Grow(Math.Min(MaxRows, Math.Max(16, _capacity * 2)));
            int i = Count++;
            _handles[i] = obj;
            Incarnation[i] = obj.Incarnation;
            Kind[i] = (byte)(item ? 1 : 0);
            Cell[i] = (byte)obj.TileCell;
            X[i] = obj.X; Y[i] = obj.Y; Z[i] = obj.Z;
            Graphic[i] = obj.Graphic;
            Displayed[i] = obj is Item it ? it.DisplayedGraphicOf(it.Graphic) : obj.Graphic;
            LightId[i] = obj is Item light ? light.LightID : obj.Graphic < data.Length ? data[obj.Graphic].Layer : (byte)0;
            Height[i] = obj.Graphic < data.Length ? data[obj.Graphic].Height : (byte)0;
            Flags[i] = obj.Graphic < data.Length ? (ulong)data[obj.Graphic].Flags : 0;
            PriorityZ[i] = obj.PriorityZ;
            return true;
        }

        private void Resize<T>(ref T[] array, int n)
        {
            T[] next = BufferPool.Rent<T>(n);
            if (array != null) Array.Copy(array, next, Count);
            BufferPool.Return(array, System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            array = next;
        }

        private void Grow(int n)
        {
            Resize(ref _handles, n); Resize(ref Incarnation, n); Resize(ref Kind, n); Resize(ref Cell, n);
            Resize(ref X, n); Resize(ref Y, n); Resize(ref Z, n); Resize(ref Graphic, n); Resize(ref Displayed, n);
            Resize(ref LightId, n); Resize(ref Height, n); Resize(ref PriorityZ, n); Resize(ref Flags, n);
            _capacity = _handles.Length;
        }

        public void Dispose()
        {
            BufferPool.Return(_handles, true); BufferPool.Return(Incarnation); BufferPool.Return(Kind); BufferPool.Return(Cell);
            BufferPool.Return(X); BufferPool.Return(Y); BufferPool.Return(Z); BufferPool.Return(Graphic); BufferPool.Return(Displayed);
            BufferPool.Return(LightId); BufferPool.Return(Height); BufferPool.Return(PriorityZ); BufferPool.Return(Flags);
            _handles = null; Incarnation = null; Kind = Cell = LightId = Height = null;
            X = Y = Graphic = Displayed = null; Z = null; PriorityZ = null; Flags = null;
            Count = StaticCount = _capacity = 0;
            _valid = false;
        }
    }
}
