// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
using Friflo.Engine.ECS;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// Fills a mobile's <see cref="AnimationState"/>, <see cref="MobileRenderState"/> and <see cref="EquipmentSlots"/>:
    /// <list type="number">
    /// <item>samples the selection inputs ClassicUO keeps on Mobile (server action, steps, war mode, frame, step offset)
    /// and the per-frame view context (selection, targeting, profile hues) — until the ECS owns the world update (W5);</item>
    /// <item>rebuilds the compacted equipment list from the mirror's <see cref="EquippedOn"/> index when it is marked dirty;</item>
    /// <item>selects the body and mount action (<see cref="AnimationGroupRules"/>), stored direction and mirror, seat and
    /// gargoyle per-layer actions, as MobileView.Draw does.</item>
    /// </list>
    /// </summary>
    internal sealed class MobileAnimationSystem
    {
        private const int N = PaperdollOrder.N;

        private readonly WorldMirror _mirror;
        private readonly ArchetypeQuery _dirty;
        private readonly List<EcsEntity> _dirtyScratch = new List<EcsEntity>(64);

        public MobileAnimationSystem(WorldMirror mirror)
        {
            _mirror = mirror;
            _dirty = mirror.Store.Query().AllComponents(ComponentTypes.Get<EquipmentSlots>()).AllTags(Tags.Get<EquipmentDirty>());
        }

        public int Rebuilds { get; private set; }

        // EquipmentSlots.Version source, shared by all mobiles (a version is never reused)
        private static uint _equipVersions;

        /// <summary>All of <paramref name="world"/>'s mobiles (the per-frame system once the extract is the renderer's source).</summary>
        public void Run(World world)
        {
            foreach (Mobile m in world.Mobiles.Values)
            {
                Update(world, m);
            }
        }

        /// <summary>Samples, rebuilds if dirty and selects for one mobile. False when it isn't mirrored.</summary>
        public bool Update(World world, Mobile m)
        {
            if (m.IsDestroyed || !_mirror.TryGetEntity(m.Serial, out EcsEntity e) || !e.HasComponent<AnimationState>())
            {
                return false;
            }

            Update(world, m, e);

            return true;
        }

        /// <summary><see cref="Update(World, Mobile)"/> for the mobile's mirror entity <paramref name="e"/>, already looked
        /// up by the caller (it must have an <see cref="AnimationState"/>).</summary>
        public void Update(World world, Mobile m, EcsEntity e)
        {
            // copies: removing the dirty tag moves the entity to another archetype, which invalidates component refs
            AnimationState s = e.GetComponent<AnimationState>();
            MobileRenderState rs = e.GetComponent<MobileRenderState>();

            // W5 (mobile_push_inputs): the slow-changing inputs come from MobileInputs, pushed at their mutation sites;
            // off = all of them read from the Mobile every frame
            bool push = Configuration.Settings.GlobalSettings.MobilePushInputs && e.HasComponent<MobileInputs>();

            if (InputBench && push)
            {
                BenchSample(world, m, e, in s, in rs);
            }
            bool keysChanged = push ? SamplePushed(world, m, in e.GetComponent<MobileInputs>(), ref s, ref rs) : Sample(world, m, ref s, ref rs);

            if (keysChanged || e.Tags.Has<EquipmentDirty>())
            {
                if (e.Tags.Has<EquipmentDirty>())
                {
                    e.RemoveTag<EquipmentDirty>();
                }

                Rebuild(e, in s, m);
            }

            ref EquipmentSlots eq = ref e.GetComponent<EquipmentSlots>();

            if (push)
            {
                // Mobile.IsMounted, from the slots the mirror rebuilds on every equipment change
                if (eq.Mounted)
                {
                    s.Flags |= AnimStateFlags.Mounted;
                }

                if (InputCheck)
                {
                    CheckInputs(world, m, in s, in rs);
                }
            }

            if (Configuration.Settings.GlobalSettings.GpuMobileFrameCache)
            {
                SelectCached(world, m, ref s, ref eq);
            }
            else
            {
                m.FrameCache?.Invalidate();
                Select(m, ref s, ref eq);
            }

            e.GetComponent<AnimationState>() = s;
            e.GetComponent<MobileRenderState>() = rs;
        }

        // W5: Select, or the last frame's result when the sampled inputs are the same (MobileFrameCache)
        private static void SelectCached(World world, Mobile m, ref AnimationState s, ref EquipmentSlots eq)
        {
            MobileFrameCache cache = m.FrameCache ??= new MobileFrameCache();
            MobileFrameCache.SelectKey key = default;
            bool cacheable = (s.Flags & AnimStateFlags.HasSteps) == 0;

            if (cacheable)
            {
                key.Flags = s.Flags;
                key.NextFrameTime = s.NextFrameTime;
                key.Body = s.Body;
                key.Graphic = m.Graphic;
                key.ServerGroup = s.ServerGroup;
                key.Facing = s.Facing;
                key.Frame = s.Frame;
                key.EquipVersion = eq.Version;
                key.X = m.X;
                key.Y = m.Y;
                key.Z = m.Z;
                key.Season = (byte)world.Season;
                key.Tile = m.TileChunk;
                key.TileVersion = m.TileChunk?.ContentVersion ?? 0;

                if (cache.SelectValid && key.Equals(cache.Key))
                {
                    MobileFrameCache.SelectHits++;

                    if (CacheCheck)
                    {
                        CheckSelect(m, in s, in eq, in cache.Selected);
                    }

                    s = cache.Selected;

                    return;
                }
            }

            MobileFrameCache.SelectMisses++;
            uint rolls = AnimationGroupRules.RandomRolls;
            Select(m, ref s, ref eq);

            cache.Key = key;
            cache.Selected = s;
            cache.SelectGen++;
            cache.SelectValid = cacheable && rolls == AnimationGroupRules.RandomRolls;
        }

        // CUO_MOBILE_CACHE_CHECK=1: every reused selection and emission is compared with a fresh one (MobileFrameCache)
        public static readonly bool CacheCheck = Environment.GetEnvironmentVariable("CUO_MOBILE_CACHE_CHECK") == "1";
        public static long CheckSelects, CheckSelectDiffs;
        private static int _checkLogged;

        private static void CheckSelect(Mobile m, in AnimationState sampled, in EquipmentSlots eq, in AnimationState cached)
        {
            AnimationState fresh = sampled;
            EquipmentSlots eqFresh = eq;
            uint rolls = AnimationGroupRules.RandomRolls;
            Select(m, ref fresh, ref eqFresh);

            if (rolls != AnimationGroupRules.RandomRolls)
            {
                return;
            }

            CheckSelects++;
            bool same = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<AnimationState>(in fresh))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<AnimationState>(in cached)));

            for (int i = 0; i < eq.Count && same; i++)
            {
                same = eq.Slots[i].Group == eqFresh.Slots[i].Group;
            }

            if (!same)
            {
                CheckSelectDiffs++;

                if (_checkLogged++ < 20)
                {
                    Utility.Logging.Log.Warn($"[mobile-cache-check] selection differs 0x{m.Serial:X8} g=0x{m.Graphic:X4}: fresh group {fresh.Group} dir {fresh.Dir} flags {fresh.Flags} mount {fresh.MountBody}/{fresh.MountGroup} sit {fresh.SitX},{fresh.SitY} frame {fresh.Frame}; cached group {cached.Group} dir {cached.Dir} flags {cached.Flags} mount {cached.MountBody}/{cached.MountGroup} sit {cached.SitX},{cached.SitY} frame {cached.Frame}");
                }
            }
        }

        /// <summary>Rebuilds every dirty <see cref="EquipmentSlots"/> (normally done lazily by <see cref="Update"/>).</summary>
        public void RebuildDirty()
        {
            _dirtyScratch.Clear();

            foreach (EcsEntity e in _dirty.Entities)
            {
                _dirtyScratch.Add(e);
            }

            foreach (EcsEntity e in _dirtyScratch)
            {
                e.RemoveTag<EquipmentDirty>();
                Rebuild(e, e.GetComponent<AnimationState>());
            }
        }

        // returns true when a flag the equipment slots are built from (dead, female) changed
        private static bool Sample(World world, Mobile m, ref AnimationState s, ref MobileRenderState rs)
        {
            Profile profile = ProfileManager.CurrentProfile;
            bool cv7000 = Client.Game.UO.Version >= ClientVersion.CV_7000;
            ushort graphic = m.Graphic;

            // The mirror bit is history (GetAnimDirection keeps it for directions 3 and 7). The Mobile holds it: MobileView
            // advances it in capture mode, the Gpu world's ECS sprite path writes the ECS result back. One history, so
            // switching gpu_ecs_sprites mid-session can't start from a stale bit. GetAnimDirection is idempotent, so
            // sampling after the view drew (EcsParity) gives the same selection.
            AnimStateFlags f = m.ViewFlipped ? AnimStateFlags.Mirror : 0;

            if (m.AnimationFromServer) f |= AnimStateFlags.FromServer;
            if (m.InWarMode) f |= AnimStateFlags.WarMode;
            if (m.IsRunning) f |= AnimStateFlags.Running;
            if (m.IsMounted) f |= AnimStateFlags.Mounted;
            if (m.IsFlying) f |= AnimStateFlags.Flying;
            if (m.IsDead) f |= AnimStateFlags.Dead;
            if (m.IsHidden) f |= AnimStateFlags.Hidden;
            if (m.IsFemale) f |= AnimStateFlags.Female;
            if (AnimationRules.IsHumanBody(graphic)) f |= AnimStateFlags.Human;
            if (AnimationRules.IsGargoyleDrawBody(graphic, cv7000)) f |= AnimStateFlags.GargoyleBody;
            if (AnimationRules.IsGargoyleProp(graphic, cv7000)) f |= AnimStateFlags.GargoyleProp;
            if (!m.IsDestroyed && m.AllowedToDraw) f |= AnimStateFlags.Visible;

            AnimGroupInput walk = m.GetAnimGroupInput();

            if (walk.Walking) f |= AnimStateFlags.Walking;
            if (walk.HasSteps) f |= AnimStateFlags.HasSteps;
            if (walk.StepRun) f |= AnimStateFlags.StepRun;

            if (!m.IsDead && !m.IsHidden && profile.ShadowsEnabled)
            {
                f |= AnimStateFlags.Shadow;
            }

            bool keysChanged = ((s.Flags ^ f) & EQUIP_KEYS) != 0;

            s.Flags = f;
            s.Body = m.GetGraphicForAnimation();
            s.ServerGroup = m.AnimationGroupRaw;
            s.Frame = m.AnimIndex;
            s.NextFrameTime = (uint)m.LastAnimationChangeTime;
            m.ProcessSteps(out s.Facing);

            rs.Alpha = m.AlphaHue;
            rs.OffsetX = (short)(int)m.Offset.X;
            rs.OffsetY = (short)(int)(m.Offset.Y - m.Offset.Z);
            rs.OverrideHue = SelectOverrideHue(world, m, profile, (f & AnimStateFlags.Human) != 0);

            return keysChanged;
        }

        // dead hair and the female torso table are baked into the slots
        private const AnimStateFlags EQUIP_KEYS = AnimStateFlags.Dead | AnimStateFlags.Female;

        /// <summary>
        /// <see cref="Sample"/> with the pushed inputs (<see cref="MobileInputs"/>): only the animation clock the legacy
        /// World.Update still advances (server action, frame, frame time, from-server), the mirror history, the walking
        /// offset and alpha, and the per-frame view context are read here. Mounted is added from the slots by the caller.
        /// </summary>
        private static bool SamplePushed(World world, Mobile m, in MobileInputs inp, ref AnimationState s, ref MobileRenderState rs)
        {
            Profile profile = ProfileManager.CurrentProfile;
            AnimStateFlags f = inp.Flags;

            if (m.ViewFlipped) f |= AnimStateFlags.Mirror;
            if (m.AnimationFromServer) f |= AnimStateFlags.FromServer;
            if (!m.IsDestroyed && m.AllowedToDraw) f |= AnimStateFlags.Visible;
            if (MobileInputRules.IsWalking(in inp, Time.Ticks)) f |= AnimStateFlags.Walking;

            if ((f & (AnimStateFlags.Dead | AnimStateFlags.Hidden)) == 0 && profile.ShadowsEnabled)
            {
                f |= AnimStateFlags.Shadow;
            }

            bool keysChanged = ((s.Flags ^ f) & EQUIP_KEYS) != 0;

            s.Flags = f;
            s.Body = inp.Body;
            s.ServerGroup = m.AnimationGroupRaw;
            s.Frame = m.AnimIndex;
            s.NextFrameTime = (uint)m.LastAnimationChangeTime;
            s.Facing = inp.Facing;

            rs.Alpha = m.AlphaHue;
            rs.OffsetX = (short)(int)m.Offset.X;
            rs.OffsetY = (short)(int)(m.Offset.Y - m.Offset.Z);
            rs.OverrideHue = SelectOverrideHue(world, m, profile, in inp);

            return keysChanged;
        }

        private static ushort SelectOverrideHue(World world, Mobile m, Profile profile, in MobileInputs inp)
        {
            bool selected = ReferenceEquals(SelectedObject.Object, m);
            AnimStateFlags f = inp.Flags;
            MobileHueBits bits = inp.HueBits;

            var input = new AnimationRules.OverrideHueInput
            {
                HighlightSelected = profile.HighlightGameObjects && selected,
                IsHealthbarTarget = SelectedObject.HealthbarObject == m,
                OutOfRange = profile.NoColorObjectsOutOfRange && m.Distance > world.ClientViewRange,
                PlayerDeadGray = world.Player.IsDead && profile.EnableBlackWhiteEffect,
                Hidden = (f & AnimStateFlags.Hidden) != 0,
                Dead = (f & AnimStateFlags.Dead) != 0,
                Human = (f & AnimStateFlags.Human) != 0,
                PoisonHighlight = profile.HighlightMobilesByPoisoned,
                Poisoned = (bits & MobileHueBits.Poisoned) != 0,
                ParalyzeHighlight = profile.HighlightMobilesByParalize,
                Paralyzed = (bits & MobileHueBits.Paralyzed) != 0,
                InvulHighlight = profile.HighlightMobilesByInvul,
                YellowHits = (bits & MobileHueBits.YellowHits) != 0,
                Invulnerable = (bits & MobileHueBits.Invulnerable) != 0,
                IsPlayer = m.Serial == world.Player.Serial,
                IsLastAttack = m.Serial == world.TargetManager.LastAttack,
                UnderMouseWhileTargeting = world.TargetManager.IsTargeting && selected,
                NotorietyHue = Notoriety.GetHue((NotorietyFlag)inp.Notoriety),
                PoisonHue = profile.PoisonHue,
                ParalyzedHue = profile.ParalyzedHue,
                InvulnerableHue = profile.InvulnerableHue
            };

            return AnimationRules.OverrideHue(in input);
        }

        // CUO_MOBILE_INPUT_BENCH=1: times the legacy and the pushed sample of the same mobile in the same frame (paired, so
        // machine-wide noise cancels); 8 calls each, alternating which goes first. Logged with [mobile-input-check]'s timer.
        public static readonly bool InputBench = Environment.GetEnvironmentVariable("CUO_MOBILE_INPUT_BENCH") == "1";
        public static long BenchLegacyTicks, BenchPushedTicks, BenchSamples, BenchSeatLegacyTicks, BenchSeatPushedTicks, BenchSeats;
        private static bool _benchFlip;

        private static void BenchSample(World world, Mobile m, EcsEntity e, in AnimationState s0, in MobileRenderState rs0)
        {
            const int N = 8;
            MobileInputs inp = e.GetComponent<MobileInputs>();
            bool mounted = e.GetComponent<EquipmentSlots>().Mounted;
            AnimStateFlags sink = 0;
            _benchFlip = !_benchFlip;

            for (int pass = 0; pass < 2; pass++)
            {
                bool legacy = (pass == 0) == _benchFlip;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

                for (int i = 0; i < N; i++)
                {
                    AnimationState s = s0;
                    MobileRenderState rs = rs0;

                    if (legacy)
                    {
                        Sample(world, m, ref s, ref rs);
                    }
                    else
                    {
                        SamplePushed(world, m, in inp, ref s, ref rs);

                        if (mounted)
                        {
                            s.Flags |= AnimStateFlags.Mounted;
                        }
                    }

                    sink ^= s.Flags;
                }

                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                if (legacy)
                {
                    BenchLegacyTicks += dt;
                }
                else
                {
                    BenchPushedTicks += dt;
                }
            }

            BenchSamples += N;
            _benchSink ^= (uint)sink;
        }

        private static uint _benchSink;

        // CUO_MOBILE_INPUT_CHECK=1: every pushed sample is compared with the legacy one (all of Sample's outputs)
        public static readonly bool InputCheck = Environment.GetEnvironmentVariable("CUO_MOBILE_INPUT_CHECK") == "1";
        public static long InputChecks, InputDiffs, SeatChecks, SeatDiffs;
        private static int _inputLogged;

        private static void CheckInputs(World world, Mobile m, in AnimationState pushed, in MobileRenderState rsPushed)
        {
            AnimationState legacy = pushed;
            MobileRenderState rsLegacy = rsPushed;
            Sample(world, m, ref legacy, ref rsLegacy);
            InputChecks++;

            if (legacy.Flags == pushed.Flags && legacy.Body == pushed.Body && legacy.ServerGroup == pushed.ServerGroup &&
                legacy.Frame == pushed.Frame && legacy.NextFrameTime == pushed.NextFrameTime && legacy.Facing == pushed.Facing &&
                rsLegacy.Alpha == rsPushed.Alpha && rsLegacy.OffsetX == rsPushed.OffsetX && rsLegacy.OffsetY == rsPushed.OffsetY &&
                rsLegacy.OverrideHue == rsPushed.OverrideHue)
            {
                return;
            }

            InputDiffs++;

            if (_inputLogged++ < 20)
            {
                Utility.Logging.Log.Warn
                (
                    $"[mobile-input-check] 0x{m.Serial:X8} g=0x{m.Graphic:X4}: legacy flags {legacy.Flags} body 0x{legacy.Body:X4} facing {legacy.Facing} hue 0x{rsLegacy.OverrideHue:X4} | " +
                    $"pushed flags {pushed.Flags} body 0x{pushed.Body:X4} facing {pushed.Facing} hue 0x{rsPushed.OverrideHue:X4} | differing flags {legacy.Flags ^ pushed.Flags}"
                );
            }
        }

        private static ushort SelectOverrideHue(World world, Mobile m, Profile profile, bool human)
        {
            bool selected = ReferenceEquals(SelectedObject.Object, m);

            var input = new AnimationRules.OverrideHueInput
            {
                HighlightSelected = profile.HighlightGameObjects && selected,
                IsHealthbarTarget = SelectedObject.HealthbarObject == m,
                OutOfRange = profile.NoColorObjectsOutOfRange && m.Distance > world.ClientViewRange,
                PlayerDeadGray = world.Player.IsDead && profile.EnableBlackWhiteEffect,
                Hidden = m.IsHidden,
                Dead = m.IsDead,
                Human = human,
                PoisonHighlight = profile.HighlightMobilesByPoisoned,
                Poisoned = m.IsPoisoned,
                ParalyzeHighlight = profile.HighlightMobilesByParalize,
                Paralyzed = m.IsParalyzed,
                InvulHighlight = profile.HighlightMobilesByInvul,
                YellowHits = m.IsYellowHits,
                Invulnerable = m.NotorietyFlag == NotorietyFlag.Invulnerable,
                IsPlayer = m.Serial == world.Player.Serial,
                IsLastAttack = m.Serial == world.TargetManager.LastAttack,
                UnderMouseWhileTargeting = world.TargetManager.IsTargeting && selected,
                NotorietyHue = Notoriety.GetHue(m.NotorietyFlag),
                PoisonHue = profile.PoisonHue,
                ParalyzedHue = profile.ParalyzedHue,
                InvulnerableHue = profile.InvulnerableHue
            };

            return AnimationRules.OverrideHue(in input);
        }

        /// <summary>
        /// Rebuilds <see cref="EquipmentSlots"/> from the mirror: the equipment by layer, the PaperdollOrder paint order,
        /// then MobileView's per-layer skips (dead hair, covered layers, items without an equip AnimID) and drawn bodies.
        /// </summary>
        public void Rebuild(EcsEntity mobile, in AnimationState s, Mobile owner = null)
        {
            Rebuilds++;
            ref EquipmentSlots eq = ref mobile.GetComponent<EquipmentSlots>();
            eq = default;
            eq.Version = ++_equipVersions;

            ushort body = mobile.GetComponent<Graphic>().Value;
            StaticTiles[] tiles = Client.Game.UO.FileManager.TileData.StaticData;

            Span<ushort> gfx = stackalloc ushort[N];
            Span<ushort> anim = stackalloc ushort[N];
            Span<ushort> hue = stackalloc ushort[N];
            Span<uint> serial = stackalloc uint[N];
            Span<bool> present = stackalloc bool[N];
            gfx.Clear();
            anim.Clear();
            hue.Clear();
            serial.Clear();
            present.Clear();

            foreach (EcsEntity item in _mirror.GetEquipment(mobile.GetComponent<Serial>().Value))
            {
                byte layer = item.GetComponent<EquippedOn>().Layer;
                ushort g = item.GetComponent<Graphic>().Value;

                if (layer == (byte)Layer.Mount)
                {
                    // Entity.FindItemByLayer returns the first; the mirror keeps one per layer in practice
                    if (eq.MountItem == 0)
                    {
                        eq.MountItem = item.GetComponent<Serial>().Value;
                        eq.MountGraphic = g;
                        eq.MountAnimId = g < tiles.Length ? tiles[g].AnimID : (ushort)0;
                        eq.MountHue = item.GetComponent<Hue>().Value;
                    }

                    continue;
                }

                if (layer >= N || present[layer])
                {
                    continue;
                }

                present[layer] = true;
                gfx[layer] = g;
                anim[layer] = g < tiles.Length ? tiles[g].AnimID : (ushort)0;
                hue[layer] = item.GetComponent<Hue>().Value;
                serial[layer] = item.GetComponent<Serial>().Value;
            }

            // Mobile.IsMounted from the mobile's own equipment list, not the mirror's index: the index is keyed by the item's
            // container, so it also holds equipment that arrived before its mobile and was never attached (0x2E with no parent
            // yet; World.GetOrCreateMobile doesn't adopt it), and such an orphan can outlive the mobile. Every change of the list
            // or of a listed item reaches the mirror and marks these slots dirty (attach and detach go through Sync, deletion
            // through Remove). Without the Mobile (RebuildDirty), the mirror's mount item.
            eq.Mounted = owner != null ? IsMounted(owner, tiles) : AnimationRules.IsMountedBy(eq.MountItem, eq.MountGraphic, eq.MountAnimId);

            if (present[(int)Layer.OneHanded])
            {
                eq.Hand1 = new HandItem { Present = true, AnimId = anim[(int)Layer.OneHanded], IsLight = tiles[gfx[(int)Layer.OneHanded]].IsLight };
            }

            if (present[(int)Layer.TwoHanded])
            {
                eq.Hand2 = new HandItem { Present = true, AnimId = anim[(int)Layer.TwoHanded], IsLight = tiles[gfx[(int)Layer.TwoHanded]].IsLight };
            }

            // MobileView draws equipment only on human bodies; other bodies only light their light items
            bool human = (s.Flags & AnimStateFlags.Human) != 0 || AnimationRules.IsHumanBody(body);

            bool cv7000 = Client.Game.UO.Version >= ClientVersion.CV_7000;
            bool gargoyle = AnimationRules.IsGargoyleDrawBody(body, cv7000);
            bool dead = (s.Flags & AnimStateFlags.Dead) != 0 || AnimationRules.IsDeadBody(body);
            bool female = (s.Flags & AnimStateFlags.Female) != 0;

            // PaperdollOrder.GraphicsFromEntity keys the table on AnimIDs of layers OneHanded..Legs
            Span<ushort> orderKey = stackalloc ushort[N];
            orderKey.Clear();

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                orderKey[l] = anim[l];
            }

            Span<Layer> raw = stackalloc Layer[N];
            Span<Layer> order = stackalloc Layer[N];
            PaperdollOrder.Build(orderKey, female || gargoyle, raw);
            int count = PaperdollOrder.Filter(raw, includeBackpack: false, order);

            Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(body, out Dictionary<ushort, EquipConvData> conv);

            for (int i = 0; i < count; i++)
            {
                Layer layer = order[i];
                int li = (int)layer;

                if (!present[li] || dead && (layer == Layer.Hair || layer == Layer.Beard))
                {
                    continue;
                }

                if (human && AnimationRules.IsCovered(layer, gfx, anim, present, body))
                {
                    continue;
                }

                if (!human || anim[li] == 0)
                {
                    if (tiles[gfx[li]].IsLight && eq.LightCount < EquipmentSlots.MAX_LIGHT_ONLY)
                    {
                        eq.LightItems[eq.LightCount++] = serial[li];
                    }

                    continue;
                }

                ref EquipSlot slot = ref eq.Slots[eq.Count++];
                slot.Item = serial[li];
                slot.Graphic = gfx[li];
                slot.AnimId = anim[li];
                slot.Hue = hue[li];
                slot.Layer = (byte)layer;
                slot.Group = 0xFF;
                slot.DrawBody = gargoyle ? AnimationRules.FixGargoyleEquipment(anim[li]) : anim[li];

                ref StaticTiles td = ref tiles[gfx[li]];

                if (td.IsPartialHue)
                {
                    slot.Bits |= EquipSlotBits.PartialHue;
                }

                if (td.IsLight)
                {
                    slot.Bits |= EquipSlotBits.Light;
                }

                if (conv != null && conv.TryGetValue(anim[li], out EquipConvData data))
                {
                    slot.DrawBody = data.Graphic;
                    slot.ConvColor = data.Color;
                    slot.Bits |= EquipSlotBits.Converted;
                }
            }

            Span<byte> slotLayers = stackalloc byte[EquipmentSlots.MAX];

            for (int i = 0; i < eq.Count; i++)
            {
                slotLayers[i] = eq.Slots[i].Layer;
            }

            eq.CloakAnchor = AnimationRules.CloakAnchor(order.Slice(0, count), slotLayers.Slice(0, eq.Count));
        }

        /// <summary>
        /// Mobile.TryGetSittingInfo. With mobile_push_inputs its inputs come from the selection: Human and Flying from the
        /// flags; mounted is false here (the branch is reached only without a mount item or with a tiller, where IsMounted
        /// is false); the action is the one just selected for the body, which is what GetGroupForAnimation returns unless
        /// that selection rolled a random action (then the legacy call runs, rolling as before).
        /// </summary>
        private static bool SeatOf(Mobile m, AnimStateFlags f, byte group, bool groupRolled, out AnimationsLoader.SittingInfoData seat)
        {
            if (Configuration.Settings.GlobalSettings.MobilePushInputs && !groupRolled)
            {
                bool sits = m.TryGetSittingInfo
                (
                    (f & AnimStateFlags.Human) != 0,
                    (f & AnimStateFlags.Mounted) != 0,
                    (f & AnimStateFlags.Flying) != 0,
                    group,
                    out seat
                );

                if (InputBench)
                {
                    AnimationsLoader.SittingInfoData tmp;
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    m.TryGetSittingInfo(out tmp);
                    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    m.TryGetSittingInfo((f & AnimStateFlags.Human) != 0, (f & AnimStateFlags.Mounted) != 0, (f & AnimStateFlags.Flying) != 0, group, out tmp);
                    long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                    BenchSeatLegacyTicks += t1 - t0;
                    BenchSeatPushedTicks += t2 - t1;
                    BenchSeats++;
                }

                if (InputCheck)
                {
                    // no random roll in this selection, so the legacy test's GetGroupForAnimation doesn't roll either
                    bool legacy = m.TryGetSittingInfo(out AnimationsLoader.SittingInfoData legacySeat);
                    SeatChecks++;

                    if (legacy != sits || legacySeat.Graphic != seat.Graphic)
                    {
                        SeatDiffs++;
                        Utility.Logging.Log.Warn($"[mobile-input-check] seat of 0x{m.Serial:X8}: legacy {legacy} 0x{legacySeat.Graphic:X4}, pushed {sits} 0x{seat.Graphic:X4}");
                    }
                }

                return sits;
            }

            return m.TryGetSittingInfo(out seat);
        }

        /// <summary>Mobile.IsMounted: the first live mount-layer item of the mobile's equipment list (FindItemByLayer), not a
        /// boat's tiller, with a mount body; <paramref name="tiles"/> gives its AnimID (Item.ItemData).</summary>
        internal static bool IsMounted(Mobile m, StaticTiles[] tiles)
        {
            Item it = m.FindItemByLayer(Layer.Mount);

            return it != null && AnimationRules.IsMountedBy(it.Serial, it.Graphic, it.Graphic < tiles.Length ? tiles[it.Graphic].AnimID : (ushort)0);
        }

        private static void Select(Mobile m, ref AnimationState s, ref EquipmentSlots eq)
        {
            var animations = Client.Game.UO.Animations;
            AnimGroupInput input = AnimGroupInput.From(in s, in eq);
            AnimStateFlags f = s.Flags & ~(AnimStateFlags.MountDrawn | AnimStateFlags.Sitting | AnimStateFlags.CharSitting);

            uint rolls = AnimationGroupRules.RandomRolls;
            s.Group = AnimationGroupRules.Select(in input, s.Body, true, animations);
            bool groupRolled = rolls != AnimationGroupRules.RandomRolls;
            s.MountBody = 0;
            s.MountGroup = 0;
            s.MountOffsetY = 0;
            s.SitX = 0;
            s.SitY = 0;

            bool mirror = (f & AnimStateFlags.Mirror) != 0;
            byte dir = s.Facing;
            AnimationRules.MapDirection(ref dir, ref mirror);

            bool human = (f & AnimStateFlags.Human) != 0;

            if (human && eq.MountItem != 0 && eq.MountGraphic != 0x3E96)
            {
                ushort mountBody = AnimationRules.MountBody(eq.MountGraphic, eq.MountAnimId);

                if (mountBody != 0xFFFF && mountBody < animations.MaxAnimationCount)
                {
                    f |= AnimStateFlags.MountDrawn;
                    s.MountBody = mountBody;
                    s.MountOffsetY = AnimationRules.MountOffsetY(eq.MountGraphic);
                    s.MountGroup = AnimationGroupRules.Select(in input, mountBody, false, animations);
                }
            }
            else if (SeatOf(m, f, s.Group, groupRolled, out AnimationsLoader.SittingInfoData seat))
            {
                f |= AnimStateFlags.Sitting;
                s.Group = (byte)PeopleAnimationGroup.Stand;
                s.Frame = 0;

                dir = s.Facing;
                int x = 0, y = 0;
                Client.Game.UO.FileManager.Animations.FixSittingDirection(ref dir, ref mirror, ref x, ref y, ref seat);
                y += AnimationRules.SIT_OFFSET_Y;

                bool gargoyleProp = (f & AnimStateFlags.GargoyleProp) != 0;

                if (dir == 3)
                {
                    if (gargoyleProp)
                    {
                        y -= 30 - AnimationRules.SIT_OFFSET_Y;
                        s.Group = 42;
                    }
                    else
                    {
                        s.Group = 25;
                    }
                }
                else if (gargoyleProp)
                {
                    s.Group = 42;
                }
                else
                {
                    f |= AnimStateFlags.CharSitting;
                }

                s.SitX = (short)x;
                s.SitY = (short)y;
            }

            s.Dir = dir;
            s.Flags = mirror ? f | AnimStateFlags.Mirror : f & ~AnimStateFlags.Mirror;

            // gargoyles pick each equipment layer's action for its own body (not while seated)
            bool perLayer = (f & AnimStateFlags.GargoyleBody) != 0 && (f & AnimStateFlags.Sitting) == 0;

            for (int i = 0; i < eq.Count; i++)
            {
                ref EquipSlot slot = ref eq.Slots[i];
                slot.Group = perLayer ? AnimationGroupRules.Select(in input, slot.DrawBody, true, animations) : (byte)0xFF;
            }
        }
    }
}
