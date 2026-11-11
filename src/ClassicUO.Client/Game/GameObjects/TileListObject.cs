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

        public override void ResetForReuse()
        {
            base.ResetForReuse();
            TNext = TPrevious = null;
        }
    }
}
