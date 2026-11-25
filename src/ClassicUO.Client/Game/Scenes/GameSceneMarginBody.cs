// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        // view_margin_body (vm-body): view_margin keeps every mobile whose anchor is within 160 px beside or 260 px below the
        // view, whatever its body, and none above it. With the switch on, the walk visits the dynamic tiles out to the caps
        // (ViewMargin.BodyRect: 320 beside, 300 below, a 200 px top band) and keeps a mobile whose anchor is outside the view
        // only when its body's, mount's and equipment's reach (BodyReach) meets the view. _vmBodyTest: the decision is
        // computed this frame (the switch, FrameStats counters or CUO_VIEW_MARGIN_BODY_CHECK).
        private bool _vmBody, _vmBodyTest;
        private FillDiamond.Bounds _vmBodyBounds;
        private int _vmBodyBudget, _vmBodySkipped;

        private void BeginMarginBody()
        {
            _vmBody = _vmSprites && ViewMargin.BodyEnabled;
            _vmBodyTest = _vmSprites && (_vmBody || (FrameStats.On && ViewMargin.WhatIf) || ViewMargin.BodyCheck);
            _vmBodyBudget = BodyReach.BudgetPerFrame;

            if (_vmBody)
            {
                ViewMargin.BodyRect(_viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y, out float minX, out float minY,
                    out float maxX, out float maxY);
                _vmBodyBounds = FillDiamond.Compute(_offset.X, _offset.Y, new Vector2(minX, minY), new Vector2(maxX, maxY), false, default, 0);
            }
        }

        /// <summary>view_margin_body: the chunk's walked tiles with the static rectangle's <paramref name="staticMask"/>: the
        /// dynamic tiles out to the caps join it.</summary>
        private ulong MarginBodyTiles(Chunk chunk, int chunkX, int chunkY, ulong inWalk, ulong staticMask)
        {
            if (!_vmBody)
            {
                return inWalk & staticMask;
            }

            ulong dynamic = chunk.Dynamic?.Cells ?? 0;

            return ViewMargin.BodyTiles(inWalk, staticMask, dynamic == 0 ? 0 : FillDiamond.ChunkMask(chunkX, chunkY, in _vmBodyBounds), dynamic);
        }

        /// <summary>view_margin_body: whether the walk leaves out <paramref name="mobile"/> (anchor at ax, ay), given the fixed
        /// margin's answer <paramref name="fixedOutside"/> (the walk's rectangle).</summary>
        private bool MarginBodySkips(Mobile mobile, int ax, int ay, bool fixedOutside)
        {
            if (ViewMargin.InView(ax, ay, _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y))
            {
                return fixedOutside;
            }

            MobileReach(mobile, ref _vmBodyBudget, out int left, out int right, out int up, out int down);
            bool skips = !BodyReach.Meets(ax, ay, left, right, up, down, _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y);

            if (skips && !fixedOutside)
            {
                _vmBodySkipped++;
            }

            return _vmBody ? skips : fixedOutside;
        }

        /// <summary>A mobile's capped reach past its anchor box from its record (AnimationState, EquipmentSlots, Steps): the
        /// fixed margin when it has none or its body isn't computed yet.</summary>
        private static void MobileReach(Mobile mobile, ref int budget, out int left, out int right, out int up, out int down)
        {
            AnimationsLoader anims = Client.Game.UO.FileManager.Animations;

            if (!MobileEntityLookup.TryGet(mobile, out Friflo.Engine.ECS.Entity e))
            {
                BodyReach.Compose(BodyReach.Default, true, false, default, 0, 0, 0, 0, out left, out right, out up, out down);

                return;
            }

            EcsRow row = EcsRead.Row(e);

            if (row.IsNull || !row.Has<EquipmentSlots>() || (row.Get<AnimationState>().Flags & AnimStateFlags.Visible) == 0)
            {
                BodyReach.Compose(BodyReach.Default, true, false, default, 0, 0, 0, 0, out left, out right, out up, out down);

                return;
            }

            ref readonly AnimationState s = ref row.Get<AnimationState>();
            ref readonly EquipmentSlots eq = ref row.Get<EquipmentSlots>();
            BodyReach.TryGet(anims, s.Body, ref budget, out BodyReach.Reach rider);

            for (int i = 0; i < eq.Count; i++)
            {
                ushort body = eq.Slots[i].DrawBody;

                if (body != 0)
                {
                    BodyReach.TryGet(anims, body, ref budget, out BodyReach.Reach piece);
                    rider = BodyReach.MergeEquipment(in rider, in piece);
                }
            }

            bool mounted = (s.Flags & AnimStateFlags.MountDrawn) != 0;
            BodyReach.Reach mount = default;
            int dx = 0, dy = 0;

            if (mounted)
            {
                BodyReach.TryGet(anims, s.MountBody, ref budget, out mount);
                dy = s.MountOffsetY;
            }
            else if ((s.Flags & AnimStateFlags.Sitting) != 0)
            {
                dx = s.SitX;
                dy = s.SitY;
            }

            float mx = 0, my = 0;

            if (row.Has<Steps>())
            {
                ref readonly Steps st = ref row.Get<Steps>();
                mx = st.OffsetX;
                my = st.OffsetY - st.OffsetZ;
            }

            BodyReach.Compose(in rider, (s.Flags & AnimStateFlags.Shadow) != 0, mounted, in mount, dx, dy, mx, my, out left, out right, out up, out down);
        }

        private void FlushMarginBody()
        {
            if (FrameStats.On && _vmBodyTest)
            {
                FrameStats.Add(FrameCounter.VmBodyMobilesSkipped, _vmBodySkipped);
            }

#if CUO_DEVTOOLS
            if (ViewMargin.BodyCheck && _vmBodyTest)
            {
                CheckMarginBody();
            }
#endif
            _vmBodySkipped = 0;
        }

