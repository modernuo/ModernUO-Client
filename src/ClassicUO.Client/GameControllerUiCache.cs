// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Managers;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Diagnostics;

namespace ClassicUO
{
    // B8: the UI render cache (setting ui_render_cache).
    //
    // The UI pass still runs in full every frame (scene DrawUI, the gump tree, the cursor), so every side effect of drawing
    // (SelectedObject.HealthbarObject/SelectedContainer, world text ordering, ...) is kept. What is skipped is the GPU work:
    // the batcher records the pass instead of drawing it and compares the recording, byte for byte, with the last one drawn
    // into the same target. Equal recordings draw equal pixels, so the target is left as it is.
    //
    //   1. Scene UI and gumps are recorded with the UI cache target bound (it keeps its contents). Changed: cleared and drawn.
    //   2. The UI target gets a copy of the cache target (opaque blend, 1:1, point sampling) and the cursor on top, recorded
    //      the same way. When stage 1 and the cursor are unchanged, the UI target, and its GPU pass, is left alone.
    //
    // What a recording can't see is checked here (UiCacheState.CheckEnvironment): the targets being recreated (resize, DPI,
    // device reset), the hue sampler textures bound outside the batcher, and any texture upload (TextureUploads.Epoch; the
    // minimap writes its atlas slot in place). CUO_UI_CACHE_CHECK=1|N verifies the cached UI target against a fresh replay
    // of both recordings without the copy every N frames.
    internal unsafe partial class GameController
    {
        private readonly UiCacheState _uiCache = new();

        // CUO_UI_CACHE_FORCE=1 (dev): every frame counts as changed, to measure what the cache costs when the UI never repeats
        private static readonly bool _uiCacheForceDirty = Environment.GetEnvironmentVariable("CUO_UI_CACHE_FORCE") == "1";

        internal sealed class UiCacheState
        {
            public readonly RecordedPass Gumps = new();
            public readonly RecordedPass Final = new();

            private int _targetsVersion = -1, _uploadEpoch = -1;
            private Texture _hue0, _hue1;

            public void Invalidate()
            {
                Gumps.Invalidate();
                Final.Invalidate();
                _targetsVersion = -1;
            }

            // false when something outside the recordings changed since the last frame. epoch: TextureUploads.Epoch at the
            // frame's start, kept as is, so uploads made while this frame draws also count as a change next frame.
            public bool CheckEnvironment(int targetsVersion, GraphicsDevice device, int epoch)
            {
                Texture hue0 = device.Textures[1], hue1 = device.Textures[2];
                bool same = targetsVersion == _targetsVersion && epoch == _uploadEpoch && hue0 == _hue0 && hue1 == _hue1;

                _targetsVersion = targetsVersion;
                _uploadEpoch = epoch;
                _hue0 = hue0;
                _hue1 = hue1;

                return same;
            }

            /// <summary>
            /// A stage's outcome. A recording equal to the last one counts as Same only if nothing outside it changed: the
            /// environment at the frame's start, and no texture upload while the stage drew (the minimap rewrites its atlas
            /// slot during its own draw, with the same texture, UVs and vertices).
            /// </summary>
            public static RecordingResult Resolve(RecordingResult recorded, bool envSame, int epochBefore, int epochAfter)
                => recorded == RecordingResult.Same && (!envSame || epochBefore != epochAfter) ? RecordingResult.Changed : recorded;
        }

        private void DrawSceneUiAndGumps()
        {
            FrameStats.Begin(FramePhase.SceneDrawUi);
            _uoSpriteBatch.Begin();
            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Scene.DrawUI(_uoSpriteBatch);
            }
            _uoSpriteBatch.End();
            FrameStats.End(FramePhase.SceneDrawUi);

            FrameStats.Begin(FramePhase.UiDraw);
            UIManager.Draw(_uoSpriteBatch);
            FrameStats.End(FramePhase.UiDraw);
        }

        private void DrawCursor()
        {
            FrameStats.Begin(FramePhase.Cursor);
            _uoSpriteBatch.Begin();
            UO.GameCursor?.Draw(_uoSpriteBatch);
            _uoSpriteBatch.End();
            FrameStats.End(FramePhase.Cursor);
        }

