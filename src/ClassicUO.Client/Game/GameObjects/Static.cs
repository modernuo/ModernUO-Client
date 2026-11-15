// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Assets;
using ClassicUO.Utility;
using System.Runtime.CompilerServices;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class Static : TileListObject
    {
        //private static readonly QueuedPool<Static> _pool = new QueuedPool<Static>
        //(
        //    Constants.PREDICTABLE_STATICS,
        //    s =>
        //    {
        //        s.IsDestroyed = false;
        //        s.AlphaHue = 0;
        //        s.FoliageIndex = 0;
        //    }
        //);

        public Static(World world) : base(world) { }

        public string Name => ItemData.Name;

        public ushort OriginalGraphic { get; private set; }

        public ref StaticTiles ItemData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Client.Game.UO.FileManager.TileData.StaticData[Graphic];
        }

        public bool IsVegetation;
        public int Index;


        /// <summary>Allocated because the recycler had none ([gcs] pool lines).</summary>
        public static long TotalNew;

        /// <summary>A new Static, or <paramref name="reuse"/> (a retired Static from MapObjectRecycler) put back to a new one's state.</summary>
        public static Static Create(World world, ushort graphic, ushort hue, int index, Static reuse = null) =>
            Create(world, graphic, hue, index, GameObject.PlayerIsGargoyle(world), reuse);

        /// <summary><see cref="Create(World, ushort, ushort, int, Static)"/> with the player's race read once by the caller
        /// (Chunk.Load creates a whole block's statics in one pass).</summary>
        public static Static Create(World world, ushort graphic, ushort hue, int index, bool playerIsGargoyle, Static reuse = null)
        {
            Static s;

            if (reuse != null && ReferenceEquals(reuse.World, world))
            {
                s = reuse;
                s.ResetForReuse();
            }
            else
            {
                s = new Static(world);
                TotalNew++;
            }

            s.Graphic = s.OriginalGraphic = graphic;
            s.Hue = hue;
            s.UpdateGraphicBySeason(playerIsGargoyle);
            s.Index = index;

            if (s.ItemData.Height > 5 || s.ItemData.Height == 0)
            {
                s._canBeTransparent = 1;
            }
            else if (s.ItemData.IsRoof || s.ItemData.IsSurface && s.ItemData.IsBackground || s.ItemData.IsWall)
            {
                s._canBeTransparent = 1;
            }
            else if (s.ItemData.Height == 5 && s.ItemData.IsSurface && !s.ItemData.IsBackground)
            {
                s._canBeTransparent = 1;
            }
            else
            {
                s._canBeTransparent = 0;
            }

            return s;
        }

        public void SetGraphic(ushort g)
        {
            Graphic = g;
        }

        public void RestoreOriginalGraphic()
        {
            Graphic = OriginalGraphic;
        }

        public override void UpdateGraphicBySeason() => UpdateGraphicBySeason(PlayerIsGargoyle(World));

        private void UpdateGraphicBySeason(bool playerIsGargoyle)
        {
            SetGraphic(SeasonManager.GetSeasonGraphic(World.Season, OriginalGraphic));
            AllowedToDraw = CanBeDrawn(Graphic, playerIsGargoyle);
            IsVegetation = StaticFilters.IsVegetation(Graphic);
        }

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            base.Destroy();
            // reuse: Chunk.Destroy retires evicted map objects into MapObjectRecycler (the QueuedPool above predates World)
        }

        public override void ResetForReuse()
        {
            base.ResetForReuse();
            OriginalGraphic = 0;
            IsVegetation = false;
            Index = 0;
            _canBeTransparent = 0;
        }
    }
}