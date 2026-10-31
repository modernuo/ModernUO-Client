// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// When GameScene.UpdateMaxDrawZ (the max Z and the "under a roof" decision, shared by both world renderers) has to run
    /// again. Upstream recomputed only when the player's tile or z changed, which was enough while every chunk loaded the
    /// moment it was first asked for. With incremental chunk loading (Map.UpdateLoading) a teleport's first frame can come
    /// before the player's chunk (or the diagonal one the roof test reads) is loaded: the decision then sees no statics,
    /// says "not under a roof" (max Z 127), and the old position check kept that answer until the player moved, so the
    /// roof stayed drawn over a player standing inside (known-issues "Gpu world renderer: the roof over the player is
    /// sometimes drawn"). A decision made while a chunk it reads was missing is not kept: it runs again every frame (a
    /// couple of null checks) until both chunks are there.
    /// </summary>
    internal struct MaxDrawZLatch
    {
        private int _x, _y, _z;
        private bool _valid, _complete;

        /// <summary>Frames since the last decision that had a chunk missing (diagnostics).</summary>
        public int IncompleteFrames;

        public readonly bool IsIncomplete => _valid && !_complete;

        public readonly bool NeedsUpdate(int x, int y, int z, bool force) =>
            force || !_valid || !_complete || x != _x || y != _y || z != _z;

        /// <param name="complete">every chunk the decision reads was loaded</param>
        public void Record(int x, int y, int z, bool complete)
        {
            bool samePlace = _valid && x == _x && y == _y && z == _z;
            IncompleteFrames = complete ? 0 : samePlace && !_complete ? IncompleteFrames + 1 : 1;
            _x = x;
            _y = y;
            _z = z;
            _valid = true;
            _complete = complete;
        }
    }
}