        private void DrawUiCached()
        {
            UltimaBatcher2D batcher = _uoSpriteBatch;
            GraphicsDevice device = GraphicsDevice;
            RenderTarget2D cacheTarget = _renderTargets.UiCacheRenderTarget;
            RenderTarget2D uiTarget = _renderTargets.UiRenderTarget;

            if (_uiCacheForceDirty)
            {
                _uiCache.Invalidate();
            }

            int epoch = TextureUploads.Epoch;
            bool envSame = _uiCache.CheckEnvironment(_renderTargets.UiTargetsVersion, device, epoch);

            // 1. scene UI and gumps
            device.SetRenderTarget(cacheTarget);
            batcher.BeginRecording(_uiCache.Gumps, Color.Transparent);
            DrawSceneUiAndGumps();
            RecordingResult gumps = UiCacheState.Resolve(batcher.StopRecording(), envSame, epoch, TextureUploads.Epoch);

            Submit(gumps, _uiCache.Gumps);

            // 2. the copy and the cursor
            device.SetRenderTarget(uiTarget);
            batcher.BeginRecording(_uiCache.Final, Color.Transparent);
            batcher.Begin();
            batcher.SetBlendState(BlendState.Opaque);
            batcher.Draw(cacheTarget, new Rectangle(0, 0, cacheTarget.Width, cacheTarget.Height), Vector3.UnitZ, 0f);
            batcher.SetBlendState(null);
            batcher.End();
            DrawCursor();
            RecordingResult final = UiCacheState.Resolve(batcher.StopRecording(), gumps == RecordingResult.Same, epoch, TextureUploads.Epoch);

            Submit(final, _uiCache.Final);

            FrameStats.Set(FrameCounter.UiCached, gumps == RecordingResult.Same ? 1 : 0);
            FrameStats.Set(FrameCounter.UiUntouched, final == RecordingResult.Same ? 1 : 0);

            if (_uiCacheCheckEvery > 0)
            {
                CheckUiCache(uiTarget, gumps == RecordingResult.Same, final == RecordingResult.Same);
            }
        }

        private void Submit(RecordingResult result, RecordedPass pass)
        {
            switch (result)
            {
                case RecordingResult.Same:
                    _uoSpriteBatch.DropRecording();

                    break;

                case RecordingResult.Changed:
                    GraphicsDevice.Clear(Color.Transparent);
                    _uoSpriteBatch.SubmitRecording(pass);

                    break;

                // Aborted: already drawn directly, and the pass is invalid
            }
        }

        // ---- CUO_UI_CACHE_CHECK=1|N (dev): every N frames (default 30) the UI target is compared with two references ----
        //
        //   replay: both recordings replayed into a scratch target, the cursor straight over the gumps (no copy). Catches a
        //           kept target that the current textures or states would no longer draw, and an inexact copy.
        //   direct: the UI pass run a second time without recording (scene DrawUI, gumps, cursor), as with the cache off.
        //           Catches a recording or replay that draws differently from the batcher's own flushes.
        //
        // The second pass repeats drawing's side effects within the frame (selection, world text order), so check runs are
        // for correctness only. CUO_UI_CACHE_CHECK_DIR=dir saves the first mismatches as PNGs.

        private static readonly int _uiCacheCheckEvery = ParseCheckEvery(Environment.GetEnvironmentVariable("CUO_UI_CACHE_CHECK"));
        private static readonly string _uiCacheCheckDir = Environment.GetEnvironmentVariable("CUO_UI_CACHE_CHECK_DIR");
        private RenderTarget2D _uiCheckReplay, _uiCheckDirect;
        private Color[] _uiCheckTarget, _uiCheckRef;
        private int _uiCheckFrame, _uiChecks, _uiChecksCached, _uiChecksUntouched, _uiReplayMismatches, _uiDirectMismatches, _uiCheckSaved;
        private long _uiReplayPixels, _uiDirectPixels, _uiCheckLastLog;
        private string _uiCheckFirst;

        private static int ParseCheckEvery(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "0")
            {
                return 0;
            }

