// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // light_batch: the occluded lights of the batched path (their list index, first occluder, occluder count, hue alpha),
        // grown to the high-water mark and reused
        private int[] _lbLight = new int[64], _lbFirst = new int[64], _lbCount = new int[64];
        private float[] _lbHueZ = new float[64];

        // the light target before the lights: black, then the ambient level without the alternative lights
        private void ClearLightTarget(GraphicsDevice device)
        {
            device.Clear(ClearOptions.Target, Color.Black, 0f, 0);

            if (!UseAltLights)
            {
                float lightColor = _world.Light.IsometricLevel;

                if (ProfileManager.CurrentProfile.UseDarkNights)
                {
                    lightColor -= 0.04f;
                }

                device.Clear(
                    ClearOptions.Target,
                    new Vector4(lightColor, lightColor, lightColor, 1),
                    0f,
                    0
                );
            }
        }

        // the frame's lights into the light target: light by light, or with light_batch (and light_occlusion) the lights with no
        // occluders first in one batch, then the occluded ones
        private void DrawLightPass(UltimaBatcher2D batcher, ref Matrix matrix, int lightCount, bool batch)
        {
            // light_occlusion: the stencil the occluders of each light are written to (LightOcclusion)
            BeginLightOcclusion(batcher.GraphicsDevice);
            batcher.Begin(null, matrix);
            BeginLightOcclusionBatch(batcher);
            batcher.SetBlendState(BlendState.Additive);

            if (LightBatch.Batched(batch, _lightOcclusion))
            {
                DrawLightsBatched(batcher, ref matrix, lightCount);
            }
            else
            {
                bool stencilSet = false;

                for (int i = 0; i < lightCount; i++)
                {
                    ref FrameLights.Entry l = ref _frameLights[i];
                    ref readonly var lightInfo = ref Client.Game.UO.Lights.GetLight(l.ID);

                    if (lightInfo.Texture == null)
                    {
                        continue;
                    }

                    DrawLight(batcher, ref matrix, ref l, in lightInfo, LightHue(ref l), ref stencilSet);
                }

                if (stencilSet)
                {
                    batcher.SetStencil(null);
                }
            }

            EndLightOcclusionBatch(batcher);
            batcher.SetBlendState(null);
            batcher.End();
            EndLightOcclusion();
        }

        private static Vector3 LightHue(ref FrameLights.Entry l)
        {
            Vector3 hue;
            hue.X = l.Color;
            hue.Y =
                hue.X > 1.0f
                    ? l.IsHue
                        ? ShaderHueTranslator.SHADER_HUED
                        : ShaderHueTranslator.SHADER_LIGHTS
                    : ShaderHueTranslator.SHADER_NONE;
            hue.Z = 1f;

            return hue;
        }

        // light_batch: every light's occluders collected first (kept one after the other in _occluders for the frame); the
        // lights with none drawn as they come, with no stencil change between them (one batch); then each occluded light with
        // its masks as the per-light path draws it
        private void DrawLightsBatched(UltimaBatcher2D batcher, ref Matrix matrix, int lightCount)
        {
            int occluded = 0;
            _occBase = 0;

            for (int i = 0; i < lightCount; i++)
            {
                ref FrameLights.Entry l = ref _frameLights[i];
                ref readonly var lightInfo = ref Client.Game.UO.Lights.GetLight(l.ID);

                if (lightInfo.Texture == null)
                {
                    continue;
                }

                Vector3 hue = LightHue(ref l);

                if (!LightShare(ref l, ref matrix, ref hue))
                {
                    continue;
                }

                Vector2 pos = new Vector2(l.DrawX - lightInfo.UV.Width * 0.5f, l.DrawY - lightInfo.UV.Height * 0.5f);
                int n = CollectLightOccluders(ref l, pos, in lightInfo);

                if (n == 0)
                {
                    batcher.Draw(lightInfo.Texture, pos, lightInfo.UV, hue, 0f);

                    continue;
                }

                if (occluded == _lbLight.Length)
                {
                    int size = occluded * 2;
                    System.Array.Resize(ref _lbLight, size);
                    System.Array.Resize(ref _lbFirst, size);
                    System.Array.Resize(ref _lbCount, size);
                    System.Array.Resize(ref _lbHueZ, size);
                }

                _lbLight[occluded] = i;
                _lbFirst[occluded] = _occBase;
                _lbCount[occluded] = n;
                _lbHueZ[occluded] = hue.Z;
                occluded++;
                _occBase += n;
            }

            int total = _occBase;
            _occBase = 0;

            for (int j = 0; j < occluded; j++)
            {
                ref FrameLights.Entry l = ref _frameLights[_lbLight[j]];
                ref readonly var lightInfo = ref Client.Game.UO.Lights.GetLight(l.ID);
                Vector3 hue = LightHue(ref l);
                hue.Z = _lbHueZ[j];
                Vector2 pos = new Vector2(l.DrawX - lightInfo.UV.Width * 0.5f, l.DrawY - lightInfo.UV.Height * 0.5f);
                DrawMasked(batcher, ref matrix, ref l, _lbFirst[j], _lbCount[j], in lightInfo, pos, hue);
            }

            if (occluded > 0)
            {
                batcher.SetStencil(null);
            }

            // no texture kept past the frame (review P2 #7)
            for (int i = 0; i < total; i++)
            {
                _occluders[i].Texture = null;
            }
#if CUO_DEVTOOLS
            if (LightBatch.Check)
            {
                LightBatchCount(lightCount, occluded);
            }
#endif
        }

