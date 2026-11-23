// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Game.Map;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // light_occlusion_bake (LightOcclusionBake): the atlas and its slots, the frame lights drawn from it this pass (by frame
        // light index), their atlas rectangles and light target positions, whether the pass draws them, the atlas's stencil
        // reference, the bake and draw blend states
        private RenderTarget2D _lkAtlas;
        private readonly LightBakeShelf _lkShelf = new LightBakeShelf(LightOcclusionBake.AtlasSize, LightOcclusionBake.AtlasSize);
        private bool[] _lkBaked = new bool[64];
        private Rectangle[] _lkSrc = new Rectangle[64];
        private Vector2[] _lkDst = new Vector2[64];
        private int _lkDraws, _lkStencilRef, _lkFrameBakes, _lkFrameHits;
        private bool _lkUse, _lkClear;
        private BlendState _lkBakeBlend, _lkDrawBlend;

        // before the light target is bound: every masked surface glow whose bake is current is flagged for the atlas draw; a new
        // or changed one is baked (up to LightOcclusionBake.BakesPerPass)
        private void LightBakePrepass(UltimaBatcher2D batcher, ref Matrix matrix, int lightCount, RenderTarget2D lightTarget)
        {
            _lkDraws = 0;
            _lkUse = false;
            _lkFrameBakes = _lkFrameHits = 0;

            if (lightTarget == null || !LightOcclusionBake.Active(LightOcclusionBake.Enabled, LightOcclusion.Enabled, LightOcclusionCache.Enabled,
                LightBatch.Enabled, LightAlphaFade.Enabled && LightOcclusion.Enabled))
            {
                return;
            }

            BeginLightOcclusionState(lightTarget.Width, lightTarget.Height);

            if (_lkBaked.Length < lightCount)
            {
                _lkBaked = new bool[Math.Max(lightCount, _lkBaked.Length * 2)];
            }

            Array.Clear(_lkBaked, 0, lightCount);
            GraphicsDevice device = batcher.GraphicsDevice;
            StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;
            float s = matrix.M11;
            bool bound = false, reset = false;
            _occBase = 0;

            for (int i = 0; i < lightCount; i++)
            {
                ref FrameLights.Entry l = ref _frameLights[i];
                ref readonly var lightInfo = ref Client.Game.UO.Lights.GetLight(l.ID);

                if (lightInfo.Texture == null || l.Source == null || !SurfaceGlow(l.Source, l.ID, staticData))
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
                    continue;
                }

                LightOccluderEntry e = _loTable.Find(l.Source, l.ID, _loPass);

                if (e == null || !Bakeable(n, ref matrix))
                {
                    ReleaseOccluders(n);

                    continue;
                }

                float tx = pos.X * s + matrix.M41, ty = pos.Y * s + matrix.M42;
                int ix = (int)MathF.Floor(tx), iy = (int)MathF.Floor(ty);
                ulong key = BakeKey(ref l, in lightInfo, hue, s, tx - ix, ty - iy, n);

                if (e.BakeGen == _lkShelf.Generation && e.BakeKey == key)
                {
                    _lkFrameHits++;
                }
                else
                {
                    int w = LightOcclusionBake.SlotSize(lightInfo.UV.Width, s), h = LightOcclusionBake.SlotSize(lightInfo.UV.Height, s);

                    if (_lkFrameBakes >= LightOcclusionBake.BakesPerPass || !AllocSlot(w, h, ref reset, out int bx, out int by))
                    {
                        ReleaseOccluders(n);

                        continue;
                    }

                    if (!bound)
                    {
                        BindAtlas(device);
                        bound = true;
                    }

                    if (_lkClear)
                    {
                        device.Clear(ClearOptions.Target | ClearOptions.Stencil, Vector4.Zero, 0f, 0);
                        _lkClear = false;
                        _lkStencilRef = 0;
                    }

                    BakeOne(batcher, ref matrix, n, in lightInfo, pos, hue, bx + 1 - ix, by + 1 - iy);
                    e.BakeKey = key;
                    e.BakeGen = _lkShelf.Generation;
                    e.BakeX = bx;
                    e.BakeY = by;
                    e.BakeW = w;
                    e.BakeH = h;
                    _lkFrameBakes++;
                }

                ReleaseOccluders(n);
                _lkBaked[i] = true;

                if (_lkDraws == _lkSrc.Length)
                {
                    Array.Resize(ref _lkSrc, _lkDraws * 2);
                    Array.Resize(ref _lkDst, _lkDraws * 2);
                }

                _lkSrc[_lkDraws] = new Rectangle(e.BakeX, e.BakeY, e.BakeW, e.BakeH);
                _lkDst[_lkDraws++] = new Vector2(ix - 1, iy - 1);
            }

            if (bound)
            {
                device.SetRenderTarget(null);
            }

            _lkUse = _lkDraws > 0;
            _lkBakes += _lkFrameBakes;
            _lkHits += _lkFrameHits;
        }

        // a slot for a w x h bake; a full atlas starts over once per pass (every slot dropped, the atlas cleared before the
        // next bake)
        private bool AllocSlot(int w, int h, ref bool reset, out int x, out int y)
        {
            if (_lkShelf.TryAlloc(w, h, out x, out y))
            {
                return true;
            }

            if (reset)
            {
                return false;
            }

            reset = true;
            _lkShelf.Reset();
            _lkClear = true;
            _lkResets++;

            return _lkShelf.TryAlloc(w, h, out x, out y);
        }

        private void BindAtlas(GraphicsDevice device)
        {
            if (_lkAtlas == null || _lkAtlas.IsDisposed)
            {
                int size = LightOcclusionBake.AtlasSize;
                _lkAtlas = new RenderTarget2D(device, size, size, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8, 0,
                    RenderTargetUsage.PreserveContents);
                _lkShelf.Reset();
                _lkClear = true;
                Log.Info($"[light-occlusion-bake] atlas {size}x{size} Color + Depth24Stencil8 ({size * size * 8 / (1024 * 1024)} MB)");
                // the bake: the light's contribution as the light target's Additive blend adds it (colour x alpha), alpha kept
                // non-zero where it adds anything; the draw: that contribution added as it is
                _lkBakeBlend = new BlendState
                {
                    ColorSourceBlend = Blend.SourceAlpha,
                    ColorDestinationBlend = Blend.One,
                    AlphaSourceBlend = Blend.One,
                    AlphaDestinationBlend = Blend.One
                };
                _lkDrawBlend = new BlendState
                {
                    ColorSourceBlend = Blend.One,
                    ColorDestinationBlend = Blend.One,
                    AlphaSourceBlend = Blend.One,
                    AlphaDestinationBlend = Blend.One
                };
            }

            device.SetRenderTarget(_lkAtlas);
        }

        // one masked light into its slot: the light pass's matrix moved by whole pixels (shiftX, shiftY), so the light and its
        // occluders keep their sub-pixel phase; the occluders into the stencil, the light where the stencil does not hold them
        private void BakeOne(UltimaBatcher2D batcher, ref Matrix matrix, int n, in SpriteInfo lightInfo, Vector2 pos, Vector3 hue, int shiftX, int shiftY)
        {
            Matrix bm = matrix;
            bm.M41 += shiftX;
            bm.M42 += shiftY;

            if (++_lkStencilRef > 255)
            {
                batcher.GraphicsDevice.Clear(ClearOptions.Stencil, Vector4.Zero, 0f, 0);
                _lkStencilRef = 1;
            }

            EnsureOcclusionStates();
            batcher.Begin(null, bm);
            batcher.SetStencil(_occWrite[_lkStencilRef]);
            batcher.SetBlendState(_noColorBlend);

            for (int i = _occBase, end = _occBase + n; i < end; i++)
            {
                ref LightOccluder o = ref _occluders[i];
                // no circle of transparency reaches it (Bakeable) and no dither (LightOcclusionBake.Active): a plain mask
                batcher.Draw(o.Texture, new Vector2(o.DrawX, o.DrawY), o.UV, Vector3.UnitZ, 0f);
            }

            batcher.SetStencil(_occTest[_lkStencilRef]);
            batcher.SetBlendState(_lkBakeBlend);
            batcher.Draw(lightInfo.Texture, pos, lightInfo.UV, hue, 0f);
            batcher.SetStencil(null);
            batcher.SetBlendState(null);
            batcher.End();
        }

        // no occluder the circle of transparency reaches: the shader's circle is in light target pixels round its centre
        private bool Bakeable(int n, ref Matrix matrix)
        {
            if (!_loCot)
            {
                return true;
            }

            float s = matrix.M11, r = _lafRadius + 1f;

            for (int i = _occBase, end = _occBase + n; i < end; i++)
            {
                ref LightOccluder o = ref _occluders[i];

                if (!o.Cot)
                {
                    continue;
                }

                float x0 = o.DrawX * s + matrix.M41, y0 = o.DrawY * s + matrix.M42;
                float dx = _lafCx - Math.Clamp(_lafCx, x0, x0 + o.UV.Width * s);
                float dy = _lafCy - Math.Clamp(_lafCy, y0, y0 + o.UV.Height * s);

                if (dx * dx + dy * dy <= r * r)
                {
                    return false;
                }
            }

            return true;
        }

        // what the bake depends on: the light (id, colour, hue, its art and texture), the scale and sub-pixel phase, and each
        // occluder's art, texture, source rectangle and offset from the light
        private ulong BakeKey(ref FrameLights.Entry l, in SpriteInfo lightInfo, Vector3 hue, float scale, float fx, float fy, int n)
        {
            ulong h = 0xCBF29CE484222325UL;
            h = LightOcclusionBake.Mix(h, l.ID | (ulong)l.Color << 8 | (l.IsHue ? 1UL << 24 : 0) | (ulong)(uint)n << 32);
            h = LightOcclusionBake.Mix(h, BitConverter.SingleToUInt32Bits(hue.X) | (ulong)BitConverter.SingleToUInt32Bits(hue.Y) << 32);
            h = LightOcclusionBake.Mix(h, BitConverter.SingleToUInt32Bits(hue.Z) | (ulong)BitConverter.SingleToUInt32Bits(scale) << 32);
            h = LightOcclusionBake.Mix(h, BitConverter.SingleToUInt32Bits(fx) | (ulong)BitConverter.SingleToUInt32Bits(fy) << 32);
            h = LightOcclusionBake.Mix(h, PackRect(lightInfo.UV));
            h = LightOcclusionBake.Mix(h, (uint)RuntimeHelpers.GetHashCode(lightInfo.Texture));

            for (int i = _occBase, end = _occBase + n; i < end; i++)
            {
                ref LightOccluder o = ref _occluders[i];
                h = LightOcclusionBake.Mix(h, o.Art | (ulong)(uint)RuntimeHelpers.GetHashCode(o.Texture) << 32);
                h = LightOcclusionBake.Mix(h, PackRect(o.UV));
                h = LightOcclusionBake.Mix(h, (uint)(o.DrawX - l.DrawX) | (ulong)(uint)(o.DrawY - l.DrawY) << 32);
            }

            return h;
        }

        private static ulong PackRect(Rectangle r) => (ulong)(ushort)r.X | (ulong)(ushort)r.Y << 16 | (ulong)(ushort)r.Width << 32 | (ulong)(ushort)r.Height << 48;

        // light_batch's pass: the baked lights from the atlas in one batch (target pixels, One/One, point sampled), then the
        // light batch again
        private void DrawBakedLights(UltimaBatcher2D batcher, ref Matrix matrix)
        {
            batcher.End();
            batcher.Begin(null, Matrix.Identity);
            batcher.SetBlendState(_lkDrawBlend);
            Vector3 hue = Vector3.UnitZ;

            for (int k = 0; k < _lkDraws; k++)
            {
                batcher.Draw(_lkAtlas, _lkDst[k], _lkSrc[k], hue, 0f);
            }

            batcher.End();
            batcher.Begin(null, matrix);
            BeginLightOcclusionBatch(batcher);
            batcher.SetBlendState(BlendState.Additive);
        }

        private long _lkBakes, _lkHits, _lkResets;
