// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Renderer;

namespace ClassicUO.Game.Scenes
{
    /// <summary>Computes a meshed sprite's hue the per-frame way (GameScene.MeshHueOf).</summary>
    internal interface IMeshHueSource
    {
        void HueOf(out float hueX, out float hueY);
    }

    /// <summary>
    /// mesh_hue_on_change: the frame-wide inputs of a meshed sprite's hue (Legacy chunk meshes). <see cref="Epoch"/> moves
    /// when one of them changes; a sprite whose <see cref="Renderer.MeshLayer.HueKeyAt"/> holds this epoch and its object's
    /// hue keeps its vertex hue without a recompute. The per-object inputs that stay out of the key (partial-hue flag,
    /// stretched land) only change with the graphic or the land's geometry, which rebuild the chunk mesh (keys reset).
    /// </summary>
    internal sealed class MeshHueEpoch
    {
        private bool _rangeColor, _deadGray, _started;
        private int _rangeX, _rangeY, _viewRange;

        /// <summary>Never 0 (0 is the layer's "no key").</summary>
        public int Epoch { get; private set; } = 1;

        /// <summary>
        /// Called once per frame before the walk. <paramref name="rangeColor"/>: NoColorObjectsOutOfRange; the range centre
        /// and view range count only while it is on. <paramref name="deadGray"/>: the player is dead and
        /// EnableBlackWhiteEffect is on.
        /// </summary>
        public void Update(bool rangeColor, int rangeX, int rangeY, int viewRange, bool deadGray)
        {
            if (!rangeColor)
            {
                rangeX = rangeY = viewRange = 0;
            }

            if (_started && rangeColor == _rangeColor && deadGray == _deadGray && rangeX == _rangeX && rangeY == _rangeY
                && viewRange == _viewRange)
            {
                return;
            }

            _started = true;
            _rangeColor = rangeColor;
            _deadGray = deadGray;
            _rangeX = rangeX;
            _rangeY = rangeY;
            _viewRange = viewRange;
            Bump();
        }

        /// <summary>
        /// Sets the sprite's vertex hue from <paramref name="source"/>: every call with <paramref name="onChange"/> off; with it
        /// on only when the sprite's key is not this epoch and <paramref name="objHue"/>. Returns true when the hue was kept.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Apply<T>(MeshLayer layer, int index, bool onChange, int epoch, ushort objHue, ref T source)
            where T : struct, IMeshHueSource
        {
            float hueX, hueY;

            if (onChange)
            {
                long key = MeshLayer.HueKey(epoch, objHue);

                if (layer.HueKeyAt(index) == key)
                {
                    return true;
                }

                source.HueOf(out hueX, out hueY);
                layer.SetHue(index, hueX, hueY, key);

                return false;
            }

            // the old per-frame path, without a key store (keys from an earlier on period never match: BeginMeshHueFrame
            // moves the epoch when the switch changes)
            source.HueOf(out hueX, out hueY);
            layer.SetHueKeyless(index, hueX, hueY);

            return false;
        }

        /// <summary>Invalidates every key made so far (the switch turned on, a new map).</summary>
        public void Bump()
        {
            Epoch++;

            if (Epoch == 0)
            {
                Epoch = 1;
            }
        }
    }
}
