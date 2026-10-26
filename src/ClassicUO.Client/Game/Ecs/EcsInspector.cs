// CLEANUP(release): developer tooling (E3). Compiled only with CUO_DEVTOOLS; DevBench's `ecsdump:` plan entry is its only caller.
#if CUO_DEVTOOLS
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Globalization;
using System.Text;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    // Dumps one serial's ECS entity (every component, the equipment slots with their serials, the pushed inputs, the animation
    // state) next to the legacy object's values, to the log under "[ecsdump]". DevBench plan entry:
    //   ecsdump:<hex serial> | player | nearest   (nearest = the closest mobile that isn't the player)
    internal static class EcsInspector
    {
        public static void Dump(World world, string what)
        {
            uint serial = Resolve(world, what);

            if (serial == 0)
            {
                Log.Info($"[ecsdump] {what}: no such serial");

                return;
            }

            Entity legacy = world.Get(serial);
            bool found = world.Ecs.Mirror.TryGetEntity(serial, out EcsEntity e);
            Log.Info($"[ecsdump] ===== 0x{serial:X8} ({what}) legacy={(legacy == null ? "none" : legacy is Mobile ? "Mobile" : "Item")}{(legacy != null && legacy.IsDestroyed ? " (destroyed)" : string.Empty)} ecs={(found ? "entity " + e.Id : "none")}");

            if (legacy != null)
            {
                Log.Info($"[ecsdump] legacy: g=0x{legacy.Graphic:X4} hue=0x{legacy.Hue:X4} ({legacy.X},{legacy.Y},{legacy.Z}) dir={(byte)legacy.Direction} flags=0x{(byte)legacy.Flags:X2}{LegacyKind(world, legacy)}");
            }

            if (!found)
            {
                return;
            }

            ref readonly Ecs.Serial s = ref e.GetComponent<Ecs.Serial>();
            ref readonly Position p = ref e.GetComponent<Position>();
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"ecs: Serial=0x{s.Value:X8} Graphic=0x{e.GetComponent<Ecs.Graphic>().Value:X4} Hue=0x{e.GetComponent<Ecs.Hue>().Value:X4} Position=({p.X},{p.Y},{p.Z}) Facing={e.GetComponent<Facing>().Value} Flags=0x{e.GetComponent<Ecs.Flags>().Value:X2}");

            if (e.TryGetComponent(out Amount amount))
            {
                sb.Append(CultureInfo.InvariantCulture, $" Amount={amount.Value}");
            }

            if (e.TryGetComponent(out EquippedOn worn))
            {
                sb.Append(CultureInfo.InvariantCulture, $" EquippedOn(parent=0x{worn.Parent:X8} layer={worn.Layer})");
            }

            if (e.TryGetComponent(out ContainedIn inside))
            {
                sb.Append(CultureInfo.InvariantCulture, $" ContainedIn(0x{inside.Parent:X8})");
            }

            sb.Append(" tags=[").Append(e.Tags.ToString()).Append(']');
            Log.Info($"[ecsdump] {sb}");

            if (e.HasComponent<MobileInputs>())
            {
                DumpMobile(world, serial, e, legacy as Mobile);
            }
            else if (legacy is Item item)
            {
                Log.Info($"[ecsdump] legacy item: container=0x{item.Container:X8} layer={(byte)item.Layer} amount={item.Amount} multi={item.IsMulti} corpse={item.IsCorpse}");
            }

            // the children the mirror indexes under this serial, next to the legacy list
            Log.Info($"[ecsdump] indexed equipment: {Join(world.Ecs.Mirror.GetEquipment(serial))} | contents: {Join(world.Ecs.Mirror.GetContents(serial))}");

            if (legacy != null)
            {
                var children = new StringBuilder();

                for (LinkedObject i = legacy.Items; i != null; i = i.Next)
                {
                    if (i is Item it)
                    {
                        children.Append(CultureInfo.InvariantCulture, $" 0x{it.Serial:X8}(g=0x{it.Graphic:X4} layer={(byte)it.Layer})");
                    }
                }

                Log.Info($"[ecsdump] legacy children:{children}");
            }
        }

        private static string LegacyKind(World world, Entity legacy)
        {
            if (legacy is Mobile m)
            {
                return $" player={ReferenceEquals(m, world.Player)} dead={m.IsDead} female={m.IsFemale} human={m.IsHuman} mounted={m.IsMounted} running={m.IsRunning} noto={m.NotorietyFlag} animIndex={m.AnimIndex} animBody=0x{m.GetGraphicForAnimation():X4}";
            }

            return string.Empty;
        }

        private static string Join(Friflo.Engine.ECS.Entities entities)
        {
            var sb = new StringBuilder();

            foreach (EcsEntity c in entities)
            {
                ref readonly Position p = ref c.GetComponent<Position>();
                sb.Append(CultureInfo.InvariantCulture, $" 0x{c.GetComponent<Ecs.Serial>().Value:X8}(g=0x{c.GetComponent<Ecs.Graphic>().Value:X4}");

                if (c.TryGetComponent(out EquippedOn w))
                {
                    sb.Append(CultureInfo.InvariantCulture, $" layer={w.Layer}");
                }

                sb.Append(')');
            }

            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static void DumpMobile(World world, uint serial, EcsEntity e, Mobile mobile)
        {
            ref readonly MobileInputs pushed = ref e.GetComponent<MobileInputs>();
            Log.Info($"[ecsdump] MobileInputs (pushed): flags={pushed.Flags} body=0x{pushed.Body:X4} facing={pushed.Facing} noto={pushed.Notoriety} hueBits={pushed.HueBits} lastStep={pushed.LastStepTime} walkDelay={pushed.WalkingDelay} pushes={pushed.Pushes}");

            if (mobile != null)
            {
                bool cv7000 = Client.Game?.UO != null && Client.Game.UO.Version >= Utility.ClientVersion.CV_7000;
                MobileInputs fresh = default;
                MobileInputSource src = MobileInputRules.From(mobile, cv7000);
                MobileInputRules.Compute(in src, ref fresh);
                bool same = pushed.Flags == fresh.Flags && pushed.Body == fresh.Body && pushed.Facing == fresh.Facing && pushed.Notoriety == fresh.Notoriety &&
                            pushed.HueBits == fresh.HueBits && pushed.LastStepTime == fresh.LastStepTime && pushed.WalkingDelay == fresh.WalkingDelay;
                Log.Info($"[ecsdump] MobileInputs (computed from the Mobile now): flags={fresh.Flags} body=0x{fresh.Body:X4} facing={fresh.Facing} noto={fresh.Notoriety} hueBits={fresh.HueBits} lastStep={fresh.LastStepTime} walkDelay={fresh.WalkingDelay} -> {(same ? "same as pushed" : "DIFFERENT from pushed")}");
            }

            ref readonly AnimationState a = ref e.GetComponent<AnimationState>();
            Log.Info
            (
                $"[ecsdump] AnimationState: flags={a.Flags} body=0x{a.Body:X4} serverGroup={a.ServerGroup} facing={a.Facing} dir={a.Dir} group={a.Group} frame={a.Frame} " +
                $"mount(group={a.MountGroup} body=0x{a.MountBody:X4} offY={a.MountOffsetY}) sit=({a.SitX},{a.SitY}) nextFrame={a.NextFrameTime}"
            );

            if (mobile != null)
            {
                Log.Info($"[ecsdump] legacy animation: body=0x{mobile.GetGraphicForAnimation():X4} animIndex={mobile.AnimIndex} group={Mobile.GetGroupForAnimation(mobile, a.Body, true)} mounted={mobile.IsMounted} lastChange={mobile.LastAnimationChangeTime}");
            }

            ref readonly MobileRenderState r = ref e.GetComponent<MobileRenderState>();
            Log.Info($"[ecsdump] MobileRenderState: overrideHue=0x{r.OverrideHue:X4} alpha={r.Alpha} offset=({r.OffsetX},{r.OffsetY})");

            ref readonly EquipmentSlots eq = ref e.GetComponent<EquipmentSlots>();
            Log.Info
            (
                $"[ecsdump] EquipmentSlots: count={eq.Count} cloakAnchor={eq.CloakAnchor} version={eq.Version} dirty={e.Tags.Has<EquipmentDirty>()} mounted={eq.Mounted} " +
                $"mount=0x{eq.MountItem:X8}(g=0x{eq.MountGraphic:X4} anim=0x{eq.MountAnimId:X4} hue=0x{eq.MountHue:X4}) hand1(present={eq.Hand1.Present} light={eq.Hand1.IsLight} anim=0x{eq.Hand1.AnimId:X4}) " +
                $"hand2(present={eq.Hand2.Present} light={eq.Hand2.IsLight} anim=0x{eq.Hand2.AnimId:X4}) lightItems={eq.LightCount}"
            );

            for (int i = 0; i < eq.Count; i++)
            {
                EquipSlot slot = eq.Slots[i];
                Item legacyItem = world.Items.Get(slot.Item);
                string legacyText = legacyItem == null ? "not in World" : $"legacy g=0x{legacyItem.Graphic:X4} hue=0x{legacyItem.Hue:X4} layer={(byte)legacyItem.Layer} container=0x{legacyItem.Container:X8}";
                Log.Info($"[ecsdump]   slot {i}: item=0x{slot.Item:X8} layer={slot.Layer} g=0x{slot.Graphic:X4} anim=0x{slot.AnimId:X4} drawBody=0x{slot.DrawBody:X4} hue=0x{slot.Hue:X4} conv=0x{slot.ConvColor:X4} group={slot.Group} bits={slot.Bits} | {legacyText}");
            }
        }

        private static uint Resolve(World world, string what)
        {
            what = what.Trim();

            if (what.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                return world.Player?.Serial ?? 0;
            }

            if (what.Equals("nearest", StringComparison.OrdinalIgnoreCase))
            {
                Mobile best = null;

                foreach (Mobile m in world.Mobiles.Values)
                {
                    if (m != world.Player && !m.IsDestroyed && (best == null || m.Distance < best.Distance))
                    {
                        best = m;
                    }
                }

                return best?.Serial ?? 0;
            }

            if (what.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                what = what.Substring(2);
            }

            return uint.TryParse(what, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint serial) ? serial : 0;
        }
    }
}
#endif