            return int.TryParse(value, out int n) && n > 1 ? n : 30;
        }

        private void CheckUiCache(RenderTarget2D uiTarget, bool gumpsCached, bool untouched)
        {
            if (++_uiCheckFrame % _uiCacheCheckEvery != 0 || !_uiCache.Gumps.IsValid || !_uiCache.Final.IsValid)
            {
                return;
            }

            GraphicsDevice device = GraphicsDevice;
            int w = uiTarget.Width, h = uiTarget.Height;

            EnsureCheckTarget(ref _uiCheckReplay, uiTarget);
            EnsureCheckTarget(ref _uiCheckDirect, uiTarget);

            if (_uiCheckTarget == null || _uiCheckTarget.Length != w * h)
            {
                _uiCheckTarget = new Color[w * h];
                _uiCheckRef = new Color[w * h];
            }

            device.SetRenderTarget(_uiCheckReplay);
            device.Clear(Color.Transparent);
            _uoSpriteBatch.ReplayPass(_uiCache.Gumps);
            _uoSpriteBatch.ReplayPass(_uiCache.Final, 1); // segment 0 is the copy of the cache target

            device.SetRenderTarget(_uiCheckDirect);
            device.Clear(Color.Transparent);
            DrawSceneUiAndGumps();
            DrawCursor();
            device.SetRenderTarget(null);

            string state = $"gumps {(gumpsCached ? "kept" : "drawn")}, ui {(untouched ? "kept" : "drawn")}";
            uiTarget.GetData(_uiCheckTarget);
            _uiCheckReplay.GetData(_uiCheckRef);
            int replayDiffs = CountDiffs("replay", state, uiTarget, _uiCheckReplay);
            _uiCheckDirect.GetData(_uiCheckRef);
            int directDiffs = CountDiffs("direct", state, uiTarget, _uiCheckDirect);

            _uiChecks++;
            _uiChecksCached += gumpsCached ? 1 : 0;
            _uiChecksUntouched += untouched ? 1 : 0;
            _uiReplayMismatches += replayDiffs > 0 ? 1 : 0;
            _uiReplayPixels += replayDiffs;
            _uiDirectMismatches += directDiffs > 0 ? 1 : 0;
            _uiDirectPixels += directDiffs;

            long now = Stopwatch.GetTimestamp();

            if (now - _uiCheckLastLog > Stopwatch.Frequency * 5)
            {
                _uiCheckLastLog = now;
                Log.Info($"[ui-cache-check] checks {_uiChecks} (gumps kept {_uiChecksCached}, ui kept {_uiChecksUntouched}), " +
                         $"replay mismatched {_uiReplayMismatches} ({_uiReplayPixels} px), direct mismatched {_uiDirectMismatches} ({_uiDirectPixels} px)" +
                         (_uiCheckFirst != null ? ", first " + _uiCheckFirst : ""));
            }
        }

        private static void EnsureCheckTarget(ref RenderTarget2D target, RenderTarget2D like)
        {
            if (target == null || target.IsDisposed || target.Width != like.Width || target.Height != like.Height)
            {
                target?.Dispose();
                target = new RenderTarget2D(like.GraphicsDevice, like.Width, like.Height, false, like.Format, like.DepthStencilFormat, 0,
                    RenderTargetUsage.PreserveContents);
            }
        }

        private int CountDiffs(string reference, string state, RenderTarget2D uiTarget, RenderTarget2D referenceTarget)
        {
            int w = uiTarget.Width, diffs = 0, first = -1;

            for (int i = 0; i < _uiCheckTarget.Length; i++)
            {
                if (_uiCheckTarget[i].PackedValue != _uiCheckRef[i].PackedValue && diffs++ == 0)
                {
                    first = i;
                }
            }

            if (diffs == 0)
            {
                return 0;
            }

            _uiCheckFirst ??= $"{reference} {diffs} px at check frame {_uiCheckFrame}, ({first % w},{first / w}) target " +
                              $"{_uiCheckTarget[first].PackedValue:X8} reference {_uiCheckRef[first].PackedValue:X8}, {state}";

            if (!string.IsNullOrEmpty(_uiCacheCheckDir) && _uiCheckSaved < 6)
            {
                _uiCheckSaved++;
                System.IO.Directory.CreateDirectory(_uiCacheCheckDir);
                string name = System.IO.Path.Combine(_uiCacheCheckDir, $"uicheck_{Environment.ProcessId}_{_uiCheckFrame}_{reference}");

                using (var f = System.IO.File.Create(name + "_target.png"))
                {
                    uiTarget.SaveAsPng(f, uiTarget.Width, uiTarget.Height);
                }

                using (var f = System.IO.File.Create(name + "_reference.png"))
                {
                    referenceTarget.SaveAsPng(f, referenceTarget.Width, referenceTarget.Height);
                }

                Log.Info($"[ui-cache-check] {reference} mismatch, {diffs} px, {state}: saved {name}_*.png");
            }

            return diffs;
        }
    }
}
