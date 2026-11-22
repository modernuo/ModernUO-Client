// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Diagnostics;

namespace ClassicUO
{
    /// <summary>ui_border_bake (G11a, default off): the switch (RenderTargets.BorderBake has the design).</summary>
    internal static class UiComposite
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_UI_BORDER_BAKE") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.UiBorderBake ?? false);

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
        // ui_border_bake was on last frame (turned off: the border target is freed unless the checker still draws it)
        private bool _borderBakeWas;

#if CUO_DEVTOOLS
        private static bool UiCompositeCheckOn => _uiCompositeCheckEvery > 0;
#else
        private const bool UiCompositeCheckOn = false;
#endif

        private void NoteUiChange(RecordingResult gumps, RecordingResult final)
            => UiComposite.NoteUiChange(_renderTargets.BorderBakeState, _geometry, gumps, final, _uiCache.Final, ref _uiCursorBounds);

        private void NoteUiChangedEverywhere()
        {
            _renderTargets.BorderBakeState.InvalidateAll();
            _uiCursorBounds = null;
        }

#if CUO_DEVTOOLS
        // ---- CUO_UI_COMPOSITE_CHECK=1|N (dev): every N frames (default 30), just before the frame's composite, both composites
        // are drawn into two scratch targets from the same inputs and compared pixel for pixel, inside and outside the world
        // viewport. Scratch targets, not the back buffer: FNA clears a DiscardContents back buffer whenever it is bound again
        // (GraphicsDevice.SetRenderTargets), so the frame's own composite must come after the check ----

        private static readonly int _uiCompositeCheckEvery = ParseCheckEvery(Environment.GetEnvironmentVariable("CUO_UI_COMPOSITE_CHECK"));
        private RenderTarget2D _uiCompositeBaked, _uiCompositeFull;
        private Color[] _uiCompositeBakedPixels, _uiCompositeFullPixels;
        private int _uiCompositeFrame, _uiCompositeChecks, _uiCompositeMismatched;
        private long _uiCompositeInside, _uiCompositeOutside, _uiCompositeLastLog;
        private string _uiCompositeFirst;

        private void CheckUiComposite(bool baked)
        {
            if (_uiCompositeCheckEvery == 0 || ++_uiCompositeFrame % _uiCompositeCheckEvery != 0)
            {
                return;
            }

            GraphicsDevice device = GraphicsDevice;
            PresentationParameters pp = device.PresentationParameters;
            int w = pp.BackBufferWidth, h = pp.BackBufferHeight;
            RenderGeometry g = _renderTargets.Geometry;

            if (w <= 0 || h <= 0 || w != g.BackBufferWidth || h != g.BackBufferHeight || _renderTargets.UiRenderTarget is not { IsDisposed: false })
            {
                return;
            }

            EnsureCompositeTarget(ref _uiCompositeBaked, device, w, h);
            EnsureCompositeTarget(ref _uiCompositeFull, device, w, h);

            if (_uiCompositeBakedPixels == null || _uiCompositeBakedPixels.Length != w * h)
            {
                _uiCompositeBakedPixels = new Color[w * h];
                _uiCompositeFullPixels = new Color[w * h];
            }

            device.SetRenderTarget(_uiCompositeBaked);
            _renderTargets.Draw(_uoSpriteBatch, _uiCompositeBaked, true);
            device.SetRenderTarget(_uiCompositeFull);
            _renderTargets.Draw(_uoSpriteBatch, _uiCompositeFull, false);

            _uiCompositeBaked.GetData(_uiCompositeBakedPixels);
            _uiCompositeFull.GetData(_uiCompositeFullPixels);

            Rectangle world = UiBorderBake.Intersect(g.WorldOnScreen, g.BackBufferBounds);
            long inside = 0, outside = 0;
            int first = -1;

            for (int i = 0; i < _uiCompositeBakedPixels.Length; i++)
            {
                if (_uiCompositeBakedPixels[i].PackedValue == _uiCompositeFullPixels[i].PackedValue)
                {
                    continue;
                }

                int x = i % w, y = i / w;

                if (world.Contains(x, y))
                {
                    inside++;
                }
                else
                {
                    outside++;
                }

                if (first < 0)
                {
                    first = i;
                }
            }

            _uiCompositeChecks++;

            if (first >= 0)
            {
                _uiCompositeMismatched++;
                _uiCompositeInside += inside;
                _uiCompositeOutside += outside;

                string where = world.Contains(first % w, first / w) ? "inside" : "outside";
                string line = $"check {_uiCompositeChecks}: {inside} px inside, {outside} px outside the viewport {world}; first ({first % w},{first / w}) {where} " +
                              $"baked {_uiCompositeBakedPixels[first].PackedValue:X8} vs full {_uiCompositeFullPixels[first].PackedValue:X8}";
                _uiCompositeFirst ??= line;
                Log.Warn($"[ui-composite-check] mismatch {line}");
            }

            long now = Stopwatch.GetTimestamp();

            if (now - _uiCompositeLastLog > Stopwatch.Frequency * 5)
            {
                _uiCompositeLastLog = now;
                UiBorderBake bake = _renderTargets.BorderBakeState;
                Log.Info($"[ui-composite-check] ui_border_bake {(baked ? "on" : "off")}, checks {_uiCompositeChecks}, mismatched {_uiCompositeMismatched} " +
                         $"({_uiCompositeInside} px inside, {_uiCompositeOutside} px outside), bakes full {bake.FullBakes} partial {bake.PartialBakes} " +
                         $"px {bake.BakedPixels}" + (_uiCompositeFirst != null ? ", first " + _uiCompositeFirst : ""));
            }
        }

        private static void EnsureCompositeTarget(ref RenderTarget2D target, GraphicsDevice device, int w, int h)
        {
            if (target == null || target.IsDisposed || target.Width != w || target.Height != h)
            {
                target?.Dispose();
                target = new RenderTarget2D(device, w, h, false, device.PresentationParameters.BackBufferFormat, DepthFormat.None, 0,
                    RenderTargetUsage.PreserveContents);
            }
        }
#endif
    }
}
