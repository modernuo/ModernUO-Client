using ClassicUO.Assets;
using Uo.Pack;

namespace ModernUO.Studio.Assets
{
    /// <summary>One static on a map tile, as stored in the pack's region files.</summary>
    internal readonly record struct MapStatic(ushort Graphic, sbyte Z, ushort Hue);

    /// <summary>A map tile: its land and its statics, lowest first.</summary>
    internal sealed class MapTile
    {
        public int Facet, X, Y;
        public ushort Land;
        public sbyte LandZ;
        public readonly List<MapStatic> Statics = new();
    }

    /// <summary>Tile lookups and the radar overview over the pack's map regions (ClassicUO.Assets' MapLoader).</summary>
    internal static class MapData
    {
        public static readonly string[] FacetNames = { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "Ter Mur" };

        public static string FacetName(int facet) => facet >= 0 && facet < FacetNames.Length ? FacetNames[facet] : $"Facet {facet}";

        public static FacetInfo Info(StudioAssets assets, int facet) => assets.Manifest.Facets.FirstOrDefault(f => f.Index == facet);

        /// <summary>The land and statics of tile (x, y); false outside the facet or for an empty block.</summary>
        public static bool TryGetTile(StudioAssets assets, int facet, int x, int y, MapTile tile)
        {
            tile.Statics.Clear();
            tile.Facet = facet;
            tile.X = x;
            tile.Y = y;

            if (x < 0 || y < 0)
            {
                return false;
            }

            int cx = x & 7, cy = y & 7;
            ReadOnlySpan<LandCell> lands;
            ReadOnlySpan<StaticCell> statics;

            // edited blocks first (MapEdits), then the pack
            if (assets.Edits.TryGetBlock(facet, x >> 3, y >> 3, out var editedLand, out var editedStatics))
            {
                lands = editedLand;
                statics = editedStatics;
            }
            else if (assets.Files.Maps.TryGetBlock(facet, x >> 3, y >> 3, out MapBlockData block))
            {
                lands = block.Land;
                statics = block.Statics;
            }
            else
            {
                return false;
            }

            LandCell land = lands[cy * 8 + cx];
            tile.Land = land.TileId;
            tile.LandZ = land.Z;

            foreach (StaticCell s in statics)
            {
                if (s.X == cx && s.Y == cy)
                {
                    tile.Statics.Add(new MapStatic(s.Graphic, s.Z, s.Hue));
                }
            }

            // painter's order: lower first; equal z keeps the stored order (the client sorts the same way)
            tile.Statics.Sort((a, b) => a.Z.CompareTo(b.Z));

            return true;
        }

        public static bool TryGetLandZ(StudioAssets assets, int facet, int x, int y, out sbyte z)
        {
            z = 0;

            if (x < 0 || y < 0)
            {
                return false;
            }

            if (assets.Edits.TryGetBlock(facet, x >> 3, y >> 3, out var land, out _))
            {
                z = land[(y & 7) * 8 + (x & 7)].Z;

                return true;
            }

            return assets.Files.Maps.TryGetLandZ(facet, x, y, out z);
        }

        /// <summary>
        /// A top-down overview of a facet, <paramref name="scale"/> tiles per pixel: each pixel is the radar colour
        /// (radarcol.bin) of the highest static on its tile, else of the land. Built on a worker; regions are read and
        /// dropped one at a time.
        /// </summary>
        public static uint[] BuildRadar(StudioAssets assets, int facet, int scale, out int width, out int height, CancellationToken cancel)
        {
            var info = Info(assets, facet);
            width = info.Width / scale;
            height = info.Height / scale;
            var pixels = new uint[width * height];
            ushort[] radar = assets.Files.Hues.RadarCol;
            var maps = assets.Files.Maps;
            int regionBlocks = maps.RegionBlocks(facet);
            int regionsX = (info.BlocksX + regionBlocks - 1) / regionBlocks;
            int regionsY = (info.BlocksY + regionBlocks - 1) / regionBlocks;
            Span<int> topZ = stackalloc int[64];
            Span<ushort> top = stackalloc ushort[64];

            for (int ry = 0; ry < regionsY; ry++)
            {
                for (int rx = 0; rx < regionsX; rx++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var region = maps.ReadRegion(facet, rx, ry);

                    if (region == null)
                    {
                        continue;
                    }

                    for (int by = 0; by < region.BlocksH; by++)
                    {
                        for (int bx = 0; bx < region.BlocksW; bx++)
                        {
                            if (!region.TryGetBlock(bx, by, out MapBlockData block))
                            {
                                continue;
                            }

                            int baseX = (rx * regionBlocks + bx) * 8, baseY = (ry * regionBlocks + by) * 8;

                            for (int c = 0; c < 64; c++)
                            {
                                topZ[c] = int.MinValue;
                                top[c] = (ushort)block.Land[c].TileId;
                            }

                            foreach (StaticCell s in block.Statics)
                            {
                                int c = s.Y * 8 + s.X;

                                if (c < 64 && s.Z >= topZ[c])
                                {
                                    topZ[c] = s.Z;
                                    top[c] = (ushort)(0x4000 + s.Graphic);
                                }
                            }

                            for (int c = 0; c < 64; c++)
                            {
                                int x = baseX + (c & 7), y = baseY + (c >> 3);

                                if (x % scale != 0 || y % scale != 0)
                                {
                                    continue;
                                }

                                int px = x / scale, py = y / scale;

                                if (px >= width || py >= height)
                                {
                                    continue;
                                }

                                int index = topZ[c] == int.MinValue ? block.Land[c].TileId & 0x3FFF : top[c];
                                ushort color = radar != null && index < radar.Length ? radar[index] : (ushort)0;
                                pixels[py * width + px] = PackColor.ToRgb8(color) | 0xFF000000u;
                            }
                        }
                    }
                }
            }

            return pixels;
        }
    }
}
