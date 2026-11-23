// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using Uo.Pack;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// view_margin_body (vm-body): how far a body's frames reach past its anchor's 44 x 44 box, from the pack's animation
    /// index alone (each frame's trim rect and centre, no pixel decode): beside it (<see cref="Reach.Side"/>, either facing),
    /// beside it with the shadow (<see cref="Reach.SideShadow"/>: the shadow is sheared right by half the frame's height),
    /// above it (<see cref="Reach.Up"/>) and below it (<see cref="Reach.Down"/>: a mobile whose anchor is above the view can
    /// reach into it). A body's entry is computed on the main thread the first time the walk needs it, at most
    /// <see cref="BudgetPerFrame"/> bodies a frame (a few microseconds each); until then, and for bodies the pack does not
    /// have, the fixed margin's values (<see cref="Default"/>) stand in. Bodies never seen cost nothing but their 8 bytes.
    /// The walk tests each mobile against the view grown by its body's, mount's and equipment's reach, capped
    /// (<see cref="CapSide"/>, <see cref="CapUp"/>, <see cref="CapTop"/>): beyond the caps a body pops in at the edge
    /// (owner 2026-11-23: accepted for extreme and custom oversized art, no overhead for it).
    /// </summary>
    internal static class BodyReach
    {
        /// <summary>The caps on a mobile's reach (beside, above, below its anchor box).</summary>
        public const int CapSide = 320, CapUp = 300, CapTop = 200;

        /// <summary>Bodies computed per frame at most; the rest wait a frame with <see cref="Default"/>.</summary>
        public const int BudgetPerFrame = 4;

        /// <summary>A body not computed yet or unknown: the fixed margin (ViewMargin.MobileSide/MobileUp) plus the top band.</summary>
        public static readonly Reach Default = new Reach(ViewMargin.MobileSide, ViewMargin.MobileSide, ViewMargin.MobileUp, CapTop);

        // the drawn frame's top is anchor + 22 - 3 - (h + centerY) (MobileSpriteExtract.Emit: drawY = posY - 3 + 22)
        private const int DRAW_Y = 19;
        private const ulong COMPUTED = 1UL << 63;
        private const int FIELD = 0x7FFF;

        private static ulong[] _table;
        private static AnimationsLoader _owner;
        private static int _computed;

        /// <summary>Entries computed since the table was made (CUO_VIEW_MARGIN_BODY_CHECK summary).</summary>
        public static int Computed => _computed;

        public readonly struct Reach
        {
            public readonly int Side, SideShadow, Up, Down;

            public Reach(int side, int sideShadow, int up, int down)
            {
                Side = side;
                SideShadow = sideShadow;
                Up = up;
                Down = down;
            }

            public override string ToString() => $"side {Side} shadow {SideShadow} up {Up} down {Down}";
        }

        /// <summary>The body's reach over all its actions and stored directions (the extract draws UO directions 3..7,
        /// mirrored for the others, so the side reach is taken for both facings). <paramref name="trim"/> false: the
        /// untrimmed frame (the bodyreach.py cross-check). Aliases are not followed (the caller resolves).</summary>
        public static Reach Compute(AnimationsLoader anims, ushort body, bool trim = true)
        {
            if (body >= anims.BodyCount)
            {
                return Default;
            }

            ref readonly AnimBodyEntry entry = ref anims.GetBody(body);
            int side = 0, shadow = 0, up = 0, down = 0;

            for (int a = 0; a < entry.ActionCount; a++)
            {
                for (int d = 3; d < AnimConst.Directions; d++)
                {
                    if (!anims.TryGetDirection(body, a, d, out AnimDirEntry dir))
                    {
                        continue;
                    }

                    for (uint f = 0; f < dir.FrameCount; f++)
                    {
                        ref readonly AnimFrameEntry fe = ref anims.GetFrame(dir.FirstFrame + f);
                        ref readonly SpriteEntry sp = ref fe.Sprite;
                        int ow = sp.OrigW, oh = sp.OrigH;

                        if (!sp.Present || ow == 0 || oh == 0)
                        {
                            continue;
                        }

                        int tx = 0, ty = 0, w = ow, h = oh;

                        if (trim)
                        {
                            if (sp.W == 0 || sp.H == 0)
                            {
                                continue;
                            }

                            tx = sp.TrimX;
                            ty = sp.TrimY;
                            w = sp.W;
                            h = sp.H;
                        }

                        // the visible columns: unmirrored from anchor + 22 - centerX + tx, mirrored from
                        // anchor + 22 + centerX - tx - w; the reach past the box's left (anchor) and right (anchor + 44)
                        int a0 = fe.CenterX - tx - 22, a1 = tx + w - fe.CenterX - 22;
                        int s = Math.Max(a0, a1);
                        int top = oh + fe.CenterY - ty - DRAW_Y;
                        int below = ty + h - oh - fe.CenterY + DRAW_Y - 44;
                        // the shadow (Batcher2D.DrawShadow, DynamicSprite's shadow mode): the full frame sheared right by
                        // half its height at its top row and squashed to its lower half (a mounted body's 10 px lower)
                        int shadowBelow = ((oh + ty + h + 1) >> 1) - oh - fe.CenterY + DRAW_Y - 44;

                        side = Math.Max(side, s);
                        shadow = Math.Max(shadow, s + ((oh - ty + 1) >> 1));
                        up = Math.Max(up, top);
                        down = Math.Max(down, Math.Max(below, shadowBelow));
                    }
                }
            }

            return new Reach(side, shadow, up, down);
        }

        /// <summary>The reach of <paramref name="body"/> as drawn (its Body.def alias followed): the cached entry, else
        /// computed now while <paramref name="budget"/> lasts, else false (<see cref="Default"/> stands in).</summary>
        public static bool TryGet(AnimationsLoader anims, ushort body, ref int budget, out Reach reach)
        {
            if (anims == null || body >= anims.BodyCount)
            {
                reach = Default;

                return false;
            }

            if (!ReferenceEquals(_owner, anims) || _table == null || _table.Length != anims.BodyCount)
            {
                _table = new ulong[anims.BodyCount];
                _owner = anims;
                _computed = 0;
            }

            ulong packed = _table[body];

            if (packed != 0)
            {
                reach = Unpack(packed);

                return true;
            }

            if (budget <= 0)
            {
                reach = Default;

                return false;
            }

            budget--;
            ushort drawn = anims.ResolveBody(body, false, out _);
            // a body without frames: the server may still send it; the fixed margin stands in (unknown body)
            reach = anims.GetBody(drawn).HasFrames ? Compute(anims, drawn) : Default;
            _table[body] = Pack(in reach);
            _computed++;

            return true;
        }

        /// <summary>Tests: forget the table.</summary>
        internal static void ResetForTests()
        {
            _table = null;
            _owner = null;
            _computed = 0;
        }

        private static ulong Pack(in Reach r) =>
            COMPUTED | (ulong)Field(r.Side) | (ulong)Field(r.SideShadow) << 15 | (ulong)Field(r.Up) << 30 | (ulong)Field(r.Down) << 45;

        private static Reach Unpack(ulong p) =>
            new Reach((int)(p & FIELD), (int)(p >> 15 & FIELD), (int)(p >> 30 & FIELD), (int)(p >> 45 & FIELD));

        private static int Field(int v) => Math.Clamp(v, 0, FIELD);

        /// <summary>
        /// One mobile's reach past its anchor box (left, right, up, down), capped: the body (with its shadow when the
        /// mobile has one) and its equipment, moved by the rider's seat (<paramref name="riderDx"/>, <paramref name="riderDy"/>:
        /// the mount offset or the sitting offset), and the mount where it has one; all moved by the walk offset
        /// (<paramref name="moveX"/>, <paramref name="moveY"/>: Steps.OffsetX, OffsetY - OffsetZ).
        /// </summary>
        public static void Compose(in Reach rider, bool shadow, bool mounted, in Reach mount, int riderDx, int riderDy, float moveX, float moveY,
            out int left, out int right, out int up, out int down)
        {
            left = rider.Side - riderDx;
            right = (shadow ? rider.SideShadow : rider.Side) + riderDx;
            up = rider.Up - riderDy;
            down = rider.Down + riderDy;

            if (mounted)
            {
                left = Math.Max(left, mount.Side);
                right = Math.Max(right, shadow ? mount.SideShadow : mount.Side);
                up = Math.Max(up, mount.Up);
                down = Math.Max(down, mount.Down);
            }

            int mx = (int)MathF.Round(moveX), my = (int)MathF.Round(moveY);
            left = Math.Clamp(left - mx, 0, CapSide);
            right = Math.Clamp(right + mx, 0, CapSide);
            up = Math.Clamp(up - my, 0, CapUp);
            down = Math.Clamp(down + my, 0, CapTop);
        }

        /// <summary>The rider's layers merged: the body and each piece of equipment (no shadow on equipment).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reach MergeEquipment(in Reach rider, in Reach equipment) =>
            new Reach(Math.Max(rider.Side, equipment.Side), Math.Max(rider.SideShadow, equipment.Side),
                Math.Max(rider.Up, equipment.Up), Math.Max(rider.Down, equipment.Down));

        /// <summary>Whether a mobile whose anchor box is at (ax, ay) and whose frames reach (left, right, up, down) past it
        /// can meet the view rectangle (vl..vb, the walk's own, its tile of slack included).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Meets(int ax, int ay, int left, int right, int up, int down, float vl, float vt, float vr, float vb)
            => ax + 44 + right >= vl && ax - left <= vr && ay + 44 + down >= vt && ay - up <= vb;
    }
}