#if CUO_DEVTOOLS
        private long _lbPasses, _lbDrawn, _lbOccluded, _lbSets, _lbCompares, _lbDiffPixels, _lbMismatches;
        private int _lbSetsMax, _lbOccludedMax, _lbMaxChannel;
        private uint _lbSummaryAt, _lbCompareAt;
        private Rectangle[] _lbQuads = new Rectangle[64];
        private int[] _lbSetOf = new int[64];
        private Color[] _lbRef, _lbCur;
        private int _lbRefLength;

        // CUO_LIGHT_BATCH_CHECK: a batched pass's counts, and the non-overlapping sets its occluded lights would form (the
        // batching design's step 2: count only, the pass does not draw by sets)
        private void LightBatchCount(int lightCount, int occluded)
        {
            if (_lbQuads.Length < occluded)
            {
                _lbQuads = new Rectangle[occluded * 2];
                _lbSetOf = new int[occluded * 2];
            }

            for (int j = 0; j < occluded; j++)
            {
                ref FrameLights.Entry l = ref _frameLights[_lbLight[j]];
                ref readonly var lightInfo = ref Client.Game.UO.Lights.GetLight(l.ID);
                int w = lightInfo.UV.Width, h = lightInfo.UV.Height;
                _lbQuads[j] = new Rectangle((int)(l.DrawX - w * 0.5f), (int)(l.DrawY - h * 0.5f), w, h);
            }

            int sets = LightBatch.CountSets(new System.ReadOnlySpan<Rectangle>(_lbQuads, 0, occluded), _lbSetOf);
            _lbPasses++;
            _lbDrawn += lightCount;
            _lbOccluded += occluded;
            _lbOccludedMax = System.Math.Max(_lbOccludedMax, occluded);
            _lbSets += sets;
            _lbSetsMax = System.Math.Max(_lbSetsMax, sets);
        }

        // once a second with light_occlusion on: this light pass renders the other path first and compares
        private bool LightBatchCompareDue()
        {
            if (!LightBatch.Check || !LightOcclusion.Enabled || Time.Ticks < _lbCompareAt)
            {
                return false;
            }

            _lbCompareAt = Time.Ticks + 1000;

            return true;
        }

        // the light target read back: the other path's (reference) first, then the active path's, compared pixel by pixel
        private void LightBatchReadback(RenderTarget2D target, bool reference)
        {
            if (target == null || target.IsDisposed)
            {
                return;
            }

            int n = target.Width * target.Height;

            if (reference)
            {
                if (_lbRef == null || _lbRef.Length < n)
                {
                    _lbRef = new Color[n];
                }

                target.GetData(_lbRef, 0, n);
                _lbRefLength = n;

                return;
            }

            if (_lbRefLength != n)
            {
                return;
            }

            if (_lbCur == null || _lbCur.Length < n)
            {
                _lbCur = new Color[n];
            }

            target.GetData(_lbCur, 0, n);
            int diff = LightBatch.Differ(new System.ReadOnlySpan<Color>(_lbRef, 0, n), new System.ReadOnlySpan<Color>(_lbCur, 0, n), out int maxChannel);
            _lbCompares++;
            _lbRefLength = 0;

            if (diff == 0)
            {
                return;
            }

            _lbMismatches++;
            _lbDiffPixels += diff;
            _lbMaxChannel = System.Math.Max(_lbMaxChannel, maxChannel);

            if (_lbMismatches <= 20)
            {
                Log.Warn($"[light-batch-check] light target {target.Width}x{target.Height}: {diff} pixels differ between the batched and " +
                         $"the per-light path (largest channel difference {maxChannel}), {_frameLights.Count} lights");
            }
        }

        // once per light pass with the checker on
        private void LightBatchSummary()
        {
            if (!LightBatch.Check || Time.Ticks < _lbSummaryAt)
            {
                return;
            }

            _lbSummaryAt = Time.Ticks + 5000;
            double passes = System.Math.Max(_lbPasses, 1);
            Log.Info($"[light-batch-check] light_batch {(LightBatch.Enabled ? "on" : "off")} (light_occlusion {(LightOcclusion.Enabled ? "on" : "off")}): " +
                     $"{_lbPasses} batched passes, lights {_lbDrawn / passes:F1} per pass, occluded {_lbOccluded / passes:F1} per pass (max {_lbOccludedMax}), " +
                     $"non-overlapping sets of the occluded {_lbSets / passes:F1} per pass (max {_lbSetsMax}); {_lbCompares} target compares, " +
                     $"{_lbDiffPixels} pixels differ (largest channel difference {_lbMaxChannel}); mismatches {_lbMismatches}");
        }
#endif
    }
}
