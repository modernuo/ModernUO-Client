// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
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
        // light_occlusion: what one light's occluders are tested against: its light point, its quad, and a wall source's
        // facing (LightOcclusion.Facing)
        private struct LightTarget
        {
            public int Px, Py, Left, Top, Right, Bottom;
            public byte Facing;
        }

        private struct LightOccluder
        {
            public Texture2D Texture;
            public Rectangle UV;
            public int DrawX, DrawY;
        }

        private bool _lightOcclusion;
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

            if (_lightOcclusion)
            {
                device.Clear(ClearOptions.Stencil, Vector4.Zero, 0f, 0);
            }
        }

        // one light of the light pass. light_occlusion on: the art of the occluders covering its source's light point goes
        // into the stencil (no colour) with a fresh reference, and the light is drawn where the stencil does not hold it
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
                _stencilRef = 1;
            }

            EnsureOcclusionStates();
            batcher.SetStencil(_occWrite[_stencilRef]);
            batcher.SetBlendState(_noColorBlend);

            for (int i = 0; i < n; i++)
            {
                ref LightOccluder o = ref _occluders[i];
                batcher.Draw(o.Texture, new Vector2(o.DrawX, o.DrawY), o.UV, Vector3.UnitZ, 0f);
            }

            batcher.SetStencil(_occTest[_stencilRef]);
            batcher.SetBlendState(BlendState.Additive);
            batcher.Draw(lightInfo.Texture, pos, lightInfo.UV, hue, 0f);
            stencilSet = true;
#if CUO_DEVTOOLS
            _loOccludedLights++;
            _loOccluderSprites += n;
#endif
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

        // the occluders of light l (its quad at left, top, w x h), whose art covers its light point (LightOcclusion.LightPoint)
        // or, for a wall source, wall pieces facing away from it (LightOcclusion.FacesAway), into _occluders; with check,
        // compared with a reference scan of every tile up to ReferenceReach around the source
        private int CollectOccluders(ref FrameLights.Entry l, int left, int top, int w, int h, bool check, out uint hash)
        {
            ViewMargin.Anchor(l.SrcX, l.SrcY, l.SrcZ, _offset.X, _offset.Y, out int sax, out int say);
            StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;
            LightTarget t = new LightTarget { Px = l.DrawX, Py = l.DrawY, Left = left, Top = top, Right = left + w, Bottom = top + h };
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
            }

            // a wall source's occluders may be anywhere under its quad; any other's must cover its light point
            int sLo, sHi, dLo, dHi;

            if (t.Facing != LightOcclusion.FacingNone)
            {
                LightOcclusion.Window(sax, say, t.Left, t.Top, t.Right, t.Bottom, out sLo, out sHi, out dLo, out dHi);
            }
            else
            {
                LightOcclusion.Window(sax, say, t.Px, t.Py, t.Px + 1, t.Py + 1, out sLo, out sHi, out dLo, out dHi);
            }
            int n = 0;
            hash = 0;

            for (int s = sLo; s <= sHi; s++)
            {
                int d0 = ((s + dLo) & 1) != 0 ? dLo + 1 : dLo;

                for (int d = d0; d <= dHi; d += 2)
                {
                    CollectTile(ref l, l.SrcX + ((s + d) >> 1), l.SrcY + ((s - d) >> 1), staticData, in t, ref n, ref hash);
                }
            }

#if CUO_DEVTOOLS
            if (check)
            {
                CheckOccluders(ref l, in t, n, hash, staticData, sLo, sHi, dLo, dHi);
            }
