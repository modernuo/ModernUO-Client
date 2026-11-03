using Uo.Pack;

namespace ModernUO.Studio.Assets
{
    /// <summary>One block before and after an edit (land and statics, as the region file stores them).</summary>
    internal readonly record struct BlockEdit(int Facet, int Bx, int By, BlockData Before, BlockData After);

    /// <summary>One undoable edit: every block it touched, as they were and as it left them.</summary>
    internal sealed class MapChange
    {
        public MapChange(string label, int facet, IReadOnlyList<BlockEdit> blocks, int tiles)
        {
            Label = label;
            Facet = facet;
            Blocks = blocks;
            Tiles = tiles;
            Time = DateTime.Now;
        }

        public string Label { get; }
        public int Facet { get; }
        public IReadOnlyList<BlockEdit> Blocks { get; }
        public int Tiles { get; }
        public DateTime Time { get; }

        /// <summary>The first tile it touched, for the Changes panel's link.</summary>
        public int X { get; init; }
        public int Y { get; init; }
    }

    /// <summary>
    /// The map edits of the open pack (studio/UX.md, "Editing model"): regions load into memory on their first edit, every
    /// edit is a <see cref="MapChange"/> on an undo stack, and an edited region's bytes go to the GPU world renderer
    /// (ChunkStore.RegionOverride) and to Studio's own map reads. The pack itself is never written; saving makes a new one.
    /// </summary>
    internal sealed class MapEdits
    {
        private sealed class EditRegion
        {
            public RegionHeader Header;
            public BlockData[] Blocks;
            public byte[] Logical; // RegionFile.Build output, rebuilt after each edit (null = stale)
        }

        private readonly StudioAssets _assets;
        private readonly Dictionary<(int Facet, int Rx, int Ry), EditRegion> _regions = new();
        private readonly List<MapChange> _undo = new();
        private readonly List<MapChange> _redo = new();
        private Transaction _open;
        private Transaction _preview;

        public MapEdits(StudioAssets assets)
        {
            _assets = assets;
        }

        public IReadOnlyList<MapChange> History => _undo;
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public MapChange NextUndo => _undo.Count > 0 ? _undo[^1] : null;
        public MapChange NextRedo => _redo.Count > 0 ? _redo[^1] : null;

        /// <summary>Changes since the pack was opened or saved; the regions that differ from the pack on disk.</summary>
        public int Dirty => _undo.Count;

        /// <summary>Raised after an edit, undo or redo with the blocks to re-bake (facet, bx0, by0, bx1, by1).</summary>
        public event Action<int, int, int, int, int> BlocksChanged;

        public event Action Changed;

        // ---- reading (edited data first)

        public bool HasRegion(int facet, int rx, int ry) => _regions.ContainsKey((facet, rx, ry));

        /// <summary>An edited region's logical bytes (for the renderer), or null when the pack's file is current.</summary>
        public byte[] RegionBytes(int facet, int rx, int ry)
        {
            if (!_regions.TryGetValue((facet, rx, ry), out var r))
            {
                return null;
            }

            return r.Logical ??= RegionFile.Build(in r.Header, r.Blocks);
        }

        /// <summary>An edited block's land (64 cells) and statics; false when the block is not edited (read the pack).</summary>
        public bool TryGetBlock(int facet, int bx, int by, out LandCell[] land, out StaticCell[] statics)
        {
            land = null;
            statics = null;
            int rb = _assets.Files.Maps.RegionBlocks(facet);

            if (!_regions.TryGetValue((facet, bx / rb, by / rb), out var r))
            {
                return false;
            }

            int i = (by % rb) * r.Header.BlocksW + bx % rb;

            if (i >= r.Blocks.Length || r.Blocks[i].IsEmpty)
            {
                return false;
            }

            land = r.Blocks[i].Land;
            statics = r.Blocks[i].Statics;

            return true;
        }

        // ---- editing

        /// <summary>Starts one undoable change; edits through it are applied at once and recorded by <see cref="Transaction.Commit"/>.</summary>
        public Transaction Begin(string label, int facet)
        {
            CancelPreview();
            _open?.Commit();
            _open = new Transaction(this, label, facet);

            return _open;
        }

