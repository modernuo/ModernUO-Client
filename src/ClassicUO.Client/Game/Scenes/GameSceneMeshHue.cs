// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // mesh_hue_on_change: Legacy meshed sprites recompute their hue only when its inputs change (MeshHueEpoch, the
        // object's hue, a mesh rebuild) instead of every frame
        private readonly MeshHueEpoch _meshHueEpoch = new MeshHueEpoch();

        private void BeginMeshHueFrame()
        {
            var profile = ProfileManager.CurrentProfile;
            _meshHueEpoch.Update(
                profile.NoColorObjectsOutOfRange,
                _world.RangeSize.X,
                _world.RangeSize.Y,
                _world.ClientViewRange,
                _world.Player.IsDead && profile.EnableBlackWhiteEffect
            );
        }
    }
}
