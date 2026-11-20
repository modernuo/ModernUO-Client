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

        private static readonly uint[] _serials = new uint[Capacity];
        private static readonly Kind[] _kinds = new Kind[Capacity];
        private static int _head, _count;

        /// <summary>Notes dropped because the queue was full, and resolved (total).</summary>
        public static long Dropped, Resolved, Requested;

        /// <summary>Notes waiting.</summary>
        public static int Count => _count;

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
                    Requested += Client.Game.UO.Arts.RequestArt(it.DisplayedGraphic) ? 0 : 1;
                }
            }
        }

        /// <summary>The look the draw uses: body (or mount) and equipment, with the group and direction of this frame.</summary>
        private static void PrefetchMobile(Mobile mobile)
        {
            var animations = Client.Game.UO.Animations;
            var gpu = Client.Game.GetGpuWorld()?.World;
            byte uoDir = (byte)((byte)mobile.GetDirectionForAnimation() & 7);
            byte dir = uoDir;
            bool flip = false;
            animations.GetAnimDirection(ref dir, ref flip);

            ushort graphic = mobile.GetGraphicForAnimation();
            byte group = Mobile.GetGroupForAnimation(mobile, graphic, true);
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
            // a non-sync lookup starts the worker decode of a direction never loaded (Animations.LoadDirection)
            animations.GetAnimationFrames(body, group, dir, out _, out _, isEquip: equip);
            Requested++;

            if (gpu != null)
            {
                gpu.PrefetchAnim(animations.ResolveBody(body), group, uoDir);
            }
        }

        /// <summary>The look of <paramref name="e"/> is drawable completely now (checker).</summary>
        internal static bool LookComplete(Entity e)
        {
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
                        return false;
                    }
                }

                return true;
            }

            if (e is Item it && it.OnGround)
            {
                return !Client.Game.UO.Arts.IsDecodePending(it.DisplayedGraphic);
            }

            return true;
        }

        private static bool Ready(Renderer.Animations.Animations animations, Uo.Render.WorldRenderer gpu, ushort body, byte group, byte dir, byte uoDir)
            => animations.IsDirectionReady(body, group, dir) && (gpu == null || gpu.AnimResident(animations.ResolveBody(body), group, uoDir));
    }
}
