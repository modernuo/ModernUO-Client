// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
using Uo.Pack;

namespace ClassicUO.Game
{
    /// <summary>
    /// asset_prefetch_on_packet (gpu-popin-2): the WorldWriter operations that place a mobile or item, change a body,
    /// graphic or hue, or equip, note the object here on packet arrival; the next Update requests the assets of its new
    /// look before its first draw: the animation direction it will draw (Legacy: the worker decode; Gpu: the anim pages,
    /// that direction first, then the action's other seven), its mount's and equipment's, and an item's art. Bounded: at
    /// most <see cref="Capacity"/> notes waiting (older ones are dropped), <see cref="BudgetPerUpdate"/> resolved per
    /// Update, objects beyond the client view range skipped. Main thread; no allocation.
    /// With the switch on, a drawn mobile whose new look is not loaded yet keeps drawing its previous complete look (the
    /// MobileFrameCache recording, Legacy and Gpu) until every layer of the new one is loaded, at most
    /// <see cref="HoldMaxMs"/>, then switches in one frame (MobileSpriteExtract.Emit / DrawLegacy). A mobile with no
    /// complete look yet (first sight) is drawn as nothing until its body and every equipment layer are loaded, at most
    /// <see cref="FirstSightMaxMs"/> from the first frame it would be visible, then as far as it is loaded (owner
    /// 2026-11-20: no naked-humanoid flash, at most 25 ms unseen). Both bounds schedule a present when they expire.
    /// CUO_LOOK_PREFETCH_CHECK=1 measures, in both switch states, packet arrival to the first complete draw per kind
    /// (p50/p95/max), how many looks were complete at their first draw, and frames that drew a tracked look incomplete.
    /// </summary>
    internal static class LookPrefetch
    {
        public enum Kind : byte
        {
            Mobile,
            Item,
            Equip,
            Count
        }

        public const int Capacity = 256;
        public const int BudgetPerUpdate = 16;

#if CUO_DEVTOOLS
        // the gate's override of the setting (CUO_ASSET_PREFETCH_ON_PACKET=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_ASSET_PREFETCH_ON_PACKET") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LOOK_PREFETCH_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.AssetPrefetchOnPacket ?? false);

        /// <summary>The draw keeps a mobile's previous complete look while its new one loads (bounded): with this switch, or
        /// with anim_async_turn, whose turns load on a worker. The first-sight wait stays with this switch only.</summary>
        public static bool HoldEnabled => Enabled || AnimAsyncTurn.Enabled;

        private static readonly uint[] _serials = new uint[Capacity];
        private static readonly Kind[] _kinds = new Kind[Capacity];
        private static int _head, _count;

        /// <summary>Notes dropped because the queue was full, and resolved (total).</summary>
        public static long Dropped, Resolved, Requested;

        /// <summary>Notes waiting.</summary>
        public static int Count => _count;

        /// <summary>The longest a mobile keeps its previous look while the new one loads (owner 2026-11-20: 50 ms, 25 ms if
        /// the measured p99 fits; a load that never completes draws the new look partial after it; cap hits are bugs to
        /// investigate).</summary>
        public const int HoldMaxMs = 50;

        /// <summary>The longest a mobile seen for the first time is drawn as nothing while its look loads (owner 2026-11-20).</summary>
        public const int FirstSightMaxMs = 25;

        /// <summary>This frame's mobile draws that drew nothing while waiting for their first complete look (reset at each
        /// draw start).</summary>
        public static int FrameFirstSightWait;

        /// <summary>Waits that reached their bound (total): holds (<see cref="HoldTimeouts"/>) and first-sight waits.</summary>
        public static long FirstSightTimeouts;

#if CUO_DEVTOOLS
        /// <summary>CUO_LOOK_PREFETCH_CHECK: hold durations and first-sight waits in ms (a wait of 0: complete at first sight).</summary>
        internal static readonly MsSamples HoldMs = new MsSamples(), FirstSightMs = new MsSamples();

        /// <summary>CUO_LOOK_PREFETCH_CHECK: at each cap hit, the time since the draw before (a bound passes between two
        /// draws, so a wait overshoots its cap by up to that frame gap: a long frame, not a late bound).</summary>
        internal static readonly MsSamples CapFrameGapMs = new MsSamples();

