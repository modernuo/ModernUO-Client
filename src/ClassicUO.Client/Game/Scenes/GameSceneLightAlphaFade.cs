// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Uo.Pack;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // light_alpha_fade: the share of light l's glow its emitter leaves (LightAlphaFade): 1 for a radial glow or a mobile's
        // light; for a surface glow the emitter's drawn alpha times the shader circle's fade at its light point
        private float EmitterFade(ref FrameLights.Entry l, ref Matrix matrix, out bool surface)
        {
            StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;
            surface = SurfaceGlow(l.Source, l.ID, staticData);

            if (!surface)
            {
                return 1f;
            }

            return LightAlphaFade.Fade(EmitterAlpha(l.Source), EmitterCot(ref l, ref matrix, staticData));
        }

        // light id's glow from emitter src is a surface glow (LightAlphaFade.IsSurface: its shape's class in lightclass.bin,
        // else the emitter's tiledata Wall/Window flags); a mobile's light is radial. light_alpha_fade and
        // light_occlusion_surface_only share it
        private static bool SurfaceGlow(GameObject src, byte id, StaticTiles[] staticData)
        {
            if (src == null || src is Mobile)
            {
                return false;
            }

            ushort g = src.Graphic;
            bool wallOrWindow = (uint)g < (uint)staticData.Length && (staticData[g].IsWall || staticData[g].IsWindow);

            return LightAlphaFade.IsSurface(Client.Game.UO.FileManager.Lights.Class(id), wallOrWindow);
        }

        private static byte EmitterAlpha(GameObject src) => src is Multi m ? m.DrawnAlpha : src.AlphaHue;

        // the shader circle's fade at the emitter's light point when the world draws the emitter with the circle (StaticView,
        // MultiView: trees and foliage never); 1 otherwise
        private float EmitterCot(ref FrameLights.Entry l, ref Matrix matrix, StaticTiles[] staticData)
        {
            if (_lafRadius <= 0f || !EmitterInCot(l.Source, staticData))
            {
                return 1f;
            }

            ViewMargin.Anchor(l.SrcX, l.SrcY, l.SrcZ, _offset.X, _offset.Y, out int sax, out int say);

            if (!EmitterLightPoint(l.Source, sax, say, out int px, out int py))
            {
                px = l.DrawX;
                py = l.DrawY;
            }

            Vector2 p = Vector2.Transform(new Vector2(px, py), matrix);
            float dx = p.X - _lafCx, dy = p.Y - _lafCy;

            return LightAlphaFade.CotFade(MathF.Sqrt(dx * dx + dy * dy), _lafRadius);
        }

        private bool EmitterInCot(GameObject src, StaticTiles[] staticData)
        {
            if (src is Multi multi)
            {
                return multi.TransparentTest(_loCotZ);
            }

            if (src is Static st && (uint)st.Graphic < (uint)staticData.Length)
            {
                return !StaticFilters.IsTree(st.Graphic, out _) && !staticData[st.Graphic].IsFoliage && st.TransparentTest(_loCotZ);
            }

            return false;
        }

        // the centre of the emitter's visible art (LightOcclusion.LightPoint), anchored at (sax, say)
        private static bool EmitterLightPoint(GameObject src, int sax, int say, out int px, out int py)
        {
            ushort g = src is Item item ? item.DisplayedGraphic : src.Graphic;
            ref ArtEntry entry = ref Client.Game.UO.FileManager.Arts.GetEntry(g + 0x4000);
            uint drawn = (uint)(g + entry.AnimOffset);
            SpriteInfo art = Client.Game.UO.Arts.GetArt(drawn);
            Rectangle b = Client.Game.UO.Arts.GetRealArtBounds(drawn);

            if (art.Texture == null || b.Width <= 0 || b.Height <= 0)
            {
                px = py = 0;

                return false;
            }

            LightOcclusion.LightPoint(sax, say, art.UV.Width, art.UV.Height, b.X, b.Y, b.Width, b.Height, out px, out py);

            return true;
        }

