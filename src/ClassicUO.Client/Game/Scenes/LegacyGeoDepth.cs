// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using Uo.Render;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// clip-4: the Legacy world's shapes for the geometric depth model (switch <c>depth_model</c>, <see cref="DepthModel"/>):
    /// the class, reference point and base tile sum each sprite gives the batcher (<see cref="UltimaBatcher2D.BeginGeo"/>) or
    /// the chunk mesh (<see cref="LegacyGeo.Apply"/>), as the Gpu's sprite.hlsl VSMain GEO gives them: land (flat; stretched
    /// land by its corners' tile sums), statics, multis and items (wall, flat or box by tiledata, <see cref="SpriteInstance.GeoShapeBits"/>;
    /// map statics whose top is at or under their tile's land are buried), mobiles and corpses (the composite around the
    /// anchor). Effects, shadows and seated mobiles keep the depth key (as on the Gpu). References are world pixels less the
    /// frame's draw offset (the batcher's screen pixels; the chunk mesh passes 0, 0).
    /// </summary>
    internal static class LegacyGeoDepth
    {
        private static int _offX, _offY;

        // wet_surface_depth at BeginFrame: the mask the batcher shapes apply to ZGeoWet (the chunk mesh keeps its build's)
        private static uint _wetMask = ~SpriteInstance.ZGeoWet;

        /// <summary>The Legacy render lists' draw offset (screen = world − offset).</summary>
        public static void BeginFrame(int offsetX, int offsetY)
        {
            _offX = offsetX;
            _offY = offsetY;
            _wetMask = WetMask(WetSurfaceDepth.On);
        }

        /// <summary>The shape-bit mask for wet_surface_depth <paramref name="on"/>: off clears <see cref="SpriteInstance.ZGeoWet"/>,
        /// so wet flats keep FlatDrop.</summary>
        public static uint WetMask(bool on) => on ? ~0u : ~SpriteInstance.ZGeoWet;

        /// <summary>A static, multi piece or item on tile (tx, ty) at z with tiledata <paramref name="height"/> and shape
        /// <paramref name="bits"/> (GeoDepth.Static; a flat with <see cref="SpriteInstance.ZGeoWet"/> drops GeoDepth.WetDrop:
        /// callers pass that bit only with wet_surface_depth on, <see cref="WetMask"/>).</summary>
        public static void Tile(int tx, int ty, int z, int height, uint bits, bool buried, int offX, int offY,
            out int cls, out float col, out float row, out double baseSum)
        {
            var (nx, ny) = GeoDepth.NorthVertex(tx, ty, z);
            baseSum = tx + ty + z * (double)GeoDepth.ZBias - (buried ? GeoDepth.BuriedDrop : 0.0);
            col = nx - offX;

            if ((bits & SpriteInstance.ZGeoWall) != 0)
            {
                cls = LegacyGeo.Wall;
                baseSum += GeoDepth.WallBias;
                row = ny - offY;
            }
            else if ((bits & SpriteInstance.ZGeoFlat) != 0)
            {
                bool wet = (bits & SpriteInstance.ZGeoWet) != 0;
                cls = wet ? LegacyGeo.FlatWet : LegacyGeo.Flat;
                baseSum -= wet ? GeoDepth.WetDrop : GeoDepth.FlatDrop;
                row = ny - offY;
            }
            else
            {
                cls = LegacyGeo.Box;
                row = ny - 4 * height - offY;
            }
        }

        /// <summary>A land tile (x, y, z) drawn as a flat quad (GeoDepth.Land).</summary>
        public static void Land(int x, int y, int z, int offX, int offY, out float col, out float row, out double baseSum)
        {
            var (nx, ny) = GeoDepth.NorthVertex(x, y, z);
            col = nx - offX;
            row = ny - offY;
            baseSum = x + y - (double)GeoDepth.FlatDrop + (z - 2) * (double)GeoDepth.ZBias;
        }

        /// <summary>Stretched land (x, y, z): the base of its top corner; the right and left corners are one more, the bottom
        /// two more (sprite.hlsl FLAG_STRETCH CornerSum: each corner is a world point whose tile sum is exact).</summary>
        public static double StretchedTop(int x, int y, int z) => x + y - (double)GeoDepth.FlatDrop + (z - 2) * (double)GeoDepth.ZBias;

        /// <summary>A mobile composite (or corpse) whose anchor (MobileView's draw point) is at (<paramref name="ax"/>,
        /// <paramref name="ay"/>) in the vertices' space, at height <paramref name="z"/> (GeoDepth.MobileBiased).</summary>
        public static void Mobile(int ax, int ay, int z, int offY, out float col, out float row, out double baseSum)
        {
            int cy = ay + 3;
            col = ax;
            row = cy;
            baseSum = (cy + offY + 22 + 4.0 * z) / 22.0 + GeoDepth.MobileBias + z * (double)GeoDepth.ZBias;
        }

        /// <summary>Whether a map static's top (z + height) is at or under its tile's land (the baker's ZGeoBuried); never a flat.</summary>
        public static bool Buried(int z, int height, uint bits, int landZ, bool hasLand) =>
            hasLand && (bits & SpriteInstance.ZGeoFlat) == 0 && z + height <= landZ;

        /// <summary>The land z on <paramref name="obj"/>'s tile (its tile list).</summary>
        public static bool TileLandZ(TileListObject obj, out int z)
        {
            for (TileListObject o = obj; o != null; o = o.TPrevious)
            {
                if (o is GameObjects.Land l)
                {
                    z = l.Z;

                    return true;
                }
            }

            for (TileListObject o = obj.TNext; o != null; o = o.TNext)
            {
                if (o is GameObjects.Land l)
                {
                    z = l.Z;

                    return true;
                }
            }

            z = 0;

            return false;
        }

        /// <summary>The chunk mesh's static or multi quad (world pixels): its shape with <paramref name="geo"/> (depth_model at
        /// the build), else untouched.</summary>
        public static void ApplyTile(ref UltimaBatcher2D.PositionNormalTextureColor4 v, bool geo, int tx, int ty, int z, int height, uint bits,
            bool buried)
        {
            if (!geo)
            {
                return;
            }

            Tile(tx, ty, z, height, bits, buried, 0, 0, out int cls, out float col, out float row, out double baseSum);
            LegacyGeo.Apply(ref v, cls, col, row, baseSum);
        }

        /// <summary>The chunk mesh's land quad (world pixels): with <paramref name="geo"/>, stretched land (the diamond the
        /// texmap draws: top, right, left, bottom) by its corners, other land as a flat; else untouched.</summary>
        public static void ApplyLand(ref UltimaBatcher2D.PositionNormalTextureColor4 v, bool geo, int x, int y, int z, bool corners)
        {
            if (!geo)
            {
                return;
            }

            if (corners)
            {
                double top = StretchedTop(x, y, z);
                LegacyGeo.ApplyLinear(ref v, top, top + 1, top + 1, top + 2);

                return;
            }

            Land(x, y, z, 0, 0, out float col, out float row, out double baseSum);
            LegacyGeo.Apply(ref v, LegacyGeo.Flat, col, row, baseSum);
        }

        /// <summary>The shape bits of a tiledata row (<see cref="SpriteInstance.ZGeoWet"/> only with <paramref name="wet"/>,
        /// wet_surface_depth).</summary>
        public static uint Bits(ref StaticTiles td, bool wet) =>
            SpriteInstance.GeoShapeBits(td.IsWall, td.IsBackground, td.IsSurface, wet && td.IsWet, td.Height);

        // the batcher shapes' bits: wet_surface_depth as of BeginFrame
        private static uint FrameBits(ref StaticTiles td) =>
            SpriteInstance.GeoShapeBits(td.IsWall, td.IsBackground, td.IsSurface, td.IsWet, td.Height) & _wetMask;

        /// <summary>Opens the batcher shape of a land, static or multi view's draw (the render lists' views); nothing for the rest
        /// or with the GEO technique off. Close with <see cref="UltimaBatcher2D.EndGeo"/>.</summary>
        public static void BeginView(UltimaBatcher2D batcher, GameObject obj)
        {
            if (!batcher.GeoDepthOn)
            {
                return;
            }

            switch (obj)
            {
                case GameObjects.Land land:
                    if (land.IsStretched && Client.Game.UO.Texmaps.GetTexmap(Client.Game.UO.FileManager.TileData.LandData[land.Graphic].TexID).Texture != null)
                    {
                        double top = StretchedTop(land.X, land.Y, land.Z);
                        batcher.BeginGeoLinear(top, top + 1, top + 1, top + 2);
                    }
                    else
                    {
                        Land(land.X, land.Y, land.Z, _offX, _offY, out float col, out float row, out double b);
                        batcher.BeginGeo(LegacyGeo.Flat, col, row, b);
                    }

                    break;

                case Static st:
                {
                    uint bits = FrameBits(ref st.ItemData);
                    bool hasLand = TileLandZ(st, out int landZ);
                    Tile(st.X, st.Y, st.Z, st.ItemData.Height, bits, Buried(st.Z, st.ItemData.Height, bits, landZ, hasLand), _offX, _offY,
                        out int cls, out float col, out float row, out double b);
                    batcher.BeginGeo(cls, col, row, b);

                    break;
                }

                case Multi multi:
                {
                    Tile(multi.X, multi.Y, multi.Z, multi.ItemData.Height, FrameBits(ref multi.ItemData), false, _offX, _offY,
                        out int cls, out float col, out float row, out double b);
                    batcher.BeginGeo(cls, col, row, b);

                    break;
                }
            }
        }

        /// <summary>Opens the batcher shape of an item drawn at screen (<paramref name="posX"/>, <paramref name="posY"/>): a corpse
        /// is a mobile composite around the corpse draw's anchor (ItemSpriteExtract.EmitCorpse), other items take their tiledata
        /// shape on their tile (not buried: the Gpu marks only baked statics).</summary>
        public static void BeginItem(UltimaBatcher2D batcher, in ItemSpriteInput item, int posX, int posY)
        {
            if (item.IsCorpse)
            {
                int ax = posX + (int)item.Offset.X + 22;
                int ay = posY + (int)(item.Offset.Y + item.Offset.Z) - 3 + 22;
                Mobile(ax, ay, item.Z, _offY, out float mcol, out float mrow, out double mb);
                batcher.BeginGeo(LegacyGeo.Mobile, mcol, mrow, mb);

                return;
            }

            Tile(item.X, item.Y, item.Z, item.TileHeight, item.TileGeoBits & _wetMask, false, _offX, _offY, out int cls, out float col, out float row, out double b);
            batcher.BeginGeo(cls, col, row, b);
        }

        /// <summary>Opens the batcher shape of a mobile whose anchor is at screen (<paramref name="ax"/>, <paramref name="ay"/>).</summary>
        public static void BeginMobile(UltimaBatcher2D batcher, int ax, int ay, int z)
        {
            Mobile(ax, ay, z, _offY, out float col, out float row, out double b);
            batcher.BeginGeo(LegacyGeo.Mobile, col, row, b);
        }
    }
}
