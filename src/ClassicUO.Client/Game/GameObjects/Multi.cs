// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class Multi : GameObject
    {
        //private static readonly QueuedPool<Multi> _pool = new QueuedPool<Multi>
        //(
        //    Constants.PREDICTABLE_MULTIS,
        //    m =>
        //    {
        //        m.IsDestroyed = false;
        //        m.AlphaHue = 0;
        //        m.FoliageIndex = 0;
        //        m.IsHousePreview = false;
        //        m.MultiOffsetX = m.MultiOffsetY = m.MultiOffsetZ = 0;
        //        m.IsCustom = false;
        //        m.State = 0;
        //        m.IsMovable = false;
        //        m.Offset = Vector3.Zero;
        //    }
        //);
        private ushort _originalGraphic;


        public string Name => ItemData.Name;

        public ref StaticTiles ItemData => ref Client.Game.UO.FileManager.TileData.StaticData[Graphic];
        public bool IsCustom;
        public bool IsVegetation;
        public int MultiOffsetX;
        public int MultiOffsetY;
        public int MultiOffsetZ;
        public bool IsMovable;
        public CUSTOM_HOUSE_MULTI_OBJECT_FLAGS State = 0;


        public Multi(World world) : base(world) { }

        public static Multi Create(World world, ushort graphic) => Create(world, graphic, PlayerIsGargoyle(world));

        /// <summary><see cref="Create(World, ushort)"/> with the player's race read once by the caller (Item's multi
        /// build creates every component in one pass).</summary>
        public static Multi Create(World world, ushort graphic, bool playerIsGargoyle)
        {
            Multi m = new Multi(world); // _pool.GetOne();
            m.Graphic = m._originalGraphic = graphic;
            m.UpdateGraphicBySeason(playerIsGargoyle); // also sets AllowedToDraw for the swapped graphic

            if (m.ItemData.Height > 5 || m.ItemData.Height == 0)
            {
                m._canBeTransparent = 1;
            }
            else if (m.ItemData.IsRoof || m.ItemData.IsSurface && m.ItemData.IsBackground || m.ItemData.IsWall)
            {
                m._canBeTransparent = 1;
            }
            else if (m.ItemData.Height == 5 && m.ItemData.IsSurface && !m.ItemData.IsBackground)
            {
                m._canBeTransparent = 1;
            }
            else
            {
                m._canBeTransparent = 0;
            }

            return m;
        }

        public override void UpdateGraphicBySeason() => UpdateGraphicBySeason(PlayerIsGargoyle(World));

        private void UpdateGraphicBySeason(bool playerIsGargoyle)
        {
            Graphic = SeasonManager.GetSeasonGraphic(World.Season, _originalGraphic);
            AllowedToDraw = CanBeDrawn(Graphic, playerIsGargoyle); // the swapped graphic's draw flags, like Static.UpdateGraphicBySeason
            IsVegetation = StaticFilters.IsVegetation(Graphic);

            if (TileChunk != null || PendingBlock >= 0)
            {
                RequestFirstSightArt(); // a season change on a placed component: the new graphic
            }
        }

        // house and boat components are built from the multi data after their chunk was: request their art when placed
        protected override void RequestFirstSightArt()
        {
            global::ClassicUO.Game.Map.FirstSightArt.RequestPlaced(Graphic);
        }

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            base.Destroy();
            //_pool.ReturnOne(this);
        }
    }
}