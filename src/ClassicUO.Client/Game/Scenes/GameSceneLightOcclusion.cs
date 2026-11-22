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
        }

        private bool _lightOcclusion;
        // light_occlusion: the circle of transparency the masks share with the world draw (shader mode; the gradient mode is
        // in AlphaHue) and the Z its TransparentTest takes
        private bool _loCot;
        private int _loCotZ;
        // light_occlusion: the item sprites the world draw makes (ItemSpriteExtract select), per scene
        private readonly ItemSpriteExtract _occItems = new ItemSpriteExtract();
        private int _stencilRef;
        // light_occlusion: one light's occluders, grown to the high-water mark and reused
        private LightOccluder[] _occluders = new LightOccluder[64];
        // one write and one test state per stencil reference (1..255), made on first use and kept with the scene
        private DepthStencilState[] _occWrite, _occTest;
        private BlendState _noColorBlend;

        // the light pass starts: the light target's stencil is cleared when the switch is on
        private void BeginLightOcclusion(GraphicsDevice device)
        {
            _lightOcclusion = LightOcclusion.Enabled;
            _stencilRef = 0;
            Profile profile = ProfileManager.CurrentProfile;
            _loCot = profile != null && profile.UseCircleOfTransparency && profile.CircleOfTransparencyType != 1 && _world.Player != null;
            _loCotZ = _world.Player != null ? _world.Player.Z + 5 : 0;

            if (_lightOcclusion)
            {
                device.Clear(ClearOptions.Stencil, Vector4.Zero, 0f, 0);
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

        // one light of the light pass. light_occlusion on: the drawn sprites of the occluders covering its source's light point
        // go into the stencil (no colour) with a fresh reference, and the light is drawn where the stencil does not hold it
        private void DrawLight(UltimaBatcher2D batcher, ref Matrix matrix, ref FrameLights.Entry l, in SpriteInfo lightInfo, Vector3 hue, ref bool stencilSet)
        {
            Vector2 pos = new Vector2(l.DrawX - lightInfo.UV.Width * 0.5f, l.DrawY - lightInfo.UV.Height * 0.5f);
            int n = 0;

            if ((_lightOcclusion || LightOcclusion.Check) && l.Source != null)
            {
                n = CollectOccluders(ref l, (int)pos.X, (int)pos.Y, lightInfo.UV.Width, lightInfo.UV.Height, LightOcclusion.Check, out _);
            }

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

            for (int i = 0; i < n; i++)
            {
                ref LightOccluder o = ref _occluders[i];
                // an alpha above 1 marks the circle of transparency (ShaderHueTranslator.GetHueVector circletrans)
                batcher.Draw(o.Texture, new Vector2(o.DrawX, o.DrawY), o.UV, o.Cot ? new Vector3(0f, 0f, 2f) : Vector3.UnitZ, 0f);
            }

            ReleaseOccluders(n);
            batcher.SetStencil(_occTest[_stencilRef]);
            batcher.SetBlendState(BlendState.Additive);
            batcher.Draw(lightInfo.Texture, pos, lightInfo.UV, hue, 0f);
            stencilSet = true;
#if CUO_DEVTOOLS
            _loOccludedLights++;
            _loOccluderSprites += n;
#endif
        }

        // no texture kept past the light that used it (review P2 #7)
        private void ReleaseOccluders(int n)
        {
            for (int i = 0; i < n; i++)
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
        private int CollectOccluders(ref FrameLights.Entry l, int left, int top, int w, int h, bool check, out uint hash)
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

            int n = 0;
            hash = 0;
            List<Chunk> chunks = _visibleChunks;

            for (int c = 0; c < chunks.Count; c++)
            {
                Chunk chunk = chunks[c];

                if (chunk.IsDestroyed || !LightOcclusion.ChunkReaches(chunk.X, chunk.Y, chunk.StaticOverLeft, chunk.StaticOverRight,
                    chunk.StaticOverUp, chunk.OccZLo, chunk.OccZHi, _offset.X, _offset.Y, rl, rt, rr, rb))
                {
                    continue;
                }

                CollectChunk(ref l, chunk, staticData, in t, rl, rr, false, ref n, ref hash);
            }

#if CUO_DEVTOOLS
            if (check)
            {
                CheckOccluders(ref l, in t, n, hash, staticData);
            }
#endif

            return n;
        }

        // the chunk's tiles whose sprites can reach columns rl..rr (every tile with all: the checker's reference)
        private void CollectChunk(ref FrameLights.Entry l, Chunk chunk, StaticTiles[] staticData, in LightTarget t, int rl, int rr,
            bool all, ref int n, ref uint hash)
        {
            int x0 = chunk.X << 3, y0 = chunk.Y << 3;

            for (int cell = 0; cell < 64; cell++)
            {
                int tx = x0 + (cell >> 3), ty = y0 + (cell & 7);

                if (!all && !LightOcclusion.TileReachesX((tx - ty) * 22 - _offset.X - 22, chunk.StaticOverLeft, chunk.StaticOverRight, rl, rr))
                {
                    continue;
                }

                CollectTile(ref l, chunk, cell, staticData, in t, ref n, ref hash);
            }
        }

        // one tile's occluders: its statics and multi pieces, and its ground items
        private void CollectTile(ref FrameLights.Entry l, Chunk chunk, int cell, StaticTiles[] staticData, in LightTarget t, ref int n, ref uint hash)
        {
            for (TileListObject o = chunk.HeadAt(cell); o != null; o = o.TNext)
            {
                if (StaticOccluder(ref l, o, staticData, in t, ref n))
                {
                    hash += LightOcclusion.Mix(o.X, o.Y, o.Z, o.Graphic, false);
                }
            }

            if (chunk.Dynamic == null || chunk.Dynamic.CountAt(cell) == 0)
            {
                return;
            }

            foreach (ref readonly DynamicTileIndex.Entry en in chunk.Dynamic.Cell(cell))
            {
                if (en.Obj is Item item && !item.IsMulti && !item.IsCorpse && ItemOccluder(ref l, item, staticData, in t, ref n))
                {
                    hash += LightOcclusion.Mix(item.X, item.Y, item.Z, item.Graphic, true);
                }
            }
        }

        private void AddOccluder(int i, Texture2D texture, Rectangle uv, int x, int y, bool cot)
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
        }

        // the cheap terms of light l's occluder test (LightOcclusion.Hides as if its art covered the light point or, a wall
        // piece for a wall source, faced away: not its own emitter, in front of its source, an occluder by tiledata, and to
        // hide by cover not above the source) and the world draw's visibility (review P1 #2): drawn at least
        // LightOcclusion.MinOccluderAlpha opaque; above the max Z or a hidden roof only while it fades out (ProcessAlpha: drawn
        // by this walk with alpha left)
        private bool OccluderCandidate(ref FrameLights.Entry l, in LightTarget t, GameObject o, ref StaticTiles data, byte alpha)
        {
            if (o.IsDestroyed || !o.AllowedToDraw || alpha < LightOcclusion.MinOccluderAlpha ||
                !LightOcclusion.Hides(true, ReferenceEquals(o, l.Source), o.X - l.SrcX, o.Y - l.SrcY, o.Z - l.SrcZ, t.SrcTop, ref data, true,
                    t.Facing != LightOcclusion.FacingNone && data.IsWall))
            {
                return false;
            }

            return !(o.Z >= _maxZ || _noDrawRoofs && data.IsRoof) || o.DrawnFrame == _drawFrame;
        }

        // a static or multi piece that hides light l where its drawn sprite is (GameObject.StaticArt: a tree's stump, the animated
        // frame; a multi piece moved by its render offset), with the circle of transparency as StaticView / MultiView draw it
        private bool StaticOccluder(ref FrameLights.Entry l, TileListObject o, StaticTiles[] staticData, in LightTarget t, ref int n)
        {
            if (!(o is Static || o is Multi) || (uint)o.Graphic >= (uint)staticData.Length)
            {
                return false;
            }

            ref StaticTiles data = ref staticData[o.Graphic];
            Multi multi = o as Multi;

            if (multi != null && multi.IgnoredInRender ||
                !OccluderCandidate(ref l, in t, o, ref data, multi != null ? multi.DrawnAlpha : o.AlphaHue))
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

            AddOccluder(n++, art.Texture, art.UV, x, y, cot);

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

                if (sp.Kind != DynamicSpriteKind.Art || sp.Hue.Z < LightOcclusion.MinOccluderAlpha / 255f ||
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
                AddOccluder(n++, d.Texture, d.Source, x, y, false);
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
        private long _loFrames, _loLights, _loOccludedLights, _loOccluderSprites, _loMismatches;
        private uint _loSummaryAt;

        // CUO_LIGHT_OCCLUSION_CHECK: every tile of every drawn chunk with the same occluder test, no chunk or column pruning;
        // a different sprite count or hash means the pruning missed an occluder (art or Z beyond the chunk's record)
        private void CheckOccluders(ref FrameLights.Entry l, in LightTarget t, int n, uint hash, StaticTiles[] staticData)
        {
            int refN = n;
            uint refHash = 0;
            List<Chunk> chunks = _visibleChunks;

            for (int c = 0; c < chunks.Count; c++)
            {
                if (!chunks[c].IsDestroyed)
                {
                    CollectChunk(ref l, chunks[c], staticData, in t, 0, 0, true, ref refN, ref refHash);
                }
            }

            // the reference's sprites were appended past the light's own: drop them
            for (int i = n; i < refN; i++)
            {
                _occluders[i].Texture = null;
            }

            int refCount = refN - n;
            _loLights++;

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

        // once per light pass with the checker on
        private void LightOcclusionSummary()
        {
            if (!LightOcclusion.Check)
            {
                return;
            }

            _loFrames++;

            if (Time.Ticks >= _loSummaryAt)
            {
                _loSummaryAt = Time.Ticks + 5000;
                Log.Info($"[light-occlusion-check] light_occlusion {(_lightOcclusion ? "on" : "off")}: {_loFrames} light passes, {_loLights} lights checked, " +
                         $"{_loOccludedLights} drawn with occluders ({_loOccluderSprites} occluder sprites); mismatches {_loMismatches}");
            }
        }
#endif
    }
}