        private static void NoteCapGap(bool capped)
        {
            if (capped)
            {
                CapFrameGapMs.Add(_prevDraw == 0 ? 0 : _thisDraw - _prevDraw, false);
            }
        }
#endif

        /// <summary>A hold ended (<paramref name="capped"/>: by <see cref="HoldMaxMs"/>).</summary>
        internal static void NoteHoldEnd(uint ms, bool capped)
        {
            if (capped)
            {
                HoldTimeouts++;
            }
#if CUO_DEVTOOLS
            if (Check)
            {
                HoldMs.Add(ms, capped);
                NoteCapGap(capped);
            }
#endif
        }

        /// <summary>A first sight ended: the look completed after <paramref name="ms"/> (0: at once), or the bound
        /// (<paramref name="capped"/>).</summary>
        internal static void NoteFirstSightEnd(uint ms, bool capped)
        {
            if (capped)
            {
                FirstSightTimeouts++;
            }
#if CUO_DEVTOOLS
            if (Check)
            {
                FirstSightMs.Add(ms, capped);
                NoteCapGap(capped);
            }
#endif
        }

        /// <summary>A wait bound that expires at <paramref name="since"/> + <paramref name="maxMs"/>: the present that draws
        /// what is loaded then (present on change has no other reason to draw if the load never completes).</summary>
        internal static void ScheduleBound(uint since, int maxMs) => ClassicUO.Loop.VisualDue.At((long)since + maxMs + 1, ClassicUO.Loop.VisualSource.Scene);

        /// <summary>This frame's mobile draws (reset at each draw start): kept their previous look (<see cref="FrameHeld"/>),
        /// drew a layer of the current look missing while a previous complete look existed but could not be held
        /// (<see cref="FramePartial"/>), drew it missing with no previous look (<see cref="FrameFirstSight"/>, a mobile never
        /// drawn complete), drew nothing at all (<see cref="FrameBlank"/>, part of the two before).</summary>
        public static int FrameHeld, FramePartial, FrameFirstSight, FrameBlank;

        /// <summary>Tests: the hold clock (null: Time.Ticks).</summary>
        [ThreadStatic] internal static uint? TicksForTests;

        internal static uint Now => TicksForTests ?? Time.Ticks;

        /// <summary>Holds ended by <see cref="HoldMaxMs"/> (total).</summary>
        public static long HoldTimeouts;

        /// <summary>Draw start: this frame's counts from zero.</summary>
        public static void BeginDraw()
        {
            FrameHeld = FramePartial = FrameFirstSight = FrameBlank = FrameFirstSightWait = 0;
            _prevDraw = _thisDraw;
            _thisDraw = Now;
        }

        private static uint _prevDraw, _thisDraw;

        /// <summary>A mobile draw found layers of its look missing (<paramref name="missing"/> of <paramref name="total"/>
        /// records); <paramref name="hadPrevious"/>: a previous complete look existed.</summary>
        internal static void NoteIncomplete(int missing, int total, bool hadPrevious)
        {
            if (hadPrevious)
            {
                FramePartial++;
            }
            else
            {
                FrameFirstSight++;
            }

            if (missing >= total)
            {
                FrameBlank++;
            }
        }

        /// <summary>WorldWriter: <paramref name="serial"/> has a new look (main thread).</summary>
        public static void Note(uint serial, Kind kind)
        {
            bool on = Enabled;
#if CUO_DEVTOOLS
            if (Check)
            {
                LookPrefetchCheck.Arrived(serial, kind, on);
            }
#endif
            if (!on)
            {
                return;
            }

            if (_count == Capacity)
            {
                // the oldest note goes: a burst larger than the queue keeps its newest looks
                _head = (_head + 1) % Capacity;
                _count--;
                Dropped++;
            }

            int i = (_head + _count) % Capacity;
            _serials[i] = serial;
            _kinds[i] = kind;
            _count++;
        }

