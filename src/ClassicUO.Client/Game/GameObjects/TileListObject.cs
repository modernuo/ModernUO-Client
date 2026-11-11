// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.GameObjects
{
    /// <summary>
    /// Land, statics and multi components: the only objects a tile's linked list holds (Chunk.AddGameObject). Items, mobiles
    /// and effects live in the chunk's DynamicTileIndex alone, so they carry no list links (16 B less per dynamic).
    /// </summary>
    internal abstract class TileListObject : GameObject
    {
        protected TileListObject(World world) : base(world) { }

        public TileListObject TNext;
        public TileListObject TPrevious;

        // T9 S9: land, statics and multi components are not world state: their graphic and hue stay on the object. Items,
        // mobiles and effects read theirs from the ECS (GameObject.Graphic dispatches by kind; these hide it so a receiver
        // typed as a list kind reads the field without the dispatch)
        private ushort _graphic, _hue;

        public new ushort Graphic
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _graphic;
            set
            {
                // a placed object's graphic changed in place (no tile-list change: a season swap, Static.SetGraphic): its
                // tiledata flags are an input of the max-Z decision. Items get the same note from WorldWriter's operations
                // (Item.GraphicCommitted). The Legacy chunk mesh baked the old graphic's art (and the Gpu walk reads
                // InChunkMesh, which depends on the graphic): rebuild it, or meshed land and statics keep the previous season.
                if (value != _graphic && TileChunk != null)
                {
                    TileChunk.OwnerMap?.NoteContentChanged(TileChunk.X, TileChunk.Y);
                    TileChunk.Mesh.IsDirty = true;
                }

                _graphic = value;
            }
        }

        public new ushort Hue
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _hue;
            set => _hue = value;
        }

        /// <summary>Takes this object out of its tile list, joining its neighbours (RemoveFromTile).</summary>
        public void Unlink()
        {
            if (TPrevious != null)
            {
                TPrevious.TNext = TNext;
            }

            if (TNext != null)
            {
                TNext.TPrevious = TPrevious;
            }

            TNext = null;
            TPrevious = null;
        }

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            base.Destroy();
            _graphic = 0;
            _hue = 0;
        }

        public override void ResetForReuse()
        {
            base.ResetForReuse();
            TNext = TPrevious = null;
            _graphic = 0;
            _hue = 0;
        }
    }
}
