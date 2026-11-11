// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Managers
{
    internal sealed class HealthLinesManager
    {
        private const int BAR_WIDTH = 34; //28;
        private const int BAR_HEIGHT = 8;
        private const int BAR_WIDTH_HALF = BAR_WIDTH >> 1;
        private const int BAR_HEIGHT_HALF = BAR_HEIGHT >> 1;

        const ushort BACKGROUND_GRAPHIC = 0x1068;
        const ushort HP_GRAPHIC = 0x1069;

        private readonly World _world;

        // ShowMobilesHP off: only the target mobiles can draw (the hits text needs ShowMobilesHP; the bar needs it or a
        // target), so the walk visits those instead of every mobile. CUO_HEALTH_LINES_CHECK runs the full walk without
        // drawing and compares what it would draw.
        private readonly Mobile[] _targets = new Mobile[4];
        private readonly Mobile[] _drawn = new Mobile[4];
        private readonly List<Mobile> _checkDrawn = new List<Mobile>();

        private Camera _camera;
        private int _mode, _showWhen;
        private bool _useNewTargetSystem, _isEnabled;

        public HealthLinesManager(World world) { _world = world; }

        public bool IsEnabled =>
            ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.ShowMobilesHP;

#if CUO_DEVTOOLS
        // CLEANUP(release): the checker
        private static readonly bool s_check = Environment.GetEnvironmentVariable("CUO_HEALTH_LINES_CHECK") == "1";
#else
        private const bool s_check = false;
#endif

        /// <summary>Tests: the checker for this manager (null: <c>CUO_HEALTH_LINES_CHECK=1</c>).</summary>
        internal bool? CheckOverride;

        /// <summary>Checker counts: frames compared, frames whose draws differed, frames that drew a line.</summary>
        public long Checks, CheckMismatches, CheckDrawFrames;

        /// <summary>Tests: sees each mobile the walk visits, in order (null in the client).</summary>
        internal Action<Mobile> VisitObserver;

        /// <summary>Tests: sees each mobile that draws (its hits text or bar), in order (null in the client).</summary>
        internal Action<Mobile> DrawObserver;

        /// <summary>Tests: reports through <see cref="DrawObserver"/> and draws nothing (no batcher or textures).</summary>
        internal bool DryRun;

        public void Draw(UltimaBatcher2D batcher, float layerDepth)
        {
            int mode = ProfileManager.CurrentProfile.MobileHPType;

            if (mode < 0)
            {
                return;
            }

            Configure(
                Client.Game.Scene.Camera,
                mode,
                ProfileManager.CurrentProfile.MobileHPShowWhen,
                ProfileManager.CurrentProfile.UseNewTargetSystem,
                IsEnabled
            );

            DrawMobiles(batcher, layerDepth);
        }

        /// <summary>The profile values <see cref="Draw"/> reads (tests set them directly).</summary>
        internal void Configure(Camera camera, int mode, int showWhen, bool useNewTargetSystem, bool isEnabled)
        {
            _camera = camera;
            _mode = mode;
            _showWhen = showWhen;
            _useNewTargetSystem = useNewTargetSystem;
            _isEnabled = isEnabled;
        }

        internal void DrawMobiles(UltimaBatcher2D batcher, float layerDepth)
        {
            if (_isEnabled)
            {
                foreach (Mobile mobile in _world.Mobiles.Values)
                {
                    VisitObserver?.Invoke(mobile);
                    DrawMobile(batcher, mobile, layerDepth, true);
                }

                return;
            }

            int count = CollectTargets();
            int drawn = 0;

            for (int i = 0; i < count; i++)
            {
                VisitObserver?.Invoke(_targets[i]);

                if (DrawMobile(batcher, _targets[i], layerDepth, true))
                {
                    _drawn[drawn++] = _targets[i];
                }
            }

            if (CheckOverride ?? s_check)
            {
                CheckTargetWalk(drawn);
            }

            Array.Clear(_targets, 0, count);
            Array.Clear(_drawn, 0, drawn);
        }

        // the distinct held mobiles under the four target serials, in Mobiles.Values order (MobileTable's slot order, Ordinal)
        private int CollectTargets()
        {
            TargetManager tm = _world.TargetManager;
            int count = AddTarget(tm.LastTargetInfo.Serial, 0);
            count = AddTarget(tm.LastAttack, count);
            count = AddTarget(tm.SelectedTarget, count);
            count = AddTarget(tm.NewTargetSystemSerial, count);

            for (int i = 1; i < count; i++)
            {
                Mobile m = _targets[i];
                int j = i - 1;

                while (j >= 0 && _targets[j].Ordinal > m.Ordinal)
                {
                    _targets[j + 1] = _targets[j];
                    j--;
                }

                _targets[j + 1] = m;
            }

            return count;
        }

        private int AddTarget(uint serial, int count)
        {
            Mobile m = _world.FindMobile(serial);

            if (m == null)
            {
                return count;
            }

            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(_targets[i], m))
                {
                    return count;
                }
            }

            _targets[count] = m;

            return count + 1;
        }

        // CUO_HEALTH_LINES_CHECK: the full walk, drawing nothing, must draw the same mobiles in the same order
        private void CheckTargetWalk(int drawn)
        {
            Checks++;
            CheckDrawFrames += drawn != 0 ? 1 : 0;
            _checkDrawn.Clear();

            foreach (Mobile mobile in _world.Mobiles.Values)
            {
                if (DrawMobile(null, mobile, 0f, false))
                {
                    _checkDrawn.Add(mobile);
                }
            }

            bool same = _checkDrawn.Count == drawn;

            for (int i = 0; same && i < drawn; i++)
            {
                same = ReferenceEquals(_checkDrawn[i], _drawn[i]);
            }

            if (!same)
            {
                CheckMismatches++;

                if (CheckMismatches <= 10)
                {
                    var text = new StringBuilder();
                    text.Append($"[health-lines-check] full walk draws {_checkDrawn.Count}, target walk {drawn}:");

                    foreach (Mobile m in _checkDrawn)
                    {
                        text.Append($" 0x{m.Serial:X8}");
                    }

                    text.Append(" |");

                    for (int i = 0; i < drawn; i++)
                    {
                        text.Append($" 0x{_drawn[i].Serial:X8}");
                    }

                    Log.Warn(text.ToString());
                }
            }

            if (Checks % 500 == 1)
            {
                Log.Info($"[health-lines-check] {Checks} frames, {CheckMismatches} mismatches, {CheckDrawFrames} drawing; mobiles {_world.Mobiles.Count}");
            }
        }

        /// <summary>One mobile's hits text and bar (the body of the old every-mobile loop). <paramref name="emit"/> false draws
        /// nothing (the checker). Returns whether it drew, or would draw, anything.</summary>
        private bool DrawMobile(UltimaBatcher2D batcher, Mobile mobile, float layerDepth, bool emit)
        {
            if (mobile.IsDestroyed)
            {
                return false;
            }

            var camera = _camera;
            int mode = _mode;
            int showWhen = _showWhen;
            bool isEnabled = _isEnabled;
            bool drew = false;

            var newTargSystem = false;
            var forceDraw = false;
            var passive = mobile.Serial != _world.Player.Serial;

            if (_world.TargetManager.LastTargetInfo.Serial == mobile ||
                _world.TargetManager.LastAttack == mobile ||
                _world.TargetManager.SelectedTarget == mobile ||
                _world.TargetManager.NewTargetSystemSerial == mobile)
            {
                newTargSystem = _useNewTargetSystem && _world.TargetManager.NewTargetSystemSerial == mobile;
                passive = false;
                forceDraw = true;
            }

            int current = mobile.Hits;
            int max = mobile.HitsMax;

            if (!newTargSystem)
            {
                if (max == 0)
                {
                    return false;
                }

                if (showWhen == 1 && current == max)
                {
                    return false;
                }
            }

            Point p = mobile.RealScreenPosition;
            Vector3 offset = mobile.Offset;
            p.X += (int)offset.X + 22 + 5;
            p.Y += (int)(offset.Y - offset.Z) + 22 + 5;
            var offsetY = 0;

            if (isEnabled)
            {
                if (mode != 1 && !mobile.IsDead)
                {
                    if (showWhen == 2 && current != max || showWhen <= 1)
                    {
                        if (mobile.HitsPercentage != 0)
                        {
                            Client.Game.UO.Animations.GetAnimationDimensions(
                                mobile.AnimIndex,
                                mobile.GetGraphicForAnimation(),
                                /*(byte) m.GetDirectionForAnimation()*/
                                0,
                                /*Mobile.GetGroupForAnimation(m, isParent:true)*/
                                0,
                                mobile.IsMounted,
                                /*(byte) m.AnimIndex*/
                                0,
                                out int centerX,
                                out int centerY,
                                out int width,
                                out int height
                            );

                            Point p1 = p;
                            p1.Y -= height + centerY + 8 + 22;

                            if (mobile.IsGargoyle && mobile.IsFlying)
                            {
                                p1.Y -= 22;
                            }
                            else if (!mobile.IsMounted)
                            {
                                p1.Y += 22;
                            }

                            p1 = camera.WorldToScreen(p1, true);
                            p1.X -= (mobile.HitsTexture.Width >> 1) + 5;
                            p1.Y -= mobile.HitsTexture.Height;

                            if (mobile.ObjectHandlesStatus == ObjectHandlesStatus.DISPLAYING)
                            {
                                int ohHeight = Constants.OBJECT_HANDLES_GUMP_HEIGHT
                                    + (ProfileManager.CurrentProfile.NameOverheadShowHpBar
                                        ? Constants.OBJECT_HANDLES_HP_BAR_HEIGHT + 1 : 0);
                                p1.Y -= ohHeight + 5;
                                offsetY += ohHeight + 5;
                            }

                            if (
                                !(
                                    p1.X < 0
                                    || p1.X > camera.Bounds.Width - mobile.HitsTexture.Width
                                    || p1.Y < 0
                                    || p1.Y > camera.Bounds.Height
                                )
                            )
                            {
                                drew = true;

                                if (emit)
                                {
                                    DrawObserver?.Invoke(mobile);

                                    if (!DryRun)
                                    {
                                        mobile.HitsTexture.Draw(batcher, p1.X, p1.Y, layerDepth);
                                    }
                                }
                            }

                            if (newTargSystem)
                            {
                                offsetY += mobile.HitsTexture.Height;
                            }
                        }
                    }
                }
            }

            p.X -= 5;
            p = camera.WorldToScreen(p, true);

            p.X -= BAR_WIDTH_HALF;
            p.Y -= BAR_HEIGHT_HALF;

            if (p.X < 0 || p.X > camera.Bounds.Width - BAR_WIDTH)
            {
                return drew;
            }

            if (p.Y < 0 || p.Y > camera.Bounds.Height - BAR_HEIGHT)
            {
                return drew;
            }

            if ((isEnabled && mode >= 1) || newTargSystem || forceDraw)
            {
                if (emit)
                {
                    DrawObserver?.Invoke(mobile);

                    if (!DryRun)
                    {
                        DrawHealthLine(batcher, mobile, p.X, p.Y, offsetY, passive, newTargSystem, layerDepth);
                    }
                }

                return true;
            }

            return drew;
        }

        private void DrawHealthLine(
            UltimaBatcher2D batcher,
            Entity entity,
            int x,
            int y,
            int offsetY,
            bool passive,
            bool newTargetSystem,
            float layerDepth
        )
        {
            if (entity == null)
            {
                return;
            }

            int per = BAR_WIDTH * entity.HitsPercentage / 100;

            Mobile mobile = entity as Mobile;

            float alpha = passive && !newTargetSystem ? 0.5f : 1.0f;
            ushort hue =
                mobile != null
                    ? Notoriety.GetHue(mobile.NotorietyFlag)
                    : Notoriety.GetHue(NotorietyFlag.Gray);

            //Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, false, alpha);
            Vector3 hueVecZero = ShaderHueTranslator.GetHueVector(0, false, alpha);
            Vector3 hueVecNoto = ShaderHueTranslator.GetHueVector(hue, false, alpha);

            if (mobile == null)
            {
                y += 22;
            }

            const int MULTIPLER = 1;

            if (newTargetSystem && mobile != null && mobile.Serial != _world.Player.Serial)
            {
                Client.Game.UO.Animations.GetAnimationDimensions(
                    mobile.AnimIndex,
                    mobile.GetGraphicForAnimation(),
                    (byte) mobile.GetDirectionForAnimation(),
                    Mobile.GetGroupForAnimation(mobile, isParent: true),
                    mobile.IsMounted,
                    0, //mobile.AnimIndex,
                    out int centerX,
                    out int centerY,
                    out int width,
                    out int height
                );

                uint topGump;
                uint bottomGump;
                //uint gumpHue = 0x7570;
                uint gumpHue = 0x7572; // gray

                if (mobile != null)
                {
                    if (mobile.NotorietyFlag == NotorietyFlag.Innocent)
                        gumpHue = 0x7570; // blue

                    else if (mobile.NotorietyFlag == NotorietyFlag.Ally)
                        gumpHue = 0x7571; // green

                    else if (mobile.NotorietyFlag == NotorietyFlag.Criminal || mobile.NotorietyFlag == NotorietyFlag.Gray)
                        gumpHue = 0x7572; // grey

                    else if (mobile.NotorietyFlag == NotorietyFlag.Enemy)
                        gumpHue = 0x7573; // orange

                    if (mobile.NotorietyFlag == NotorietyFlag.Invulnerable)
                        gumpHue = 0x7575; // yellow

                    else if (mobile.NotorietyFlag == NotorietyFlag.Murderer)
                        gumpHue = 0x7577; // red
                }

                if (width >= 80)
                {
                    topGump = 0x756D;
                    bottomGump = 0x756A;
                }
                else if (width >= 40)
                {
                    topGump = 0x756E;
                    bottomGump = 0x756B;
                }
                else
                {
                    topGump = 0x756F;
                    bottomGump = 0x756C;
                }

                ref readonly var hueGumpInfo = ref Client.Game.UO.Gumps.GetGump(gumpHue);
                var targetX = x + BAR_WIDTH_HALF - hueGumpInfo.UV.Width / 2f;
                var topTargetY = height + centerY + 8 + 22 + offsetY;

                ref readonly var newTargGumpInfo = ref Client.Game.UO.Gumps.GetGump(topGump);
                if (newTargGumpInfo.Texture != null)
                    batcher.Draw(
                        newTargGumpInfo.Texture,
                        new Vector2(targetX, y - topTargetY),
                        newTargGumpInfo.UV,
                        hueVecZero,
                        layerDepth
                    );

                if (hueGumpInfo.Texture != null)
                    batcher.Draw(
                        hueGumpInfo.Texture,
                        new Vector2(targetX, y - topTargetY),
                        hueGumpInfo.UV,
                        hueVecZero,
                        layerDepth
                    );

                y += 7 + newTargGumpInfo.UV.Height / 2 - centerY;

                newTargGumpInfo = ref Client.Game.UO.Gumps.GetGump(bottomGump);
                if (newTargGumpInfo.Texture != null)
                    batcher.Draw(
                        newTargGumpInfo.Texture,
                        new Vector2(targetX, y - 1 - newTargGumpInfo.UV.Height / 2f),
                        newTargGumpInfo.UV,
                        hueVecZero,
                        layerDepth
                    );
            }


            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(BACKGROUND_GRAPHIC);

            batcher.Draw(
                gumpInfo.Texture,
                new Rectangle(x, y, gumpInfo.UV.Width * MULTIPLER, gumpInfo.UV.Height * MULTIPLER),
                gumpInfo.UV,
                hueVecNoto,
                layerDepth
            );

            hueVecNoto.X = 0x21;

            if (entity.Hits != entity.HitsMax || entity.HitsMax == 0)
            {
                int offset = 2;

                if (per >> 2 == 0)
                {
                    offset = per;
                }

                gumpInfo = ref Client.Game.UO.Gumps.GetGump(HP_GRAPHIC);

                batcher.DrawTiled(
                    gumpInfo.Texture,
                    new Rectangle(
                        x + per * MULTIPLER - offset,
                        y,
                        (BAR_WIDTH - per) * MULTIPLER - offset / 2,
                        gumpInfo.UV.Height * MULTIPLER
                    ),
                    gumpInfo.UV,
                    hueVecNoto,
                    layerDepth
                );
            }

            hue = 90;

            if (per > 0)
            {
                if (mobile != null)
                {
                    if (mobile.IsPoisoned)
                    {
                        hue = 63;
                    }
                    else if (mobile.IsYellowHits)
                    {
                        hue = 53;
                    }
                }

                hueVecNoto.X = hue;

                gumpInfo = ref Client.Game.UO.Gumps.GetGump(HP_GRAPHIC);
                batcher.DrawTiled(
                    gumpInfo.Texture,
                    new Rectangle(x, y, per * MULTIPLER, gumpInfo.UV.Height * MULTIPLER),
                    gumpInfo.UV,
                    hueVecNoto,
                    layerDepth
                );
            }
        }
    }
}
