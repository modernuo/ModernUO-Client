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
    /// that direction first, then the action's other seven), its mount's and equipment's, and an item's art. Mobile and
    /// Equip notes have their own queue and go first (coordinator 2026-11-20: mobiles are what PvP needs); items get the
    /// budget the mobiles leave. Bounded: at most <see cref="MobileCapacity"/> / <see cref="ItemCapacity"/> notes waiting
    /// (the oldest are dropped), <see cref="BudgetPerUpdate"/> resolved per Update, objects beyond the client view range
    /// skipped (at most <see cref="TakesPerUpdate"/> notes looked at). Main thread; no allocation (the queues are sized
    /// once).
    /// Whatever the switch, a drawn mobile whose new look is not loaded yet keeps drawing its previous complete look (the
    /// MobileFrameCache recording, Legacy and Gpu) until every layer of the new one is loaded, at most
    /// <see cref="HoldMaxMs"/>, then switches in one frame (MobileSpriteExtract.Emit / DrawLegacy). With the switch on, a
    /// mobile with no complete look yet (first sight) is drawn as nothing until its body and every equipment layer are loaded, at most
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

        // Sizes (gpu-popin round 6). The large scene's crowd arrival (cw:lswalk, p5 bench-000999/001000) noted ~5.0k Mobile,
        // ~3.0k Equip and 29.2k Item looks within a few Updates; the old single queue (256, 16 per Update) dropped ~29.9k
        // of them. The queues hold that burst with headroom: 16384 mobile notes (2x the 8k burst) and 32768 item notes
        // (1.1x the 29.2k burst, drained meanwhile), 5 B each: 240 KiB per client, allocated once.
        // Budget: 256 resolves per Update, mobiles first. A mobile resolve (layers, mount, up to ~8 equipment requests) is
        // estimated at 2-4 us, an item's at ~0.5 us, so a burst Update spends about 0.5-1 ms; the 8k mobile burst drains
        // in ~32 Updates (~220 ms at 144 fps), the items after it. More per Update would lengthen the teleport frames,
        // which already set the first-sight overshoot (p5 cap_frame_gap_ms). CUO_LOOK_PREFETCH_CHECK reports the drain's
        // longest Update and each queue's high water and drops, to check these numbers.
        public const int MobileCapacity = 16384, ItemCapacity = 32768;
        public const int BudgetPerUpdate = 256;

        /// <summary>Notes looked at per Update at most (resolved or skipped: gone, beyond the view range), so a queue of
        /// far objects cannot make one Update long.</summary>
        public const int TakesPerUpdate = 4 * BudgetPerUpdate;

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

        /// <summary>A bounded FIFO of notes, sized once; full: the oldest note goes (a burst larger than the queue keeps its
        /// newest looks).</summary>
        internal sealed class NoteQueue
        {
            private readonly uint[] _serials;
            private readonly Kind[] _kinds;
            private int _head, _count;

            /// <summary>Notes dropped because the queue was full (total), and the most notes waiting at once.</summary>
            public long Dropped;
            public int HighWater;

            public NoteQueue(int capacity)
            {
                _serials = new uint[capacity];
                _kinds = new Kind[capacity];
            }

            public int Capacity => _serials.Length;
            public int Count => _count;

            public void Add(uint serial, Kind kind)
            {
                int cap = _serials.Length;

                if (_count == cap)
                {
                    _head = (_head + 1) % cap;
                    _count--;
                    Dropped++;
                }

                int i = (_head + _count) % cap;
                _serials[i] = serial;
                _kinds[i] = kind;
                _count++;

                if (_count > HighWater)
                {
                    HighWater = _count;
                }
            }

            public bool TryTake(out uint serial, out Kind kind)
            {
                if (_count == 0)
                {
                    serial = 0;
                    kind = Kind.Mobile;

                    return false;
                }

                serial = _serials[_head];
                kind = _kinds[_head];
                _head = (_head + 1) % _serials.Length;
                _count--;

                return true;
            }

            public void Clear() => _head = _count = 0;

            public void Reset()
            {
                Clear();
                Dropped = 0;
                HighWater = 0;
            }
        }

        /// <summary>Mobile and Equip notes (drained first), Item notes.</summary>
        internal static readonly NoteQueue Mobiles = new NoteQueue(MobileCapacity), Items = new NoteQueue(ItemCapacity);

        /// <summary>Notes resolved, and asset requests made (total).</summary>
        public static long Resolved, Requested;

        /// <summary>Notes dropped because a queue was full (total).</summary>
        public static long Dropped => Mobiles.Dropped + Items.Dropped;

        /// <summary>Notes waiting.</summary>
        public static int Count => Mobiles.Count + Items.Count;

