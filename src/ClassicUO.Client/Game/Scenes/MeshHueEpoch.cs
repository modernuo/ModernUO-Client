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
    /// graphic and hue keeps its vertex hue without a recompute. The graphic is in the key because it can change in place
    /// without a mesh rebuild (World.ChangeSeason, Static.SetGraphic) and selects the partial-hue flag. Stretched land
    /// changes only with the land's geometry, which rebuilds the chunk mesh (keys reset).
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
        /// Sets the sprite's vertex hue from <paramref name="source"/> when the sprite's key is not this epoch,
        /// <paramref name="objGraphic"/> and <paramref name="objHue"/>. Returns true when the hue was kept.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Apply<T>(MeshLayer layer, int index, int epoch, ushort objGraphic, ushort objHue, ref T source)
            where T : struct, IMeshHueSource
        {
            long key = MeshLayer.HueKey(epoch, objGraphic, objHue);

            if (layer.HueKeyAt(index) == key)
            {
                return true;
            }

            source.HueOf(out float hueX, out float hueY);
            layer.SetHue(index, hueX, hueY, key);

            return false;
        }

        /// <summary>Invalidates every key made so far.</summary>
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