#if CUO_DEVTOOLS
        private long _lafFrames, _lafLights, _lafSurface, _lafFaded, _lafHidden, _lafRadialSpared, _lafMasks, _lafDithered, _lafMismatches;
        private uint _lafSummaryAt;

        // CUO_LIGHT_ALPHA_FADE_CHECK: the unfaded path draws every glow at 1. The new one may differ only for a surface glow
        // whose emitter is drawn below full alpha or inside the shader circle; any other difference is logged
        private void CheckFade(ref FrameLights.Entry l, ref Matrix matrix, float fade, bool surface)
        {
            _lafLights++;
            GameObject src = l.Source;
            StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;
            byte alpha = src is Mobile ? (byte)255 : EmitterAlpha(src);
            bool inCot = !(src is Mobile) && _lafRadius > 0f && EmitterInCot(src, staticData);
            bool drawnFull = alpha == 255 && !inCot;
            string why = null;

            if (fade < 0f || fade > 1f)
            {
                why = "fade outside 0..1";
            }
            else if (!surface && fade != 1f)
            {
                why = "a radial glow faded";
            }
            else if (surface && drawnFull && fade != 1f)
            {
                why = "faded at full alpha outside the circle";
            }

            if (surface)
            {
                _lafSurface++;

                if (fade <= 0f)
                {
                    _lafHidden++;
                }
                else if (fade < 1f)
                {
                    _lafFaded++;
                }
            }
            else if (!drawnFull)
            {
                _lafRadialSpared++;
            }

            if (why != null)
            {
                _lafMismatches++;

                if (_lafMismatches <= 20)
                {
                    Log.Warn($"[light-alpha-fade-check] light {l.ID} from 0x{src.Graphic:X4} at {l.SrcX},{l.SrcY},{l.SrcZ}: {why} " +
                             $"(class {Client.Game.UO.FileManager.Lights.Class(l.ID)}, alpha {alpha}, circle {inCot}, fade {fade:F3})");
                }
            }
        }

        // the occluders the binary cut (LightOcclusion.MinOccluderAlpha) takes for light l
        private OccluderSums BinaryMaskSums(ref FrameLights.Entry l, int left, int top, int w, int h)
        {
            byte min = _minMaskAlpha;
            _minMaskAlpha = LightOcclusion.MinOccluderAlpha;
            int n = CollectOccluders(ref l, left, top, w, h, false, out _, out OccluderSums sums);
            ReleaseOccluders(n);
            _minMaskAlpha = min;

            return sums;
        }

        // the dithered masks hold exactly the binary cut's occluders among those at least half opaque; the rest are the
        // occluders below 128 the dither adds
        private void CompareMasks(ref FrameLights.Entry l, in OccluderSums binary, in OccluderSums dithered)
        {
            _lafMasks++;

            if (dithered.Hash != dithered.OpaqueHash)
            {
                _lafDithered++;
            }

            if (binary.Opaque != dithered.Opaque || binary.OpaqueHash != dithered.OpaqueHash || binary.Hash != binary.OpaqueHash)
            {
                _lafMismatches++;

                if (_lafMismatches <= 20)
                {
                    Log.Warn($"[light-alpha-fade-check] light {l.ID} from {l.SrcX},{l.SrcY},{l.SrcZ}: binary-cut occluders {binary.Opaque} " +
                             $"(hash {binary.OpaqueHash:X8}), dithered set's opaque ones {dithered.Opaque} (hash {dithered.OpaqueHash:X8})");
                }
            }
        }

        // once per light pass with the checker on
        private void LightAlphaFadeSummary()
        {
            if (!LightAlphaFade.Check)
            {
                return;
            }

            _lafFrames++;

            if (Time.Ticks >= _lafSummaryAt)
            {
                _lafSummaryAt = Time.Ticks + 5000;
                Log.Info($"[light-alpha-fade-check] light_alpha_fade {(_alphaFade ? "on" : "off")} (light_occlusion {(_lightOcclusion ? "on" : "off")}, " +
                         $"lightclass.bin {(Client.Game.UO.FileManager.Lights.HasClasses ? "present" : "absent")}): {_lafFrames} light passes, " +
                         $"{_lafLights} lights, {_lafSurface} surface ({_lafFaded} faded, {_lafHidden} hidden), {_lafRadialSpared} radial kept " +
                         $"full under a fade; {_lafMasks} masks checked, {_lafDithered} with occluders below 128; mismatches {_lafMismatches}");
            }
        }
#endif
    }
}
