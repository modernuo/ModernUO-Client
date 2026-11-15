// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// A facet index with the land-height lookup, for callers that only read heights (the world map gump). Unlike a
    /// <see cref="Map"/> it owns no chunk slots and creates no chunks.
    /// </summary>
    internal readonly struct FacetView
    {
        public readonly int Index;

        public FacetView(int index)
        {
            Index = index;
        }

        public sbyte GetTileZ(int x, int y) => Client.Game.UO.FileManager.Maps.GetLandZ(Index, x, y);
    }
}