        /// <summary>Takes the next note (tests and <see cref="Drain"/>).</summary>
        internal static bool TryTake(out uint serial, out Kind kind)
        {
            if (_count == 0)
            {
                serial = 0;
                kind = Kind.Mobile;

                return false;
            }

            serial = _serials[_head];
            kind = _kinds[_head];
            _head = (_head + 1) % Capacity;
            _count--;

            return true;
        }

        internal static void ResetForTests()
        {
            _head = _count = 0;
            Dropped = Resolved = Requested = 0;
        }

        /// <summary>Each Update, before the draw: resolves up to <see cref="BudgetPerUpdate"/> notes.</summary>
        public static void Drain(World world)
        {
            if (_count == 0)
            {
                return;
            }

            if (world?.Player == null || !Enabled)
            {
                _head = _count = 0;

                return;
            }

            int range = world.ClientViewRange;

            for (int n = 0; n < BudgetPerUpdate && TryTake(out uint serial, out Kind kind); n++)
            {
                Entity e = world.Get(serial);

                if (e == null || e.IsDestroyed)
                {
                    continue;
                }

                if (e is Item item && kind == Kind.Equip && item.Container != 0 && world.Get(item.Container) is Mobile wearer)
                {
                    e = wearer;
                }

                if (e.Distance > range)
                {
                    continue;
                }

                Resolved++;

                if (e is Mobile mobile)
                {
                    PrefetchMobile(mobile);
                }
                else if (e is Item it && it.OnGround)
                {
                    var gpu = Client.Game.GetGpuWorld()?.World;

                    if (gpu != null)
                    {
                        Requested += gpu.PrefetchArt(it.DisplayedGraphic) ? 1 : 0;
                    }
                    else
                    {
                        Requested += Client.Game.UO.Arts.RequestArt(it.DisplayedGraphic) ? 0 : 1;
                    }
                }
            }
        }

        /// <summary>The look the draw uses: body (or mount) and equipment, with the group and direction of this frame.</summary>
        private static void PrefetchMobile(Mobile mobile)
        {
            ushort graphic = mobile.GetGraphicForAnimation();
            PrefetchMobile(mobile, (byte)((byte)mobile.GetDirectionForAnimation() & 7), Mobile.GetGroupForAnimation(mobile, graphic, true));
        }

        /// <summary>The look's body (or mount), mount and equipment in UO direction <paramref name="uoDir"/> of
        /// <paramref name="group"/> (anim_async_turn requests the neighbours of the facing direction with it).</summary>
        internal static void PrefetchMobile(Mobile mobile, byte uoDir, byte group)
        {
            var animations = Client.Game.UO.Animations;
            var gpu = Client.Game.GetGpuWorld()?.World;
            byte dir = uoDir;
            bool flip = false;
            animations.GetAnimDirection(ref dir, ref flip);

            ushort graphic = mobile.GetGraphicForAnimation();
            Request(animations, gpu, graphic, group, dir, uoDir, false);

            Item mount = mobile.FindItemByLayer(Layer.Mount);

            if (mount != null)
            {
                ushort mountGraphic = mount.GetGraphicForAnimation();

                if (mountGraphic != 0xFFFF)
                {
                    Request(animations, gpu, mountGraphic, Mobile.GetGroupForAnimation(mobile, mountGraphic), dir, uoDir, false);
                }
            }

            var look = new EquipLook(mobile);

            for (int layer = (int)Layer.OneHanded; layer <= (int)Layer.Legs; layer++)
            {
                if (layer == (int)Layer.Mount || layer == (int)Layer.Backpack)
                {
                    continue;
                }

                ushort drawBody = EquipDrawBody(mobile.FindItemByLayer((Layer)layer), in look);

                if (drawBody != 0)
                {
                    Request(animations, gpu, drawBody, group, dir, uoDir, true);
                }
            }
        }

        /// <summary>What the draw needs to turn a worn item's AnimID into the body it draws (MobileAnimationSystem.Rebuild).</summary>
        private readonly struct EquipLook
        {
            public readonly LayerSubject Subject;
            public readonly Dictionary<ushort, EquipConvData> Conv;
            public readonly bool Human;

            public EquipLook(Mobile mobile)
            {
                ushort body = mobile.Graphic;
                bool gargoyle = AnimationRules.IsGargoyleDrawBody(body, Client.Game.UO.Version >= ClientVersion.CV_7000);
                Subject = LayerRuleEval.Subject(body, mobile.IsFemale, gargoyle);
                Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(body, out Conv);
                Human = mobile.IsHuman;
            }
        }

