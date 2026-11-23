// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// The world prepare makes no pick tests (CheckMouseSelection) in a frame whose mouse is not over the world
    /// (UIManager.IsMouseOverWorld false: over a gump, a modal, dragging a control, the SDL cursor hidden, outside the game
    /// viewport). GameScene.DrawUI clears such a frame's selection before anything outside the world draw reads it, so the
    /// selection the game sees is the same; the world draw no longer highlights the object under a gump.
    /// </summary>
    internal static class PickSkip
    {
        /// <summary>This fill makes no pick tests (GameScene.CheckMouseSelectionTimed).</summary>
        public static bool Off;

        /// <summary>Start of a world fill: whether its pick tests are skipped.</summary>
        public static void BeginFill(bool mouseOverWorld)
        {
            Off = !mouseOverWorld;
        }
    }
}
