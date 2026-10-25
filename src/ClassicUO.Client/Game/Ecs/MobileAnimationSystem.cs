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

            // copies: removing the dirty tag moves the entity to another archetype, which invalidates component refs
            AnimationState s = e.GetComponent<AnimationState>();
            MobileRenderState rs = e.GetComponent<MobileRenderState>();
            bool keysChanged = Sample(world, m, ref s, ref rs);

            if (keysChanged || e.Tags.Has<EquipmentDirty>())
            {
                if (e.Tags.Has<EquipmentDirty>())
                {
                    e.RemoveTag<EquipmentDirty>();
                }

                Rebuild(e, in s);
            }

            Select(m, ref s, ref e.GetComponent<EquipmentSlots>());
            e.GetComponent<AnimationState>() = s;
            e.GetComponent<MobileRenderState>() = rs;

            return true;
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

            AnimStateFlags f = s.Flags & AnimStateFlags.Mirror;

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

            // dead hair and the female torso table are baked into the slots
            const AnimStateFlags EQUIP_KEYS = AnimStateFlags.Dead | AnimStateFlags.Female;

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
        public void Rebuild(EcsEntity mobile, in AnimationState s)
        {
            Rebuilds++;
            ref EquipmentSlots eq = ref mobile.GetComponent<EquipmentSlots>();
            ushort version = (ushort)(eq.Version + 1);
            eq = default;
            eq.Version = version;

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

            if (present[(int)Layer.OneHanded])
            {
                eq.Hand1 = new HandItem { Present = true, AnimId = anim[(int)Layer.OneHanded], IsLight = tiles[gfx[(int)Layer.OneHanded]].IsLight };
            }

            if (present[(int)Layer.TwoHanded])
            {
                eq.Hand2 = new HandItem { Present = true, AnimId = anim[(int)Layer.TwoHanded], IsLight = tiles[gfx[(int)Layer.TwoHanded]].IsLight };
            }

            // MobileView draws equipment only on human bodies
            if ((s.Flags & AnimStateFlags.Human) == 0 && !AnimationRules.IsHumanBody(body))
            {
                return;
            }

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

                if (AnimationRules.IsCovered(layer, gfx, anim, present, body) || anim[li] == 0)
                {
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
        }

        private static void Select(Mobile m, ref AnimationState s, ref EquipmentSlots eq)
        {
            var animations = Client.Game.UO.Animations;
            AnimGroupInput input = AnimGroupInput.From(in s, in eq);
            AnimStateFlags f = s.Flags & ~(AnimStateFlags.MountDrawn | AnimStateFlags.Sitting | AnimStateFlags.CharSitting);

            s.Group = AnimationGroupRules.Select(in input, s.Body, true, animations);
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
            else if (m.TryGetSittingInfo(out AnimationsLoader.SittingInfoData seat))
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
