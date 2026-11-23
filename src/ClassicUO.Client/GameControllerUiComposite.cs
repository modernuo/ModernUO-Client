// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO
{
    /// <summary>ui_border_bake (G11a): where the baked border is stale (RenderTargets has the design).</summary>
    internal static class UiComposite
    {
        /// <summary>
        /// Where the UI target changed this frame, from the B8 results (GameControllerUiCache): nothing when the final pass
        /// was kept; only the cursor's old and new rects when the gumps were kept and only the cursor stage was redrawn;
        /// everywhere otherwise. <paramref name="cursorBounds"/> carries the cursor rect between frames.
        /// </summary>
        public static void NoteUiChange(UiBorderBake bake, in RenderGeometry geometry, RecordingResult gumps, RecordingResult final,
            RecordedPass finalPass, ref Rectangle? cursorBounds)
        {
            Rectangle? now = final != RecordingResult.Aborted && finalPass.TryGetBounds(1, out Rectangle b) ? b : null;

            if (final != RecordingResult.Same)
            {
                if (gumps == RecordingResult.Same && final == RecordingResult.Changed && now.HasValue && cursorBounds.HasValue)
                {
                    bake.AddDirtyUi(cursorBounds.Value, geometry);
                    bake.AddDirtyUi(now.Value, geometry);
                }
                else
                {
                    bake.InvalidateAll();
                }
            }

            cursorBounds = now;
        }
    }

    internal partial class GameController
    {
        // ui_border_bake: the cursor stage's rect in the UI target at the last frame (UiComposite.NoteUiChange)
        private Rectangle? _uiCursorBounds;

        private void NoteUiChange(RecordingResult gumps, RecordingResult final)
            => UiComposite.NoteUiChange(_renderTargets.BorderBakeState, _geometry, gumps, final, _uiCache.Final, ref _uiCursorBounds);

        private void NoteUiChangedEverywhere()
        {
            _renderTargets.BorderBakeState.InvalidateAll();
            _uiCursorBounds = null;
        }
    }
}
