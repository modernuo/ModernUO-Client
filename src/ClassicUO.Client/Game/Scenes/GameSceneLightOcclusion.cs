// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // light_occlusion: what one light's occluders are tested against: its light point, its quad, its source's height
        // (LightOcclusion.SourceTop) and a wall source's facing (LightOcclusion.Facing)
        private struct LightTarget
        {
            public int Px, Py, Left, Top, Right, Bottom, SrcTop;
            public byte Facing;
        }

        private struct LightOccluder
        {
            public Texture2D Texture;
            public Rectangle UV;
            public int DrawX, DrawY;
            // drawn with the circle of transparency (IsometricWorld.fx discards the circle, as in the world draw)
            public bool Cot;
            // light_alpha_fade: its drawn alpha (the dithered mask's coverage)
            public byte Alpha;
            // light_occlusion_bake: the art id drawn (the bake key)
            public uint Art;
        }

        // light_occlusion's occluder sums (LightOcclusion.Mix): every occluder found, and those at least
        // LightOcclusion.MinOccluderAlpha opaque (CUO_LIGHT_ALPHA_FADE_CHECK: the occluders the binary cut takes)
        private struct OccluderSums
        {
            public uint Hash, OpaqueHash;
            public int Opaque;
        }

        // light_occlusion: only surface glows get occluders and masks (LightOcclusion.Masks)
        private bool _lightOcclusion;
        // light_alpha_fade: surface glows fade with their emitter (_alphaFade); masks dithered to alpha (_ditherMasks, with
        // light_occlusion); the least alpha that masks (LightAlphaFade.MinMaskAlpha)
        private bool _alphaFade, _ditherMasks;
        private byte _minMaskAlpha = LightOcclusion.MinOccluderAlpha;
        // light_alpha_fade: the shader circle's centre (the light target's) and radius in target pixels, 0 without one
        private float _lafCx, _lafCy, _lafRadius;
        // light_occlusion: the circle of transparency the masks share with the world draw (shader mode; the gradient mode is
        // in AlphaHue) and the Z its TransparentTest takes
        private bool _loCot;
        private int _loCotZ;
        // light_occlusion: the item sprites the world draw makes (ItemSpriteExtract select), per scene
        private readonly ItemSpriteExtract _occItems = new ItemSpriteExtract();
        private int _stencilRef;
        // light_occlusion: one light's occluders, grown to the high-water mark and reused
        private LightOccluder[] _occluders = new LightOccluder[64];
        // light_batch: where the next light's occluders go in _occluders (0 on the per-light path; the batched path keeps every
        // light's occluders for the frame, one after the other)
        private int _occBase;
        // one write and one test state per stencil reference (1..255), made on first use and kept with the scene
        private DepthStencilState[] _occWrite, _occTest;
        private BlendState _noColorBlend;
        // FrameStats: the light pass's occluder search and masked-draw time (Stopwatch ticks), lights drawn masked and their
        // occluder sprites
        private long _loSearchTicks, _loMaskTicks;
        private int _loFrameMasked, _loFrameOccluders, _loFlushes0, _loDrawCalls0;
        // light_occlusion_cache: the table, on this pass, the pass count, the drawn chunks' signature this pass, the entry the
        // search running records into (null: none), this pass's hits and rebuilds
        private readonly LightOccluderTable _loTable = new LightOccluderTable();
        private bool _loCacheOn;
        private uint _loPass, _loVisibleSig;
        private LightOccluderEntry _loRecord;
        private int _loFrameHits, _loFrameMisses;
        private long _loCacheHits, _loCacheMisses;
        private LightSearchMeasure _lightSearchMeasure;
        private bool _measureSearch, _measureCandidates;
        private uint _measureLightFrame;

        private void BeginLightSearchMeasure(RenderTarget2D target)
        {
            _measureSearch = FrameStats.On;
            if (!_measureSearch) return;
            _lightSearchMeasure ??= new LightSearchMeasure();
            Profile p = ProfileManager.CurrentProfile;
            var inputs = new LightSearchMeasure.ProfileInputs(p?.HighlightGameObjects ?? false, p?.NoColorObjectsOutOfRange ?? false,
                _world.Player != null && _world.Player.IsDead && (p?.EnableBlackWhiteEffect ?? false), p?.TreeToStumps ?? false,
                p?.FieldsType ?? 0, _world.RangeSize.X, _world.RangeSize.Y, _world.ClientViewRange,
                SelectedObject.Object, SelectedObject.SelectedContainer);
            var key = new LightSearchMeasure.Scene(Camera.ViewTransformMatrix, Camera.Bounds, _viewMinPixel, _viewMaxPixel,
                _offset, Camera.Zoom, _maxZ, _noDrawRoofs, GPU_WALK_MAX_Z, p?.UseCircleOfTransparency ?? false,
                p?.CircleOfTransparencyType ?? 0, p?.CircleOfTransparencyRadius ?? 0, _world.Player?.Z ?? int.MinValue,
                LightAlphaFade.Enabled, LightOcclusion.Enabled, UseAltLights, p?.UseColoredLights ?? false,
                _lightRange, _frameLights.Limit, LightEmitters.Epoch, target?.Width ?? 0, target?.Height ?? 0, inputs);
            _lightSearchMeasure.Begin(in key, OccluderChunks, _measureLightFrame + 1 == _lightsFrame);
            _measureLightFrame = _lightsFrame;
        }

        // the light pass starts: the light target's stencil is cleared when the switch is on
        private void BeginLightOcclusion(GraphicsDevice device)
        {
            BeginLightOcclusionState(device.Viewport.Width, device.Viewport.Height);

            if (_lightOcclusion)
            {
                device.Clear(ClearOptions.Stencil, Vector4.Zero, 0f, 0);
            }
        }

        // the light pass's occlusion state for a light target of width x height (light_occlusion_bake's prepass runs it before
        // the light target is bound)
        private void BeginLightOcclusionState(int width, int height)
        {
            _lightOcclusion = LightOcclusion.Enabled;
            _stencilRef = 0;
            _loSearchTicks = _loMaskTicks = 0;
            _loFrameMasked = _loFrameOccluders = 0;
            _loFlushes0 = UltimaBatcher2D.FrameFlushes;
            _loDrawCalls0 = UltimaBatcher2D.FrameDrawCalls;
            _loFrameHits = _loFrameMisses = 0;
            BeginLightOcclusionCache();
#if CUO_DEVTOOLS
            _loPassLights = _loPassMasked = _loPassRadial = 0;
#endif
            Profile profile = ProfileManager.CurrentProfile;
            _loCot = profile != null && profile.UseCircleOfTransparency && profile.CircleOfTransparencyType != 1 && _world.Player != null;
            _loCotZ = _world.Player != null ? _world.Player.Z + 5 : 0;
            _alphaFade = LightAlphaFade.Enabled;
            _ditherMasks = _alphaFade && _lightOcclusion;
            _minMaskAlpha = LightAlphaFade.MinMaskAlpha(_ditherMasks);
            _lafCx = width * 0.5f;
            _lafCy = height * 0.5f;
            _lafRadius = _loCot ? profile.CircleOfTransparencyRadius / Camera.Zoom : 0f;
        }

        // the light pass has ended: its FrameStats counters
        private void EndLightOcclusion()
        {
            if (!FrameStats.On)
            {
                return;
            }

            FrameStats.Set(FrameCounter.LightsMasked, _loFrameMasked);
            FrameStats.Set(FrameCounter.LightOccluders, _loFrameOccluders);
            FrameStats.Set(FrameCounter.LightFlushes, UltimaBatcher2D.FrameFlushes - _loFlushes0);
            FrameStats.Set(FrameCounter.LightDrawCalls, UltimaBatcher2D.FrameDrawCalls - _loDrawCalls0);
            FrameStats.Set(FrameCounter.LightSearchUs, (int)(_loSearchTicks * 1000000 / System.Diagnostics.Stopwatch.Frequency));
            FrameStats.Set(FrameCounter.LightMaskUs, (int)(_loMaskTicks * 1000000 / System.Diagnostics.Stopwatch.Frequency));
            FrameStats.Set(FrameCounter.LoCacheHits, _loFrameHits);
            FrameStats.Set(FrameCounter.LoCacheMisses, _loFrameMisses);
            FrameStats.Set(FrameCounter.LoBakes, _lkFrameBakes);
            FrameStats.Set(FrameCounter.LoBakeDraws, _lkDraws);
            FrameStats.Set(FrameCounter.LoRebakes, _lkFrameRebakes);
            FrameStats.Set(FrameCounter.LoRebakesPerSecond, _lkRebakeRate.PerSecond);
            if (_measureSearch)
            {
                FrameStats.Set(FrameCounter.LightSearchPasses, 1);
                FrameStats.Set(FrameCounter.LightCandidateReads, _lightSearchMeasure.Reads);
                FrameStats.Set(FrameCounter.LightUniqueCandidates, _lightSearchMeasure.Unique);
                FrameStats.Set(FrameCounter.LightResultQueries, _lightSearchMeasure.Queries);
                FrameStats.Set(FrameCounter.LightResultHits, _lightSearchMeasure.Hits);
                FrameStats.Set(FrameCounter.LightMeasureOverflow, _lightSearchMeasure.Overflow);
            }
        }

        // light_occlusion_cache: on with light_occlusion; the drawn chunks' signature (an entry built under another set is
        // rebuilt), idle entries dropped every 256 passes; off: the table released once
        private void BeginLightOcclusionCache()
        {
            _loCacheOn = _lightOcclusion && LightOcclusionCache.Enabled;

            if (!_loCacheOn)
            {
                if (_loTable.Count > 0)
                {
                    _loTable.Reset();
                }

                return;
            }

            _loPass++;
            // the chunk set the occluder search walks (the full bands' chunks)
            List<Chunk> searched = OccluderChunks;
            uint sig = (uint)searched.Count * 2654435761u;

            for (int i = 0; i < searched.Count; i++)
            {
                Chunk c = searched[i];
                sig = (sig ^ (uint)(c.X | c.Y << 16)) * 16777619u;
            }

            _loVisibleSig = sig;

            if ((_loPass & 255) == 0)
            {
                _loTable.Sweep(_loPass);
            }
        }

        // the light pass's batcher has begun: the masks' circle of transparency as the world draw sets it (DrawWorld; Gpu:
        // GameSceneGpuWorld), centred on the target like the world's (the light target has the world target's size)
        private void BeginLightOcclusionBatch(UltimaBatcher2D batcher)
        {
            if (_lightOcclusion && _loCot)
            {
                batcher.SetCircleOfTransparencyRadius((float)ProfileManager.CurrentProfile.CircleOfTransparencyRadius / Camera.Zoom);
            }
        }

        private void EndLightOcclusionBatch(UltimaBatcher2D batcher)
        {
            if (_lightOcclusion && _loCot)
            {
                batcher.SetCircleOfTransparencyRadius(0f);
            }
        }

        // one light of the per-light path. light_occlusion on: the drawn sprites of the occluders covering its source's light
        // point go into the stencil (no colour) with a fresh reference, and the light is drawn where the stencil does not hold it
        private void DrawLight(UltimaBatcher2D batcher, ref Matrix matrix, ref FrameLights.Entry l, in SpriteInfo lightInfo, Vector3 hue, ref bool stencilSet)
        {
            Vector2 pos = new Vector2(l.DrawX - lightInfo.UV.Width * 0.5f, l.DrawY - lightInfo.UV.Height * 0.5f);

            if (!LightShare(ref l, ref matrix, ref hue))
            {
                return;
            }

            int n = CollectLightOccluders(ref l, pos, in lightInfo);

            if (!_lightOcclusion || n == 0)
            {
                ReleaseOccluders(n);

                if (stencilSet)
                {
                    batcher.SetStencil(null);
                    stencilSet = false;
                }

                batcher.Draw(lightInfo.Texture, pos, lightInfo.UV, hue, 0f);

                return;
            }

            DrawMasked(batcher, ref matrix, ref l, _occBase, n, in lightInfo, pos, hue);
            ReleaseOccluders(n);
            stencilSet = true;
        }

        // the share of light l drawn, as its hue alpha: light_range's range and budget fade (l.Share) times light_alpha_fade's
        // emitter fade (a surface glow at its emitter's drawn alpha). False: a hidden emitter or a light faded out gives none
        private bool LightShare(ref FrameLights.Entry l, ref Matrix matrix, ref Vector3 hue)
        {
            float share = l.Share;

            if ((_alphaFade || LightAlphaFade.Check) && l.Source != null)
            {
                float fade = EmitterFade(ref l, ref matrix, out bool surface);
#if CUO_DEVTOOLS
                if (LightAlphaFade.Check)
                {
                    CheckFade(ref l, ref matrix, fade, surface);
                }
#endif

                share *= LightAlphaFade.Share(_alphaFade, fade);
            }

            if (share < 1f)
            {
                if (share <= 0f)
                {
                    return false;
                }

                hue.Z = LightAlphaFade.HueAlpha(share);
            }

            return true;
        }

        // light l's occluders (light_occlusion, or its checker) into _occluders from _occBase: their count. A radial light
        // gets none (no search, no stencil work); the checker still collects its occluders to count the radial lights with
        // occluders left unmasked
        private int CollectLightOccluders(ref FrameLights.Entry l, Vector2 pos, in SpriteInfo lightInfo)
        {
            if (!(_lightOcclusion || LightOcclusion.Check) || l.Source == null)
            {
                return 0;
            }

            if (LightOcclusion.Masks(SurfaceGlow(l.Source, l.ID, Client.Game.UO.FileManager.TileData.StaticData)))
            {
                long t0 = FrameStats.Stamp();
                int found = CollectLightOccludersCore(ref l, pos, in lightInfo);
                _loSearchTicks += FrameStats.AddSince(FramePhase.LightSearch, t0) - t0;

                return found;
            }

#if CUO_DEVTOOLS
            if (LightOcclusion.Check)
            {
                int found = CollectLightOccludersCore(ref l, pos, in lightInfo);

                if (found > 0)
                {
                    _loRadialUnmasked++;
                    _loPassRadial++;
                }

                ReleaseOccluders(found);
            }
#endif
            return 0;
        }

        private int CollectLightOccludersCore(ref FrameLights.Entry l, Vector2 pos, in SpriteInfo lightInfo)
        {
#if CUO_DEVTOOLS
            // CUO_LIGHT_ALPHA_FADE_CHECK: the binary cut's occluders first, then the dithered set must hold exactly them
            // among its occluders at least half opaque
            bool maskCheck = LightAlphaFade.Check && _ditherMasks;
            OccluderSums binary = maskCheck ? BinaryMaskSums(ref l, (int)pos.X, (int)pos.Y, lightInfo.UV.Width, lightInfo.UV.Height) : default;
            int n = CollectOccluders(ref l, (int)pos.X, (int)pos.Y, lightInfo.UV.Width, lightInfo.UV.Height, LightOcclusion.Check, out _, out OccluderSums sums);

            if (maskCheck)
            {
                CompareMasks(ref l, in binary, in sums);
            }

            return n;
#else
            return CollectOccluders(ref l, (int)pos.X, (int)pos.Y, lightInfo.UV.Width, lightInfo.UV.Height, LightOcclusion.Check, out _);
#endif
        }

        // an occluded light: its n occluders from _occluders[first] into the stencil with a fresh reference, then the light where
        // the stencil does not hold it. Leaves the stencil test set
        private void DrawMasked(UltimaBatcher2D batcher, ref Matrix matrix, ref FrameLights.Entry l, int first, int n, in SpriteInfo lightInfo, Vector2 pos, Vector3 hue)
        {
#if CUO_DEVTOOLS
            // CUO_LIGHT_OCCLUSION_CHECK: light_occlusion draws no radial light masked
            if (LightOcclusion.Check && !SurfaceGlow(l.Source, l.ID, Client.Game.UO.FileManager.TileData.StaticData))
            {
                _loMismatches++;

                if (_loMismatches <= 20)
                {
                    Log.Warn($"[light-occlusion-check] radial light {l.ID} from {l.SrcX},{l.SrcY},{l.SrcZ} " +
                             $"(0x{l.Source.Graphic:X4}) drawn with {n} occluder sprites");
                }
            }
#endif
            long t0 = FrameStats.Stamp();

            if (++_stencilRef > 255)
            {
                // every reference used: clear the stencil and start over
                batcher.End();
                batcher.GraphicsDevice.Clear(ClearOptions.Stencil, Vector4.Zero, 0f, 0);
                batcher.Begin(null, matrix);
                BeginLightOcclusionBatch(batcher);
                _stencilRef = 1;
            }

            EnsureOcclusionStates();
            batcher.SetStencil(_occWrite[_stencilRef]);
            batcher.SetBlendState(_noColorBlend);

            for (int i = first, end = first + n; i < end; i++)
            {
                ref LightOccluder o = ref _occluders[i];
                // an alpha above 1 marks the circle of transparency (ShaderHueTranslator.GetHueVector circletrans);
                // light_alpha_fade: the mask dithered to the occluder's drawn alpha (IsometricWorld.fx DITHER_MASK)
                Vector3 maskHue = _ditherMasks
                    ? new Vector3(0f, LightAlphaFade.DitherMaskMode, o.Alpha / 255f + (o.Cot ? 1f : 0f))
                    : o.Cot ? new Vector3(0f, 0f, 2f) : Vector3.UnitZ;
                batcher.Draw(o.Texture, new Vector2(o.DrawX, o.DrawY), o.UV, maskHue, 0f);
            }

            batcher.SetStencil(_occTest[_stencilRef]);
            batcher.SetBlendState(BlendState.Additive);
            batcher.Draw(lightInfo.Texture, pos, lightInfo.UV, hue, 0f);
            _loMaskTicks += FrameStats.Stamp() - t0;
            _loFrameMasked++;
            _loFrameOccluders += n;
#if CUO_DEVTOOLS
            _loOccludedLights++;
            _loPassMasked++;
            _loOccluderSprites += n;
#endif
        }

        // no texture kept past the light that used it (review P2 #7): the n occluders from _occBase
        private void ReleaseOccluders(int n)
        {
            for (int i = _occBase, end = _occBase + n; i < end; i++)
            {
                _occluders[i].Texture = null;
            }
        }

        private void EnsureOcclusionStates()
        {
            if (_occWrite != null)
            {
                return;
            }

            _occWrite = new DepthStencilState[256];
            _occTest = new DepthStencilState[256];

            for (int r = 1; r < 256; r++)
            {
                _occWrite[r] = new DepthStencilState
                {
                    DepthBufferEnable = false,
                    StencilEnable = true,
                    StencilFunction = CompareFunction.Always,
                    StencilPass = StencilOperation.Replace,
                    ReferenceStencil = r,
                    StencilMask = -1,
                    StencilWriteMask = -1
                };
                _occTest[r] = new DepthStencilState
                {
                    DepthBufferEnable = false,
                    StencilEnable = true,
                    StencilFunction = CompareFunction.NotEqual,
                    StencilPass = StencilOperation.Keep,
                    StencilFail = StencilOperation.Keep,
                    ReferenceStencil = r,
                    StencilMask = -1,
                    StencilWriteMask = 0
                };
            }

            _noColorBlend = new BlendState { ColorWriteChannels = ColorWriteChannels.None };
        }

        // the occluders of light l (its quad at left, top, w x h), whose drawn art covers its light point
        // (LightOcclusion.LightPoint) or, for a wall source, wall pieces facing away from it (LightOcclusion.FacesAway), into
        // _occluders. The search walks the drawn chunks (only what the world draws can hide a glow) whose recorded art and Z
        // can reach the point, or a wall source's quad (LightOcclusion.ChunkReaches); with check, compared with a scan of every
        // tile of those chunks
        private int CollectOccluders(ref FrameLights.Entry l, int left, int top, int w, int h, bool check, out uint hash) =>
            CollectOccluders(ref l, left, top, w, h, check, out hash, out _);

        private int CollectOccluders(ref FrameLights.Entry l, int left, int top, int w, int h, bool check, out uint hash, out OccluderSums sums)
        {
            ViewMargin.Anchor(l.SrcX, l.SrcY, l.SrcZ, _offset.X, _offset.Y, out int sax, out int say);
            StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;
            LightTarget t = new LightTarget
            {
                Px = l.DrawX, Py = l.DrawY, Left = left, Top = top, Right = left + w, Bottom = top + h, SrcTop = LightOcclusion.MobileTop
            };
            GameObject src = l.Source;

            // a static or item emitter: the centre of its visible art; a mobile's light: the glow's centre
            if (!(src is Mobile))
            {
                ushort g = src is Item srcItem ? srcItem.DisplayedGraphic : src.Graphic;
                ref ArtEntry srcEntry = ref Client.Game.UO.FileManager.Arts.GetEntry(g + 0x4000);
                uint srcDrawn = (uint)(g + srcEntry.AnimOffset);
                SpriteInfo srcArt = Client.Game.UO.Arts.GetArt(srcDrawn);
                Rectangle b = Client.Game.UO.Arts.GetRealArtBounds(srcDrawn);

                if (srcArt.Texture != null && b.Width > 0 && b.Height > 0)
                {
                    LightOcclusion.LightPoint(sax, say, srcArt.UV.Width, srcArt.UV.Height, b.X, b.Y, b.Width, b.Height, out t.Px, out t.Py);

                    if ((uint)src.Graphic < (uint)staticData.Length && staticData[src.Graphic].IsWall)
                    {
                        t.Facing = LightOcclusion.Facing(srcArt.UV.Width, b.X, b.Width);
                    }
                }

                t.SrcTop = LightOcclusion.SourceTop((uint)src.Graphic < (uint)staticData.Length ? staticData[src.Graphic].Height : 0, say, t.Py);
            }

            // a wall source's occluders may be anywhere under its quad; any other's must cover its light point
            int rl, rt, rr, rb;

            if (t.Facing != LightOcclusion.FacingNone)
            {
                rl = t.Left;
                rt = t.Top;
                rr = t.Right;
                rb = t.Bottom;
            }
            else
            {
                rl = t.Px;
                rt = t.Py;
                rr = t.Px + 1;
                rb = t.Py + 1;
            }

            int n = _occBase;
            if (_measureSearch)
            {
                var target = new LightSearchMeasure.Target(l.SrcX, l.SrcY, l.SrcZ, src is Item si ? si.DisplayedGraphic : src.Graphic,
                    l.DrawX, l.DrawY, t.Px, t.Py, t.Left, t.Top, t.Right, t.Bottom, t.SrcTop, t.Facing);
                _lightSearchMeasure.Query(src, l.ID, in target);
            }
            sums = default;
            LightOccluderEntry entry = null;
            bool cached = false;

            if (_loCacheOn)
            {
                // light_occlusion_cache: the key relative to the source's anchor (camera scroll keeps it)
                ushort srcGraphic = src is Item keyItem ? keyItem.DisplayedGraphic : src.Graphic;
                entry = _loTable.Find(src, l.ID, _loPass);
                cached = entry != null && entry.KeyMatches(l.SrcX, l.SrcY, l.SrcZ, srcGraphic, t.Px - sax, t.Py - say, t.Left - sax,
                    t.Top - say, t.Right - sax, t.Bottom - say, t.SrcTop, t.Facing, _loVisibleSig) && entry.ChunksValid();

                if (cached)
                {
                    _loFrameHits++;
                    _loCacheHits++;
                }
                else
                {
                    entry ??= _loTable.Take(src, l.ID, _loPass);
                    entry.SetKey(l.SrcX, l.SrcY, l.SrcZ, srcGraphic, t.Px - sax, t.Py - say, t.Left - sax, t.Top - say, t.Right - sax,
                        t.Bottom - say, t.SrcTop, t.Facing, _loVisibleSig);
                    _loFrameMisses++;
                    _loCacheMisses++;
                }
            }

            _measureCandidates = _measureSearch;
            if (cached)
            {
                FrameStats.Add(FrameCounter.LoCandidates, entry.CandidateCount);
                LightSearchMeasure measure = _measureCandidates ? _lightSearchMeasure : null;
                for (int i = 0; i < entry.CandidateCount; i++)
                {
                    if (measure != null) measure.Read(entry.Candidates[i]);
                    CollectCandidate(ref l, entry.Candidates[i], staticData, in t, ref n, ref sums);
                }
            }
            else
            {
                _loRecord = entry;
                CollectVisible(ref l, staticData, in t, rl, rt, rr, rb, ref n, ref sums);
                _loRecord = null;
            }
            _measureCandidates = false;

            n -= _occBase;
            hash = sums.Hash;
#if CUO_DEVTOOLS
            if (cached && LightOcclusionCache.Check)
            {
                CheckCachedOccluders(ref l, in t, rl, rt, rr, rb, n, in sums, staticData);
            }

            if (check)
            {
                CheckOccluders(ref l, in t, n, hash, staticData);
            }
#endif

            return n;
        }

        // the full search: the drawn chunks that can reach the search rectangle (light_occlusion_cache: recorded into
        // _loRecord with their versions)
        private void CollectVisible(ref FrameLights.Entry l, StaticTiles[] staticData, in LightTarget t, int rl, int rt, int rr, int rb,
            ref int n, ref OccluderSums sums)
        {
            List<Chunk> chunks = OccluderChunks;
            bool stats = FrameStats.On;
            int visited = 0;

            for (int c = 0; c < chunks.Count; c++)
            {
                Chunk chunk = chunks[c];
                if (stats) visited++;

                if (chunk.IsDestroyed || !LightOcclusion.ChunkReaches(chunk.X, chunk.Y, chunk.StaticOverLeft, chunk.StaticOverRight,
                    chunk.StaticOverUp, chunk.OccZLo, chunk.OccZHi, _offset.X, _offset.Y, rl, rt, rr, rb))
                {
                    continue;
                }

                _loRecord?.AddChunk(chunk);
                CollectChunk(ref l, chunk, staticData, in t, rl, rr, false, ref n, ref sums);
            }

            if (stats) FrameStats.Add(FrameCounter.LoChunks, visited);
        }

        // light_occlusion_cache: an object able to hide light l whatever its drawn state (LightOcclusion.Hides's terms that
        // depend only on its position, graphic and the source), so later frames test only it
        private static bool CacheCandidate(ref FrameLights.Entry l, in LightTarget t, GameObject o, StaticTiles[] staticData)
        {
            if ((uint)o.Graphic >= (uint)staticData.Length)
            {
                return false;
            }

            ref StaticTiles data = ref staticData[o.Graphic];

            return LightOcclusion.Hides(true, ReferenceEquals(o, l.Source), o.X - l.SrcX, o.Y - l.SrcY, o.Z - l.SrcZ, t.SrcTop, ref data, true,
                t.Facing != LightOcclusion.FacingNone && data.IsWall);
        }

        // light_occlusion_cache: one cached candidate with the full occluder test, as CollectTile tests it
        private void CollectCandidate(ref FrameLights.Entry l, GameObject o, StaticTiles[] staticData, in LightTarget t, ref int n, ref OccluderSums sums)
        {
            if (o is Item item)
            {
                if (!item.IsMulti && !item.IsCorpse && ItemOccluder(ref l, item, staticData, in t, ref n))
                {
                    Sum(ref sums, LightOcclusion.Mix(item.X, item.Y, item.Z, item.Graphic, true), item.AlphaHue);
                }
            }
            else if (o is TileListObject to && StaticOccluder(ref l, to, staticData, in t, ref n, out byte alpha))
            {
                Sum(ref sums, LightOcclusion.Mix(to.X, to.Y, to.Z, to.Graphic, false), alpha);
            }
        }

        // the chunk's tiles whose sprites can reach columns rl..rr (every tile with all: the checker's reference)
        private void CollectChunk(ref FrameLights.Entry l, Chunk chunk, StaticTiles[] staticData, in LightTarget t, int rl, int rr,
            bool all, ref int n, ref OccluderSums sums)
        {
            int x0 = chunk.X << 3, y0 = chunk.Y << 3;
            bool stats = FrameStats.On;
            int tiles = 0, objects = 0;

            for (int cell = 0; cell < 64; cell++)
            {
                int tx = x0 + (cell >> 3), ty = y0 + (cell & 7);

                if (!all && !LightOcclusion.TileReachesX((tx - ty) * 22 - _offset.X - 22, chunk.StaticOverLeft, chunk.StaticOverRight, rl, rr))
                {
                    continue;
                }

                if (stats) tiles++;
                CollectTile(ref l, chunk, cell, staticData, in t, ref n, ref sums, stats, ref objects);
            }

            if (stats)
            {
                FrameStats.Add(FrameCounter.LoTiles, tiles);
                FrameStats.Add(FrameCounter.LoObjects, objects);
            }
        }

        // one tile's occluders: its statics and multi pieces, and its ground items
        private void CollectTile(ref FrameLights.Entry l, Chunk chunk, int cell, StaticTiles[] staticData, in LightTarget t, ref int n, ref OccluderSums sums, bool stats, ref int objects)
        {
            LightOccluderEntry rec = _loRecord;
            LightSearchMeasure measure = _measureCandidates ? _lightSearchMeasure : null;

            for (TileListObject o = chunk.HeadAt(cell); o != null; o = o.TNext)
            {
                if (measure != null && (o is Static || o is Multi)) measure.Read(o);
                if (stats) objects++;
                if (rec != null && (o is Static || o is Multi) && CacheCandidate(ref l, in t, o, staticData))
                {
                    rec.AddCandidate(o);
                }

                if (StaticOccluder(ref l, o, staticData, in t, ref n, out byte alpha))
                {
                    Sum(ref sums, LightOcclusion.Mix(o.X, o.Y, o.Z, o.Graphic, false), alpha);
                }
            }

            if (chunk.Dynamic == null || chunk.Dynamic.CountAt(cell) == 0)
            {
                return;
            }

            foreach (ref readonly DynamicTileIndex.Entry en in chunk.Dynamic.Cell(cell))
            {
                if (measure != null && en.Obj is Item) measure.Read(en.Obj);
                if (stats) objects++;
                if (rec != null && en.Obj is Item candidate && CacheCandidate(ref l, in t, candidate, staticData))
                {
                    rec.AddCandidate(candidate);
                }

                if (en.Obj is Item item && !item.IsMulti && !item.IsCorpse && ItemOccluder(ref l, item, staticData, in t, ref n))
                {
                    Sum(ref sums, LightOcclusion.Mix(item.X, item.Y, item.Z, item.Graphic, true), item.AlphaHue);
                }
            }
        }

        private static void Sum(ref OccluderSums sums, uint mix, byte alpha)
        {
            sums.Hash += mix;

            if (alpha >= LightOcclusion.MinOccluderAlpha)
            {
                sums.OpaqueHash += mix;
                sums.Opaque++;
            }
        }

        private void AddOccluder(int i, Texture2D texture, Rectangle uv, int x, int y, bool cot, byte alpha, uint art)
        {
            if (i == _occluders.Length)
            {
                System.Array.Resize(ref _occluders, i * 2);
            }

            ref LightOccluder o = ref _occluders[i];
            o.Texture = texture;
            o.UV = uv;
            o.DrawX = x;
            o.DrawY = y;
            o.Cot = cot;
            o.Alpha = alpha;
            o.Art = art;
        }

        // the cheap terms of light l's occluder test (LightOcclusion.Hides as if its art covered the light point or, a wall
        // piece for a wall source, faced away: not its own emitter, in front of its source, an occluder by tiledata, and to
        // hide by cover not above the source) and the world draw's visibility (review P1 #2): drawn at least
        // LightOcclusion.MinOccluderAlpha opaque; above the max Z or a hidden roof only while it fades out (ProcessAlpha: drawn
        // by this walk with alpha left)
        private bool OccluderCandidate(ref FrameLights.Entry l, in LightTarget t, GameObject o, ref StaticTiles data, byte alpha)
        {
            if (o.IsDestroyed || !o.AllowedToDraw || alpha < _minMaskAlpha ||
                !LightOcclusion.Hides(true, ReferenceEquals(o, l.Source), o.X - l.SrcX, o.Y - l.SrcY, o.Z - l.SrcZ, t.SrcTop, ref data, true,
                    t.Facing != LightOcclusion.FacingNone && data.IsWall))
            {
                return false;
            }

            return !(o.Z >= _maxZ || _noDrawRoofs && data.IsRoof) || o.DrawnFrame == _drawFrame;
        }

        // a static or multi piece that hides light l where its drawn sprite is (GameObject.StaticArt: a tree's stump, the animated
        // frame; a multi piece moved by its render offset), with the circle of transparency as StaticView / MultiView draw it
        private bool StaticOccluder(ref FrameLights.Entry l, TileListObject o, StaticTiles[] staticData, in LightTarget t, ref int n, out byte alpha)
        {
            alpha = 0;

            if (!(o is Static || o is Multi) || (uint)o.Graphic >= (uint)staticData.Length)
            {
                return false;
            }

            ref StaticTiles data = ref staticData[o.Graphic];
            Multi multi = o as Multi;
            alpha = multi != null ? multi.DrawnAlpha : o.AlphaHue;

            if (multi != null && multi.IgnoredInRender ||
                !OccluderCandidate(ref l, in t, o, ref data, alpha))
            {
                return false;
            }

            ViewMargin.Anchor(o.X, o.Y, o.Z, _offset.X, _offset.Y, out int ax, out int ay);
            ushort graphic = o.Graphic;
            bool cot;

            if (multi != null)
            {
                multi.DrawPosition(ref ax, ref ay);
                cot = _loCot && multi.TransparentTest(_loCotZ);
            }
            else
            {
                bool isTree = StaticFilters.IsTree(graphic, out _);
                graphic = Static.DrawnGraphic(graphic, isTree);
                cot = _loCot && !isTree && !data.IsFoliage && o.TransparentTest(_loCotZ);
            }

            ushort drawn = GameObject.StaticArt(graphic, ax, ay, out SpriteInfo art, out int x, out int y);

            if (art.Texture == null || !SpriteHides(in t, ref data, drawn, art.UV.Width, art.UV.Height, x, y, o.X - l.SrcX, o.Y - l.SrcY, o.Z - l.SrcZ))
            {
                return false;
            }

            AddOccluder(n++, art.Texture, art.UV, x, y, cot, alpha, drawn);

            return true;
        }

        // a ground item that hides light l where its drawn sprites are: the item select the world draw runs (render offset,
        // displayed graphic, a stack's second sprite, the sprite's alpha), art sprites only
        private bool ItemOccluder(ref FrameLights.Entry l, Item item, StaticTiles[] staticData, in LightTarget t, ref int n)
        {
            if ((uint)item.Graphic >= (uint)staticData.Length)
            {
                return false;
            }

            ref StaticTiles data = ref staticData[item.Graphic];

            if (!OccluderCandidate(ref l, in t, item, ref data, item.AlphaHue))
            {
                return false;
            }

            ViewMargin.Anchor(item.X, item.Y, item.Z, _offset.X, _offset.Y, out int ax, out int ay);
            ItemSpriteInput.Gather(item, out ItemSpriteInput input);

            if (!_occItems.Select(in input, false, ax, ay, 0f))
            {
                return false;
            }

            ReadOnlySpan<DynamicSprite> sprites = _occItems.Selected;
            int first = n;
            bool hides = false;

            for (int i = 0; i < sprites.Length; i++)
            {
                ref readonly DynamicSprite sp = ref sprites[i];

                if (sp.Kind != DynamicSpriteKind.Art || sp.Hue.Z < _minMaskAlpha / 255f ||
                    !LegacySpriteResolve.Resolve(ClientLegacySpriteSource.Instance, in sp, out LegacyDraw d))
                {
                    continue;
                }

                int x = (int)d.Position.X, y = (int)d.Position.Y;

                if (!LightOcclusion.RectOverlaps(x, y, d.Source.Width, d.Source.Height, t.Left, t.Top, t.Right, t.Bottom))
                {
                    continue;
                }

                hides |= SpriteHides(in t, ref data, sp.Id, d.Source.Width, d.Source.Height, x, y, item.X - l.SrcX, item.Y - l.SrcY, item.Z - l.SrcZ);
                AddOccluder(n++, d.Texture, d.Source, x, y, false, (byte)Math.Clamp((int)(sp.Hue.Z * 255f + 0.5f), 0, 255), sp.Id);
            }

            if (!hides)
            {
                for (int i = first; i < n; i++)
                {
                    _occluders[i].Texture = null;
                }

                n = first;
            }

            return hides;
        }

        // a drawn sprite (art id drawn, w x h at top left x, y) of an occluder at (dx, dy, dz) from light l's source covers its
        // light point (the art's pixel there) and is not above the source (LightOcclusion.Above), or (a wall source) is a wall
        // piece under its quad facing away from it (LightOcclusion.FacesAway)
        private static bool SpriteHides(in LightTarget t, ref StaticTiles data, uint drawn, int w, int h, int x, int y, int dx, int dy, int dz)
        {
            int lx = t.Px - x, ly = t.Py - y;

            if (!LightOcclusion.Above(dz, t.SrcTop) && (uint)lx < (uint)w && (uint)ly < (uint)h && Client.Game.UO.Arts.PixelCheck(drawn, lx, ly))
            {
                return true;
            }

            if (t.Facing == LightOcclusion.FacingNone || !data.IsWall || !LightOcclusion.RectOverlaps(x, y, w, h, t.Left, t.Top, t.Right, t.Bottom))
            {
                return false;
            }

            Rectangle b = Client.Game.UO.Arts.GetRealArtBounds(drawn);

            return LightOcclusion.FacesAway(t.Facing, LightOcclusion.Facing(w, b.X, b.Width), dx, dy);
        }