#if CUO_DEVTOOLS
        private long _vbFrames, _vbChecked, _vbKept, _vbFixedOnly, _vbFixedMiss, _vbBeyondCap, _vbMismatch, _vbSkipped;
        private uint _vbSummaryAt;
        private int _vbLogged;

        // CUO_VIEW_MARGIN_BODY_CHECK (view_margin_body, both switch states): every mobile of the world whose anchor is outside
        // the view is tested as the switch tests it (BodyReach over all frames, capped) and compared with two references: the
        // fixed margin (anchor within 160 px beside or 260 px below the view) and the true reach, its current frame's box
        // (body, shadow, mount and equipment as MobileSpriteExtract.Emit places them, from the pack index). A mobile the
        // per-body test leaves out whose current frame meets the visible world (no slack) is a mismatch, unless reaching it
        // needs more than the caps (accepted pop-in, counted apart). Also counted: mobiles the fixed margin keeps and the
        // per-body test leaves out (the saving) and visible mobiles the fixed margin leaves out (what the switch adds).
        private void CheckMarginBody()
        {
            Point vMin = Camera.ScreenToWorld(Point.Zero);
            Point vMax = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
            int offX = _offset.X, offY = _offset.Y;
            int budget = int.MaxValue;
            _vbFrames++;
            _vbSkipped += _vmBodySkipped;

            foreach (Mobile m in _world.Mobiles.Values)
            {
                if (m.IsDestroyed)
                {
                    continue;
                }

                ViewMargin.Anchor(m.X, m.Y, m.Z, offX, offY, out int ax, out int ay);

                if (ViewMargin.InView(ax, ay, _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y))
                {
                    continue;
                }

                _vbChecked++;
                bool fixedKeep = ax >= _viewMinPixel.X - ViewMargin.MobileSide && ax <= _viewMaxPixel.X + ViewMargin.MobileSide &&
                                 ay >= _viewMinPixel.Y && ay <= _viewMaxPixel.Y + ViewMargin.MobileUp;
                MobileReach(m, ref budget, out int left, out int right, out int up, out int down);
                bool bodyKeep = BodyReach.Meets(ax, ay, left, right, up, down, _viewMinPixel.X, _viewMinPixel.Y, _viewMaxPixel.X, _viewMaxPixel.Y);

                if (bodyKeep)
                {
                    _vbKept++;
                }

                if (!TrueMobileBox(m, ax, ay, out int x0, out int y0, out int x1, out int y1))
                {
                    continue;
                }

                bool visible = x1 >= vMin.X && x0 <= vMax.X && y1 >= vMin.Y && y0 <= vMax.Y;

                if (fixedKeep && !bodyKeep)
                {
                    _vbFixedOnly++;
                }

                if (!fixedKeep && visible)
                {
                    _vbFixedMiss++;
                }

                if (bodyKeep || !visible)
                {
                    continue;
                }

                if (ax - x0 > BodyReach.CapSide || x1 - ax - 44 > BodyReach.CapSide || ay - y0 > BodyReach.CapUp || y1 - ay - 44 > BodyReach.CapTop)
                {
                    _vbBeyondCap++;

                    continue;
                }

                _vbMismatch++;

                if (_vbLogged < 30)
                {
                    _vbLogged++;
                    Log.Warn($"[view-margin-body-check] mobile 0x{m.Serial:X8} body {m.Graphic} at {m.X},{m.Y},{m.Z} left out by its reach " +
                             $"(left {left} right {right} up {up} down {down}, view_margin_body {(_vmBody ? "on" : "off")}" +
                             $"{(fixedKeep ? ", the fixed margin keeps it" : "")}) but its frame {x0},{y0}..{x1},{y1} meets the view " +
                             $"{vMin.X},{vMin.Y}..{vMax.X},{vMax.Y} (anchor {ax},{ay})");
                }
            }

            if (Time.Ticks >= _vbSummaryAt)
            {
                _vbSummaryAt = Time.Ticks + 5000;
                Log.Info($"[view-margin-body-check] view_margin_body {(_vmBody ? "on" : "off")}, {_vbFrames} frames: margin mobiles checked {_vbChecked}, " +
                         $"kept by their reach {_vbKept}, walk {(_vmBody ? "left out" : "would leave out")} {_vbSkipped}, fixed margin keeps but reach " +
                         $"leaves out {_vbFixedOnly}, visible but outside the fixed margin {_vbFixedMiss}, beyond the caps (accepted pop-in) {_vbBeyondCap}, " +
                         $"bodies computed {BodyReach.Computed}, mismatches {_vbMismatch} (visible, left out within the caps)");
            }
        }

        // the mobile's current frame box (all layers) as MobileSpriteExtract.Emit and Layer place them; false: nothing drawn
        private static bool TrueMobileBox(Mobile m, int ax, int ay, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = int.MaxValue;
            x1 = y1 = int.MinValue;

            if (!MobileEntityLookup.TryGet(m, out Friflo.Engine.ECS.Entity e))
            {
                return false;
            }

            EcsRow row = EcsRead.Row(e);

            if (row.IsNull || !row.Has<EquipmentSlots>() || !row.Has<Steps>())
            {
                return false;
            }

            ref readonly AnimationState s = ref row.Get<AnimationState>();

            if ((s.Flags & AnimStateFlags.Visible) == 0)
            {
                return false;
            }

            ref readonly EquipmentSlots eq = ref row.Get<EquipmentSlots>();
            ref readonly Steps st = ref row.Get<Steps>();
            AnimationsLoader anims = Client.Game.UO.FileManager.Animations;
            int drawX = ax + 22 + (int)st.OffsetX;
            int drawY = ay + 19 + (int)(st.OffsetY - st.OffsetZ);
            bool mirror = (s.Flags & AnimStateFlags.Mirror) != 0;
            bool shadow = (s.Flags & AnimStateFlags.Shadow) != 0;

            if ((s.Flags & AnimStateFlags.MountDrawn) != 0)
            {
                if (shadow)
                {
                    LayerBox(anims, s.Body, s.Group, s.Dir, s.Frame, false, true, drawX, drawY + 10, mirror, ref x0, ref y0, ref x1, ref y1);
                    LayerBox(anims, s.MountBody, s.MountGroup, s.Dir, s.Frame, false, true, drawX, drawY, mirror, ref x0, ref y0, ref x1, ref y1);
                }

                LayerBox(anims, s.MountBody, s.MountGroup, s.Dir, s.Frame, false, false, drawX, drawY, mirror, ref x0, ref y0, ref x1, ref y1);
                drawY += s.MountOffsetY;
            }
            else if ((s.Flags & AnimStateFlags.Sitting) != 0)
            {
                drawX += s.SitX;
                drawY += s.SitY;
            }
            else if (shadow)
            {
                LayerBox(anims, s.Body, s.Group, s.Dir, s.Frame, false, true, drawX, drawY, mirror, ref x0, ref y0, ref x1, ref y1);
            }

            LayerBox(anims, s.Body, s.Group, s.Dir, s.Frame, false, false, drawX, drawY, mirror, ref x0, ref y0, ref x1, ref y1);

            for (int i = 0; i < eq.Count; i++)
            {
                ref readonly EquipSlot slot = ref eq.Slots[i];
                LayerBox(anims, slot.DrawBody, slot.Group == 0xFF ? s.Group : slot.Group, s.Dir, s.Frame, true, false, drawX, drawY, mirror,
                    ref x0, ref y0, ref x1, ref y1);
            }

            return x1 >= x0;
        }

        private static void LayerBox(AnimationsLoader anims, ushort id, byte group, byte dir, byte frame, bool isEquip, bool isShadow, int x, int y,
            bool mirror, ref int x0, ref int y0, ref int x1, ref int y1)
        {
            if (id >= anims.BodyCount || !MobileSpriteExtract.TryGetFrames(anims, id, group, dir, isEquip, false, out uint first,
                    out int count, out _))
            {
                return;
            }

            ref readonly Uo.Pack.AnimFrameEntry fe = ref anims.GetFrame(first + (uint)Math.Min(frame, count - 1));
            ref readonly Uo.Pack.SpriteEntry sp = ref fe.Sprite;
            int w = sp.OrigW, h = sp.OrigH;

            if (!sp.Present || w == 0 || h == 0 || sp.W == 0 || sp.H == 0)
            {
                return;
            }

            int fx = x - (mirror ? w - fe.CenterX : fe.CenterX);
            int fy = y - (h + fe.CenterY);
            int vx0 = mirror ? fx + w - sp.TrimX - sp.W : fx + sp.TrimX;
            int vy0 = fy + sp.TrimY;
            int vx1 = vx0 + sp.W, vy1 = vy0 + sp.H;

            if (isShadow)
            {
                // sheared right by half the full frame's height at its top row, squashed into its lower half, 10 px up
                vx1 += (h - sp.TrimY + 1) >> 1;
                vy0 = fy + ((h + sp.TrimY) >> 1) - 10;
                vy1 = fy + ((h + sp.TrimY + sp.H + 1) >> 1) - 10;
            }

            x0 = Math.Min(x0, vx0);
            y0 = Math.Min(y0, vy0);
            x1 = Math.Max(x1, vx1);
            y1 = Math.Max(y1, vy1);
        }
#endif
    }
}
