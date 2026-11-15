// SPDX-License-Identifier: BSD-2-Clause

using System;
using Microsoft.Xna.Framework;
using Uo.Render;

namespace ClassicUO.Game
{
    /// <summary>
    /// ClassicUO's seated-character deformation as Uo.Render instances: MobileView.CalculateSitAnimation (the section
    /// fractions of each layer, relative to the seated body) and Batcher2D.DrawCharacterSitted (up to three horizontal
    /// sections: the upper body moved 8 px toward the facing side, the middle sheared back, the legs in place, each
    /// sampling its own row range). Shared by the view capture (GpuWorldRenderer) and the ECS extract (MobileSpriteExtract),
    /// so the parity harness compares the section fractions, not two copies of the geometry.
    /// </summary>
    internal static class SitDeform
    {
        private const float UPPER_BODY_RATIO = 0.35f;
        private const float MID_BODY_RATIO = 0.60f;
        private const float LOWER_BODY_RATIO = 0.94f;

        /// <summary>The seated body's waist and knee rows, carried from the body layer to its equipment (MobileView's statics).</summary>
        public struct BodyRows
        {
            public int WaistY, KneesY;
        }

        /// <summary>
        /// MobileView.CalculateSitAnimation for a layer with a texture whose top-left row is <paramref name="y"/> and height
        /// <paramref name="height"/>. The human body layer (<paramref name="isItem"/> false) sets <paramref name="rows"/>;
        /// equipment layers read them.
        /// </summary>
        public static Vector3 Mod(ref BodyRows rows, int y, int height, bool isItem, bool isHuman)
        {
            if (!isItem && isHuman)
            {
                int frameHeight = height == 0 ? 61 : height;
                rows.WaistY = (int)(frameHeight * UPPER_BODY_RATIO) + y;
                rows.KneesY = (int)(frameHeight * MID_BODY_RATIO) + y;
            }

            Vector3 mod = new Vector3(UPPER_BODY_RATIO, MID_BODY_RATIO, LOWER_BODY_RATIO);

            if (!isItem)
            {
                return mod;
            }

            float itemsEndY = y + height;

            if (y >= rows.WaistY)
            {
                mod.X = 0;
            }
            else if (itemsEndY <= rows.WaistY)
            {
                mod.X = 1.0f;
            }
            else
            {
                float upperBodyDiff = rows.WaistY - y;
                mod.X = upperBodyDiff / height;

                if (mod.X < 0)
                {
                    mod.X = 0;
                }
            }

            if (rows.WaistY >= itemsEndY || y >= rows.KneesY)
            {
                mod.Y = 0;
            }
            else if (rows.WaistY <= y && itemsEndY <= rows.KneesY)
            {
                mod.Y = 1.0f;
            }
            else
            {
                float midBodyDiff;

                if (y >= rows.WaistY)
                {
                    midBodyDiff = rows.KneesY - y;
                }
                else if (itemsEndY <= rows.KneesY)
                {
                    midBodyDiff = itemsEndY - rows.WaistY;
                }
                else
                {
                    midBodyDiff = rows.KneesY - rows.WaistY;
                }

                mod.Y = mod.X + midBodyDiff / height;

                if (mod.Y < 0)
                {
                    mod.Y = 0;
                }
            }

            if (itemsEndY <= rows.KneesY)
            {
                mod.Z = 0;
            }
            else if (y >= rows.KneesY)
            {
                mod.Z = 1.0f;
            }
            else
            {
                float lowerBodyDiff = itemsEndY - rows.KneesY;
                mod.Z = mod.Y + lowerBodyDiff / height;

                if (mod.Z < 0)
                {
                    mod.Z = 0;
                }
            }

            return mod;
        }

        /// <summary>
        /// Replaces <paramref name="buffer"/>[<paramref name="at"/>] — the whole frame (source rows 0..<paramref name="frameHeight"/>,
        /// trim-clipped) drawn plain with its untrimmed top at target row <paramref name="top"/> — with DrawCharacterSitted's
        /// sections, appended from <paramref name="at"/>. Section edges are rounded to whole pixels (Batcher2D draws them at
        /// fractional rows), so an edge can move by up to half a pixel.
        /// </summary>
        public static void Split(ref SpriteInstance[] buffer, ref int count, int at, int top, int frameHeight, Vector3 mod, bool flip)
        {
            SpriteInstance t = buffer[at];
            count = at;

            float h = frameHeight;
            float off = flip ? -8f : 8f;
            float h03 = h * mod.X, h06 = h * mod.Y, h09 = h * mod.Z;

            // DrawSittedSection: quad rows [c, d) sample rows [a, a + (h - a) * mod) — the uv offset shrinks the span the
            // fraction applies to — with the top edge shifted by sTop and the bottom edge by sBot
            if (mod.X != 0f)
            {
                Section(ref buffer, ref count, in t, top, 0f, h * mod.X, 0f, h03, off, off);
            }

            if (mod.Y != 0f)
            {
                Section(ref buffer, ref count, in t, top, h03, h03 + (h - h03) * mod.Y, h03, h06, off, 0f);
            }

            if (mod.Z != 0f)
            {
                Section(ref buffer, ref count, in t, top, h06, h06 + (h - h06) * mod.Z, h06, h09, 0f, 0f);
            }
        }

        // Rasterised rows of an edge at fractional row v (pixel centres at j + 0.5).
        private static int Row(float v) => (int)MathF.Ceiling(v - 0.5f);

        private static void Section(ref SpriteInstance[] buffer, ref int count, in SpriteInstance t, int top, float a, float b, float c, float d, float sTop, float sBot)
        {
            if (b <= a || d <= c)
            {
                return;
            }

            // the template's rows in untrimmed frame rows
            int iy0 = t.Y - top, iy1 = iy0 + t.Height;
            float a1 = Math.Max(a, iy0), b1 = Math.Min(b, iy1);

            if (b1 <= a1)
            {
                return;
            }

            float k = (d - c) / (b - a);
            float c1 = c + (a1 - a) * k, d1 = c + (b1 - a) * k;
            int sa = Math.Max(Row(a1), iy0), sb = Math.Min(Row(b1), iy1);
            int da = Row(c1), db = Row(d1);

            if (sb <= sa || db <= da)
            {
                return;
            }

            float fTop = (c1 - c) / (d - c), fBot = (d1 - c) / (d - c);
            int xBot = (int)MathF.Round(sTop + (sBot - sTop) * fBot);
            int shear = (int)MathF.Round(sTop + (sBot - sTop) * fTop) - xBot;
            int dstH = db - da, srcH = sb - sa;

            if (count >= buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            ref SpriteInstance inst = ref buffer[count++];
            inst = t;
            inst.X = t.X + xBot;
            inst.Y = top + da;
            uint flags = t.Flags;

            if (shear != 0 || srcH != dstH)
            {
                flags |= SpriteInstance.FlagDeform;
                inst.Corners = SpriteInstance.DeformCorners(t.Corners, shear, srcH);
            }

            inst.Whf = SpriteInstance.PackWhf(t.Width, dstH, flags);
            inst.Uv = (t.Uv & 0xFFFF) | ((t.Uv >> 16) + (uint)(sa - iy0)) << 16;
        }
    }
}