#if CUO_DEVTOOLS
        private long _lkCompares, _lkDiffPixels, _lkMismatches;
        private int _lkMaxChannel;
        private uint _lkCompareAt, _lkSummaryAt;
        private Color[] _lkRef, _lkCur;

        // CUO_LIGHT_OCC_BAKE_CHECK: once a second while lights are baked, the pass is drawn without them first (reference)
        private bool LightBakeCompareDue()
        {
            if (!LightOcclusionBake.Check || !_lkUse || Time.Ticks < _lkCompareAt)
            {
                return false;
            }

            _lkCompareAt = Time.Ticks + 1000;

            return true;
        }

        // the light target read back: the reference (bakes off) first, then the baked pass's, compared pixel by pixel
        private void LightBakeReadback(RenderTarget2D target, bool reference)
        {
            int n = target.Width * target.Height;
            ref Color[] buf = ref reference ? ref _lkRef : ref _lkCur;

            if (buf == null || buf.Length < n)
            {
                buf = new Color[n];
            }

            target.GetData(buf, 0, n);

            if (reference)
            {
                return;
            }

            int diff = LightBatch.Differ(new ReadOnlySpan<Color>(_lkRef, 0, n), new ReadOnlySpan<Color>(_lkCur, 0, n), out int maxChannel);
            _lkCompares++;

            if (diff == 0)
            {
                return;
            }

            _lkMismatches++;
            _lkDiffPixels += diff;
            _lkMaxChannel = Math.Max(_lkMaxChannel, maxChannel);

            if (_lkMismatches <= 20)
            {
                Log.Warn($"[light-occ-bake-check] light target {target.Width}x{target.Height}: {diff} pixels differ between the baked and the " +
                         $"stencil pass (largest channel difference {maxChannel}), {_lkDraws} lights from the atlas");
            }
        }

        private void LightBakeSummary()
        {
            if (!LightOcclusionBake.Check || Time.Ticks < _lkSummaryAt)
            {
                return;
            }

            _lkSummaryAt = Time.Ticks + 5000;
            Log.Info($"[light-occ-bake-check] light_occlusion_bake {(LightOcclusionBake.Enabled ? "on" : "off")}: bakes {_lkBakes}, atlas draws " +
                     $"{_lkHits + _lkBakes} ({_lkHits} reused), atlas resets {_lkResets}, last pass {_lkDraws} lights from the atlas " +
                     $"({_lkFrameBakes} baked); {_lkCompares} target compares, {_lkDiffPixels} pixels differ (largest channel difference " +
                     $"{_lkMaxChannel}); mismatches {_lkMismatches}");
        }
#endif

        private void DisposeLightBake()
        {
            _lkAtlas?.Dispose();
            _lkAtlas = null;
            _lkBakeBlend?.Dispose();
            _lkDrawBlend?.Dispose();
            _lkBakeBlend = _lkDrawBlend = null;
        }
    }
}