#if CUO_DEVTOOLS
        /// <summary>CUO_LOOK_PREFETCH_CHECK: the longest Drain (Stopwatch ticks) since the checker last read it.</summary>
        public static long DrainMaxTicks;
#endif

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
#if CUO_DEVTOOLS
            DrawSeq++;
#endif
        }

#if CUO_DEVTOOLS
        /// <summary>CUO_LOOK_PREFETCH_CHECK: the draw's frame number (MobileFrameCache.CheckDrawSeq).</summary>
        public static uint DrawSeq;

        /// <summary>CUO_LOOK_PREFETCH_CHECK: a mobile draw's verdict on its look (the checker's completeness test).</summary>
        internal static void NoteDrawn(MobileFrameCache cache, bool complete, bool held = false)
        {
            if (Check)
            {
                cache.CheckDrawSeq = DrawSeq;
                cache.CheckDrawComplete = complete;
                cache.CheckDrawHeld = held;
            }
        }
#endif

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

            (kind == Kind.Item ? Items : Mobiles).Add(serial, kind);
        }

        /// <summary>Takes the next note, mobiles first (tests and <see cref="Drain"/>).</summary>
        internal static bool TryTake(out uint serial, out Kind kind) => Mobiles.TryTake(out serial, out kind) || Items.TryTake(out serial, out kind);

        internal static void ResetForTests()
        {
            Mobiles.Reset();
            Items.Reset();
            Resolved = Requested = 0;
        }

        /// <summary>Each Update, before the draw: resolves up to <see cref="BudgetPerUpdate"/> notes, mobiles first.</summary>
        public static void Drain(World world)
        {
            if (Mobiles.Count == 0 && Items.Count == 0)
            {
                return;
            }

            if (world?.Player == null || !Enabled)
            {
                Mobiles.Clear();
                Items.Clear();

                return;
            }

#if CUO_DEVTOOLS
            long t0 = Check ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
#endif
            int range = world.ClientViewRange;
            int resolved = 0;

            for (int n = 0; n < TakesPerUpdate && resolved < BudgetPerUpdate && TryTake(out uint serial, out Kind kind); n++)
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
                resolved++;

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

#if CUO_DEVTOOLS
            if (Check)
            {
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                if (dt > DrainMaxTicks)
                {
                    DrainMaxTicks = dt;
                }
            }
#endif
        }

        /// <summary>The look the draw uses: body (or mount) and equipment, with the group and direction of this frame.</summary>
        private static void PrefetchMobile(Mobile mobile)
        {
            ushort graphic = mobile.GetGraphicForAnimation();
            PrefetchMobile(mobile, (byte)((byte)mobile.GetDirectionForAnimation() & 7), Mobile.GetGroupForAnimation(mobile, graphic, true));
        }

        /// <summary>The look's body (or mount), mount and equipment in UO direction <paramref name="uoDir"/> of
        /// <paramref name="group"/> (the turn prefetch, AnimAsyncTurn, requests the neighbours of the facing direction with it).</summary>
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
                    byte mountGroup = Mobile.GetGroupForAnimation(mobile, mountGraphic);
                    Request(animations, gpu, mountGraphic, mountGroup, dir, uoDir, false);

                    // mount-fix (owner 2026-11-23): the horse a dismount leaves stands as a mobile of the mount's body, in
                    // its own standing group (not the ridden one); requested while riding so it is loaded at the dismount
                    AnimGroupInput standing = default;
                    byte standGroup = AnimationGroupRules.Select(in standing, mountGraphic, true, animations);

                    if (standGroup != mountGroup)
                    {
                        Request(animations, gpu, mountGraphic, standGroup, dir, uoDir, false);
                    }
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
            else
            {
                // on a worker also for a body already drawn, and never decoded here
                animations.PrefetchDirection(body, group, dir, equip);
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
