// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Data;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// The world map's coordinate captions, rebuilt only when their inputs change: the gump draws them every frame, and
    /// formatting them there allocated a new string (plus the sextant text) per frame while the map was open.
    /// One instance per caption.
    /// </summary>
    internal sealed class WorldMapCoordinateText
    {
        private string _text;
        private int _x, _y, _z, _zoomIndex, _mapIndex;
        private bool _sextant;

        /// <summary>"x, y (z) [zoom]", plus the sextant position on a second line when asked for and known.</summary>
        public string Player(int x, int y, int z, int zoomIndex, int mapIndex, bool sextant)
        {
            if (_text != null && x == _x && y == _y && z == _z && zoomIndex == _zoomIndex && mapIndex == _mapIndex && sextant == _sextant)
            {
                return _text;
            }

            string text = $"{x}, {y} ({z}) [{zoomIndex}]";

            if (sextant && Sextant.FormatString(new Point(x, y), mapIndex, out string sextantCoords))
            {
                text += "\n" + sextantCoords;
            }

            Store(text, x, y, z, zoomIndex, mapIndex, sextant);

            return text;
        }

        /// <summary>"x y", plus the sextant position on a second line when asked for and known.</summary>
        public string Mouse(int x, int y, int mapIndex, bool sextant)
        {
            if (_text != null && x == _x && y == _y && mapIndex == _mapIndex && sextant == _sextant)
            {
                return _text;
            }

            string text = $"{x} {y}";

            if (sextant && Sextant.FormatString(new Point(x, y), mapIndex, out string sextantCoords))
            {
                text += "\n" + sextantCoords;
            }

            Store(text, x, y, 0, 0, mapIndex, sextant);

            return text;
        }

        private void Store(string text, int x, int y, int z, int zoomIndex, int mapIndex, bool sextant)
        {
            _text = text;
            _x = x;
            _y = y;
            _z = z;
            _zoomIndex = zoomIndex;
            _mapIndex = mapIndex;
            _sextant = sextant;
        }
    }
}