        /// <summary>
        /// A tool's preview under the cursor: applied (and drawn) like an edit but not recorded. <see cref="Transaction.Rollback"/>
        /// takes it back; <see cref="Transaction.Commit"/> records it after all (a click). Any other edit, undo or redo
        /// takes the preview back first, so it never mixes with recorded changes.
        /// </summary>
        public Transaction BeginPreview(string label, int facet)
        {
            CancelPreview();
            _open?.Commit();
            _preview = new Transaction(this, label, facet);

            return _preview;
        }

        /// <summary>The preview becomes the open change (a click on it): it is recorded by its Commit, not taken back.</summary>
        public Transaction PromotePreview()
        {
            var tx = _preview;
            _preview = null;

            if (tx != null)
            {
                _open?.Commit();
                _open = tx;
            }

            return tx;
        }

        public void CancelPreview()
        {
            _preview?.Rollback();
            _preview = null;
        }

        internal sealed class Transaction
        {
            private readonly MapEdits _edits;
            private readonly Dictionary<(int Bx, int By), BlockData> _before = new();
            private readonly HashSet<(int X, int Y)> _tiles = new();
            private bool _done;
            private int _firstX = -1, _firstY = -1;

            public Transaction(MapEdits edits, string label, int facet)
            {
                _edits = edits;
                Label = label;
                Facet = facet;
            }

            public string Label { get; set; }
            public int Facet { get; }
            public int TileCount => _tiles.Count;
            public bool IsDone => _done;
            public IReadOnlyCollection<(int X, int Y)> Tiles => _tiles;

            /// <summary>Puts every block it touched back as it was and records nothing (a preview the cursor left).</summary>
            public void Rollback()
            {
                if (_done)
                {
                    return;
                }

                _done = true;

                if (_edits._open == this)
                {
                    _edits._open = null;
                }

                if (_edits._preview == this)
                {
                    _edits._preview = null;
                }

                foreach (var (key, block) in _before)
                {
                    if (_edits.EnsureBlock(Facet, key.Bx, key.By, out _, out int index))
                    {
                        _edits.Store(Facet, key.Bx, key.By, index, block);
                    }
                }
            }

            /// <summary>The tile's land cell and statics, to change in place with <see cref="SetLand"/> and <see cref="SetStatics"/>.</summary>
            public bool TryGet(int x, int y, out LandCell land, out StaticCell[] statics)
            {
                land = default;
                statics = null;

                if (!_edits.EnsureBlock(Facet, x >> 3, y >> 3, out var block))
                {
                    return false;
                }

                land = block.Land[(y & 7) * 8 + (x & 7)];
                statics = block.Statics.Where(s => s.X == (x & 7) && s.Y == (y & 7)).ToArray();

                return true;
            }

            public void SetLand(int x, int y, ushort tile, sbyte z)
            {
                Touch(x, y, out var block, out int index);

                if (block.Land == null)
                {
                    return;
                }

                var land = (LandCell[])block.Land.Clone();
                land[(y & 7) * 8 + (x & 7)] = new LandCell { TileId = tile, Z = z };
                _edits.Store(Facet, x >> 3, y >> 3, index, new BlockData(land, block.Statics));
            }

            /// <summary>Replaces the statics of tile (x, y) with <paramref name="statics"/> (their X and Y are set to the tile's cell).</summary>
            public void SetStatics(int x, int y, IEnumerable<StaticCell> statics)
            {
                Touch(x, y, out var block, out int index);

                if (block.Land == null)
                {
                    return;
                }

                byte cx = (byte)(x & 7), cy = (byte)(y & 7);
                var others = block.Statics.Where(s => s.X != cx || s.Y != cy);
                var placed = statics.Select(s =>
                {
                    s.X = cx;
                    s.Y = cy;

                    return s;
                });

                _edits.Store(Facet, x >> 3, y >> 3, index, new BlockData(block.Land, others.Concat(placed).ToArray()));
            }

            private void Touch(int x, int y, out BlockData block, out int index)
            {
                index = -1;
                block = default;

                if (!_edits.EnsureBlock(Facet, x >> 3, y >> 3, out block, out index))
                {
                    return;
                }

                _before.TryAdd((x >> 3, y >> 3), block);

                if (_tiles.Add((x, y)) && _firstX < 0)
                {
                    _firstX = x;
                    _firstY = y;
                }
            }

