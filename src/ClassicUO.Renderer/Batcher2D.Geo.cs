// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Renderer
{
    public sealed unsafe partial class UltimaBatcher2D
    {
        // clip-4 (depth_model): the GEO technique is in use, and the open shape (BeginGeo): the first queued sprite it covers
        // (-1: none) and its parameters (LegacyGeo)
        private bool _geoOn;
        private int _geoMark = -1;
        private int _geoCls;
        private float _geoCol, _geoRow;
        private double _geoBase, _geoB1, _geoB2, _geoB3;

        /// <summary>clip-4: whether the world pass draws with the geometric depth (<see cref="SetGeoDepth"/>).</summary>
        public bool GeoDepthOn => _geoOn;

        /// <summary>
        /// clip-4 (switch depth_model): draws the rest of this Begin/End with IsometricWorld.fx's HueGeoTechnique, depths
        /// relative to tile sum <paramref name="camSum"/> (<see cref="LegacyGeo.CamSum"/>); off, HueTechnique. End turns it off.
        /// </summary>
        public void SetGeoDepth(bool on, int camSum)
        {
            Flush();
            EndGeo();
            _geoOn = on;
            _basicUOEffect.SetGeo(on);

            if (on)
            {
                _basicUOEffect.GeoCam.SetValue((float)camSum);
            }

            _effectDirty = true;
        }

        /// <summary>
        /// clip-4: the sprites queued until <see cref="EndGeo"/> take shape class <paramref name="cls"/> with reference
        /// (<paramref name="col"/>, <paramref name="row"/>) and base tile sum <paramref name="baseSum"/>
        /// (<see cref="LegacyGeo.Apply"/>). Nothing while <see cref="GeoDepthOn"/> is false.
        /// </summary>
        public void BeginGeo(int cls, float col, float row, double baseSum)
        {
            if (!_geoOn)
            {
                return;
            }

            EndGeo();
            _geoMark = _numSprites;
            _geoCls = cls;
            _geoCol = col;
            _geoRow = row;
            _geoBase = baseSum;
        }

        /// <summary>clip-4: <see cref="BeginGeo"/> of the linear class, each vertex its own base (land corners).</summary>
        public void BeginGeoLinear(double b0, double b1, double b2, double b3)
        {
            if (!_geoOn)
            {
                return;
            }

            EndGeo();
            _geoMark = _numSprites;
            _geoCls = LegacyGeo.Linear;
            _geoBase = b0;
            _geoB1 = b1;
            _geoB2 = b2;
            _geoB3 = b3;
        }

        /// <summary>clip-4: ends <see cref="BeginGeo"/>: writes the class into the sprites queued since.</summary>
        public void EndGeo()
        {
            if (_geoMark < 0)
            {
                return;
            }

            PatchGeo();
            _geoMark = -1;
        }

        // the open shape's sprites still queued; Flush calls it before drawing them and moves the mark to the empty queue
        private void PatchGeo()
        {
            for (int i = _geoMark; i < _numSprites; i++)
            {
                if (_geoCls == LegacyGeo.Linear)
                {
                    LegacyGeo.ApplyLinear(ref _vertexInfo[i], _geoBase, _geoB1, _geoB2, _geoB3);
                }
                else
                {
                    LegacyGeo.Apply(ref _vertexInfo[i], _geoCls, _geoCol, _geoRow, _geoBase);
                }
            }
        }
    }
}