#if CUO_DEVTOOLS
        private long _loFrames, _loLights, _loOccludedLights, _loOccluderSprites, _loMismatches, _loRadialUnmasked;
        // the last light pass's lights checked, lights drawn masked and radial lights with occluders left unmasked, for the
        // summary
        private int _loPassLights, _loPassMasked, _loPassRadial;
        private uint _loSummaryAt;

        // CUO_LIGHT_OCCLUSION_CHECK: every tile of every drawn chunk with the same occluder test, no chunk or column pruning;
        // a different sprite count or hash means the pruning missed an occluder (art or Z beyond the chunk's record)
        private void CheckOccluders(ref FrameLights.Entry l, in LightTarget t, int n, uint hash, StaticTiles[] staticData)
        {
            int end = _occBase + n, refN = end;
            OccluderSums refSums = default;
            List<Chunk> chunks = OccluderChunks;

            for (int c = 0; c < chunks.Count; c++)
            {
                if (!chunks[c].IsDestroyed)
                {
                    CollectChunk(ref l, chunks[c], staticData, in t, 0, 0, true, ref refN, ref refSums);
                }
            }

            uint refHash = refSums.Hash;

            // the reference's sprites were appended past the light's own: drop them
            for (int i = end; i < refN; i++)
            {
                _occluders[i].Texture = null;
            }

            int refCount = refN - end;
            _loLights++;
            _loPassLights++;

            if (refCount != n || refHash != hash)
            {
                _loMismatches++;

                if (_loMismatches <= 20)
                {
                    Log.Warn($"[light-occlusion-check] light {l.ID} from {l.SrcX},{l.SrcY},{l.SrcZ}: occluder sprites {n} (hash {hash:X8}), " +
                             $"reference {refCount} (hash {refHash:X8})");
                }
            }
        }

        private long _loCacheChecked, _loCacheMismatches;
        private uint _loCacheSummaryAt;

        // CUO_LIGHT_OCCLUSION_CACHE_CHECK: a cached search's occluders (n sprites from _occBase, sums) against the full search's,
        // appended past them and dropped: the same sprite count, hash and opaque sums, or a logged mismatch
        private void CheckCachedOccluders(ref FrameLights.Entry l, in LightTarget t, int rl, int rt, int rr, int rb, int n, in OccluderSums sums,
            StaticTiles[] staticData)
        {
            int end = _occBase + n, refN = end;
            OccluderSums refSums = default;
            CollectVisible(ref l, staticData, in t, rl, rt, rr, rb, ref refN, ref refSums);

            for (int i = end; i < refN; i++)
            {
                _occluders[i].Texture = null;
            }

            _loCacheChecked++;

            if (refN - end != n || refSums.Hash != sums.Hash || refSums.OpaqueHash != sums.OpaqueHash || refSums.Opaque != sums.Opaque)
            {
                _loCacheMismatches++;

                if (_loCacheMismatches <= 20)
                {
                    Log.Warn($"[light-occlusion-cache-check] light {l.ID} from {l.SrcX},{l.SrcY},{l.SrcZ} (0x{l.Source.Graphic:X4}): cached " +
                             $"{n} occluder sprites (hash {sums.Hash:X8}), full search {refN - end} (hash {refSums.Hash:X8})");
                }
            }
        }

        // every 5 s with CUO_LIGHT_OCCLUSION_CACHE_CHECK
        private void LightOcclusionCacheSummary()
        {
            if (!LightOcclusionCache.Check || Time.Ticks < _loCacheSummaryAt)
            {
                return;
            }

            _loCacheSummaryAt = Time.Ticks + 5000;
            Log.Info($"[light-occlusion-cache-check] light_occlusion_cache {(LightOcclusionCache.Enabled ? "on" : "off")} " +
                     $"(light_occlusion {(_lightOcclusion ? "on" : "off")}): hits {_loCacheHits}, rebuilds {_loCacheMisses}, " +
                     $"{_loCacheChecked} cached searches checked; entries {_loTable.Count}, {_loTable.Bytes() / 1024} KB, evicted " +
                     $"{_loTable.Evictions}, idle dropped {_loTable.Dropped}; mismatches {_loCacheMismatches}");
        }

        // once per light pass with the checker on
        private void LightOcclusionSummary()
        {
            LightOcclusionCacheSummary();

            if (!LightOcclusion.Check)
            {
                return;
            }

            _loFrames++;

            if (Time.Ticks >= _loSummaryAt)
            {
                _loSummaryAt = Time.Ticks + 5000;
                Log.Info($"[light-occlusion-check] light_occlusion {(_lightOcclusion ? "on" : "off")}: " +
                         $"{_loFrames} light passes, {_loLights} lights checked, {_loOccludedLights} drawn with occluders ({_loOccluderSprites} occluder sprites), " +
                         $"{_loRadialUnmasked} radial lights with occluders left unmasked; last pass: {_loPassLights} lights, {_loPassMasked} masked, " +
                         $"{_loPassRadial} radial unmasked; mismatches {_loMismatches}");
            }
        }
#endif
    }
}