            /// <summary>Records the change (nothing when no tile changed) and re-bakes the blocks it touched.</summary>
            public MapChange Commit()
            {
                if (_done)
                {
                    return null;
                }

                _done = true;

                if (_edits._open == this)
                {
                    _edits._open = null;
                }

                if (_edits._preview == this)
                {
                    _edits._preview = null;
                }

                if (_before.Count == 0)
                {
                    return null;
                }

                var blocks = _before.Select(kv => new BlockEdit(Facet, kv.Key.Bx, kv.Key.By, kv.Value, _edits.CurrentBlock(Facet, kv.Key.Bx, kv.Key.By))).ToList();
                var change = new MapChange(Label, Facet, blocks, _tiles.Count) { X = _firstX, Y = _firstY };
                _edits._undo.Add(change);
                _edits._redo.Clear();
                _edits.Notify(change);

                return change;
            }
        }

        public void Undo()
        {
            CancelPreview();

            if (_undo.Count == 0)
            {
                return;
            }

            var change = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Apply(change, before: true);
            _redo.Add(change);
            Notify(change);
        }

        public void Redo()
        {
            CancelPreview();

            if (_redo.Count == 0)
            {
                return;
            }

            var change = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Apply(change, before: false);
            _undo.Add(change);
            Notify(change);
        }

        private void Apply(MapChange change, bool before)
        {
            foreach (var b in change.Blocks)
            {
                if (EnsureBlock(b.Facet, b.Bx, b.By, out _, out int index))
                {
                    Store(b.Facet, b.Bx, b.By, index, before ? b.Before : b.After);
                }
            }
        }

        private void Notify(MapChange change)
        {
            int bx0 = change.Blocks.Min(b => b.Bx), by0 = change.Blocks.Min(b => b.By);
            int bx1 = change.Blocks.Max(b => b.Bx), by1 = change.Blocks.Max(b => b.By);
            BlocksChanged?.Invoke(change.Facet, bx0, by0, bx1, by1);
            Changed?.Invoke();
        }

        /// <summary>After saving: the edits are now the pack's content, so nothing is dirty and history starts again.</summary>
        public void MarkSaved()
        {
            _undo.Clear();
            _redo.Clear();
            Changed?.Invoke();
        }

        // ---- regions in memory

        /// <summary>The regions that have edits, with their blocks as they are now (for saving).</summary>
        public IEnumerable<(int Facet, int Rx, int Ry, RegionHeader Header, BlockData[] Blocks)> EditedRegions() =>
            _regions.Select(kv => (kv.Key.Facet, kv.Key.Rx, kv.Key.Ry, kv.Value.Header, kv.Value.Blocks));

        private BlockData CurrentBlock(int facet, int bx, int by)
        {
            EnsureBlock(facet, bx, by, out var block, out _);

            return block;
        }

        private bool EnsureBlock(int facet, int bx, int by, out BlockData block) => EnsureBlock(facet, bx, by, out block, out _);

        private bool EnsureBlock(int facet, int bx, int by, out BlockData block, out int index)
        {
            block = default;
            index = -1;
            var info = MapData.Info(_assets, facet);

            if (info == null || bx < 0 || by < 0 || bx >= info.BlocksX || by >= info.BlocksY)
            {
                return false;
            }

            int rb = _assets.Files.Maps.RegionBlocks(facet);
            var key = (facet, bx / rb, by / rb);

            if (!_regions.TryGetValue(key, out var region))
            {
                string path = PackConst.RegionPath(_assets.Pack.PathOf(Path.Combine("map", facet.ToString())), key.Item2, key.Item3);

                if (!File.Exists(path))
                {
                    return false;
                }

                var (header, blocks) = RegionFile.Read(path);
                region = new EditRegion { Header = header, Blocks = blocks };
                _regions[key] = region;
            }

            index = (by % rb) * region.Header.BlocksW + bx % rb;

            if (index >= region.Blocks.Length || region.Blocks[index].IsEmpty)
            {
                return false;
            }

            block = region.Blocks[index];

            return true;
        }

        private void Store(int facet, int bx, int by, int index, BlockData block)
        {
            int rb = _assets.Files.Maps.RegionBlocks(facet);
            var region = _regions[(facet, bx / rb, by / rb)];
            region.Blocks[index] = block;
            region.Logical = null;

            // re-bake at once, so a drag stroke shows each tile as it goes (the commit notifies the whole change again)
            BlocksChanged?.Invoke(facet, bx, by, bx, by);
        }
    }
}