        /// <summary>The body a worn item draws with (the layering rules' remap, then the equip conversion), as
        /// MobileAnimationSystem.Rebuild sets EquipSlot.DrawBody; 0 when it draws nothing (no AnimID, or a non-human body).</summary>
        private static ushort EquipDrawBody(Item item, in EquipLook look)
        {
            if (item == null || !look.Human)
            {
                return 0;
            }

            ushort anim = item.ItemData.AnimID;

            if (anim == 0)
            {
                return 0;
            }

            ushort drawBody = LayerRuleEval.Remap(anim, look.Subject);

            if (look.Conv != null && look.Conv.TryGetValue(anim, out EquipConvData data))
            {
                drawBody = data.Graphic;
            }

            return drawBody;
        }

        private static void Request(Renderer.Animations.Animations animations, Uo.Render.WorldRenderer gpu, ushort body, byte group, byte dir, byte uoDir, bool equip)
        {
            Requested++;

            // the renderer drawing the world: the Gpu world reads its own pages (a Legacy atlas decode would be unused)
            if (gpu != null)
            {
                gpu.PrefetchAnim(animations.ResolveBody(body), group, uoDir);
            }
            else if (Renderer.Animations.Animations.AsyncTurn)
            {
                // anim_async_turn: on a worker also for a body already drawn, and never decoded here
                animations.PrefetchDirection(body, group, dir, equip);
            }
            else
            {
                // a non-sync lookup starts the worker decode of a direction never loaded (Animations.LoadDirection)
                animations.GetAnimationFrames(body, group, dir, out _, out _, isEquip: equip);
            }
        }

        /// <summary>The look of <paramref name="e"/> is drawable completely now (checker).</summary>
        internal static bool LookComplete(Entity e) => LookComplete(e, out _, out _);

        /// <summary><see cref="LookComplete(Entity)"/>, with what is missing: <paramref name="missing"/> -1 the body, else
        /// the layer, and the body not loaded (<paramref name="missingBody"/>).</summary>
        internal static bool LookComplete(Entity e, out int missing, out ushort missingBody)
        {
            missing = 0;
            missingBody = 0;
            var animations = Client.Game.UO.Animations;
            var gpu = Client.Game.GetGpuWorld()?.World;

            if (e is Mobile mobile)
            {
                byte uoDir = (byte)((byte)mobile.GetDirectionForAnimation() & 7);
                byte dir = uoDir;
                bool flip = false;
                animations.GetAnimDirection(ref dir, ref flip);
                ushort graphic = mobile.GetGraphicForAnimation();
                byte group = Mobile.GetGroupForAnimation(mobile, graphic, true);

                if (!Ready(animations, gpu, graphic, group, dir, uoDir))
                {
                    missing = -1;
                    missingBody = graphic;

                    return false;
                }

                var look = new EquipLook(mobile);

                for (int layer = (int)Layer.OneHanded; layer <= (int)Layer.Legs; layer++)
                {
                    if (layer == (int)Layer.Mount || layer == (int)Layer.Backpack)
                    {
                        continue;
                    }

                    ushort drawBody = EquipDrawBody(mobile.FindItemByLayer((Layer)layer), in look);

                    if (drawBody != 0 && !Ready(animations, gpu, drawBody, group, dir, uoDir))
                    {
                        missing = layer;
                        missingBody = drawBody;

                        return false;
                    }
                }

                return true;
            }

            if (e is Item it && it.OnGround)
            {
                return gpu != null ? gpu.ArtResident(it.DisplayedGraphic) : !Client.Game.UO.Arts.IsDecodePending(it.DisplayedGraphic);
            }

            return true;
        }

        private static bool Ready(Renderer.Animations.Animations animations, Uo.Render.WorldRenderer gpu, ushort body, byte group, byte dir, byte uoDir)
            => gpu != null ? gpu.AnimResident(animations.ResolveBody(body), group, uoDir) : animations.IsDirectionReady(body, group, dir);
    }
}