#endif

            return n;
        }

        // one tile's occluders: its statics and multi pieces, and its ground items
        private void CollectTile(ref FrameLights.Entry l, int tx, int ty, StaticTiles[] staticData, in LightTarget t, ref int n, ref uint hash)
        {
            Chunk chunk = tx < 0 || ty < 0 ? null : _world.Map.GetChunk(tx, ty, false);

            if (chunk == null)
            {
                return;
            }

            int cell = ((tx & 7) << 3) | (ty & 7);

            for (TileListObject o = chunk.HeadAt(cell); o != null; o = o.TNext)
            {
                if (Occluder(ref l, o, o.Graphic, staticData, in t, out SpriteInfo art, out int x, out int y))
                {
                    hash += LightOcclusion.Mix(o.X, o.Y, o.Z, o.Graphic, false);
                    AddOccluder(n++, in art, x, y);
                }
            }

            if (chunk.Dynamic == null || chunk.Dynamic.CountAt(cell) == 0)
            {
                return;
            }

            foreach (ref readonly DynamicTileIndex.Entry en in chunk.Dynamic.Cell(cell))
            {
                if (en.Obj is Item item && !item.IsMulti && !item.IsCorpse &&
                    Occluder(ref l, item, item.DisplayedGraphic, staticData, in t, out SpriteInfo art, out int x, out int y))
                {
                    hash += LightOcclusion.Mix(item.X, item.Y, item.Z, item.Graphic, true);
                    AddOccluder(n++, in art, x, y);
                }
            }
        }

        private void AddOccluder(int i, in SpriteInfo art, int x, int y)
        {
            if (i == _occluders.Length)
            {
                System.Array.Resize(ref _occluders, i * 2);
            }

            ref LightOccluder o = ref _occluders[i];
            o.Texture = art.Texture;
            o.UV = art.UV;
            o.DrawX = x;
            o.DrawY = y;
        }

        // a drawn static, multi piece or ground item, not light l's own emitter, in front of its source, whose art covers the
        // light point or (a wall source) a wall piece under its quad facing away from it, so it hides the light where its art
        // is (LightOcclusion.Hides); with its art and draw position (View.DrawStaticAnimated)
        private bool Occluder(ref FrameLights.Entry l, GameObject o, ushort graphic, StaticTiles[] staticData, in LightTarget t,
            out SpriteInfo art, out int x, out int y)
        {
            art = default;
            x = y = 0;

            if (!(o is Static || o is Multi || o is Item) || o.IsDestroyed || !o.AllowedToDraw || o.Z >= _maxZ ||
                (uint)o.Graphic >= (uint)staticData.Length)
            {
                return false;
            }

            ref StaticTiles data = ref staticData[o.Graphic];
            int dx = o.X - l.SrcX, dy = o.Y - l.SrcY, dz = o.Z - l.SrcZ;

            // the cheap terms first (in front, an occluder, not the emitter); cover or facing below
            if (_noDrawRoofs && data.IsRoof ||
                !LightOcclusion.Hides(true, ReferenceEquals(o, l.Source), dx, dy, dz, ref data, true))
            {
                return false;
            }

            ref ArtEntry entry = ref Client.Game.UO.FileManager.Arts.GetEntry(graphic + 0x4000);
            uint drawn = (uint)(graphic + entry.AnimOffset);
            art = Client.Game.UO.Arts.GetArt(drawn);

            if (art.Texture == null)
            {
                return false;
            }

            ViewMargin.Anchor(o.X, o.Y, o.Z, _offset.X, _offset.Y, out int ax, out int ay);

            bool covers = LightOcclusion.CoverPoint(ax, ay, art.UV.Width, art.UV.Height, t.Px, t.Py, out int lx, out int ly) &&
                Client.Game.UO.Arts.PixelCheck(drawn, lx, ly);

            if (!covers)
            {
                if (t.Facing == LightOcclusion.FacingNone || !data.IsWall ||
                    !LightOcclusion.ArtOverlaps(ax, ay, art.UV.Width, art.UV.Height, t.Left, t.Top, t.Right, t.Bottom))
                {
                    return false;
                }

                Rectangle b = Client.Game.UO.Arts.GetRealArtBounds(drawn);

                if (!LightOcclusion.FacesAway(t.Facing, LightOcclusion.Facing(art.UV.Width, b.X, b.Width), dx, dy))
                {
                    return false;
                }
            }

            x = ax - ((art.UV.Width >> 1) - 22);
            y = ay - (art.UV.Height - 44);

            return true;
        }

#if CUO_DEVTOOLS
        private long _loFrames, _loLights, _loOccludedLights, _loOccluderSprites, _loMismatches;
        private uint _loSummaryAt;

        // CUO_LIGHT_OCCLUSION_CHECK: every tile up to ReferenceReach around the source, the same occluder test; a different
        // count or hash means the window missed an occluder (art or Z beyond what it assumes)
        private void CheckOccluders(ref FrameLights.Entry l, in LightTarget t, int n, uint hash, StaticTiles[] staticData,
            int sLo, int sHi, int dLo, int dHi)
        {
            Map.Map map = _world.Map;
            int refCount = 0, r = LightOcclusion.ReferenceReach;
            uint refHash = 0;
            GameObject missed = null;

            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    int tx = l.SrcX + dx, ty = l.SrcY + dy;
                    Chunk chunk = tx < 0 || ty < 0 ? null : map.GetChunk(tx, ty, false);

                    if (chunk == null)
                    {
                        continue;
                    }

                    int cell = ((tx & 7) << 3) | (ty & 7);
                    int s = dx + dy, d = dx - dy;
                    bool inWindow = dx == 0 && dy == 0 || s >= sLo && s <= sHi && d >= dLo && d <= dHi;

                    for (TileListObject o = chunk.HeadAt(cell); o != null; o = o.TNext)
                    {
                        if (Occluder(ref l, o, o.Graphic, staticData, in t, out _, out _, out _))
                        {
                            refCount++;
                            refHash += LightOcclusion.Mix(o.X, o.Y, o.Z, o.Graphic, false);

                            if (!inWindow)
                            {
                                missed ??= o;
                            }
                        }
                    }

                    if (chunk.Dynamic == null)
                    {
                        continue;
                    }

                    foreach (ref readonly DynamicTileIndex.Entry en in chunk.Dynamic.Cell(cell))
                    {
                        if (en.Obj is Item item && !item.IsMulti && !item.IsCorpse &&
                            Occluder(ref l, item, item.DisplayedGraphic, staticData, in t, out _, out _, out _))
                        {
                            refCount++;
                            refHash += LightOcclusion.Mix(item.X, item.Y, item.Z, item.Graphic, true);

                            if (!inWindow)
                            {
                                missed ??= item;
                            }
                        }
                    }
                }
            }

            _loLights++;

            if (refCount != n || refHash != hash)
            {
                _loMismatches++;

                if (_loMismatches <= 20)
                {
                    Log.Warn($"[light-occlusion-check] light {l.ID} from {l.SrcX},{l.SrcY},{l.SrcZ}: occluders {n} (hash {hash:X8}), reference {refCount} " +
                             $"(hash {refHash:X8}); first outside the window: {missed?.GetType().Name} 0x{missed?.Graphic:X4} at {missed?.X},{missed?.Y},{missed?.Z}");
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
