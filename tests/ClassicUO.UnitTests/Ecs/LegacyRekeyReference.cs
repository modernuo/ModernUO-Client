using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    /// <summary>
    /// T9 child-list slice B: <c>WorldEcs.OnSerialChanged</c>, the death re-key's ECS update before T1, copied from the client
    /// when the child list went (the tests' old-path copies and their fixtures re-key with it). It walked the entity's child
    /// list; the copy walks the ECS contents of the old serial, taken before the re-key (the same children in the same order:
    /// the gates checked list and ECS against each other with CUO_CONTENTS_CHECK until slice B).
    /// </summary>
    internal static class LegacyRekeyReference
    {
        /// <summary>The children the re-key moves: the old serial's held children, in child order.</summary>
        internal static Item[] Children(World world, uint serial) =>
            world.State.Contents(serial).ToArray().Select(s => world.Items.Get(s)).Where(i => i != null).ToArray();

        /// <summary>The old DisplayDeath block's re-key: out of the table, each child's Container, into the table, the Serial, then <see cref="SerialChanged"/>.</summary>
        internal static void Rekey(World world, Mobile owner, uint serial)
        {
            uint oldSerial = owner.Serial;
            Item[] children = Children(world, oldSerial);

            foreach (Item it in children)
            {
                it.TestSetContainer(serial);
            }

            world.Mobiles[serial] = owner;
            owner.Serial = serial;
            SerialChanged(world, oldSerial, owner, children);
        }

        internal static void SerialChanged(World world, uint oldSerial, Entity entity) =>
            SerialChanged(world, oldSerial, entity, Children(world, oldSerial));

        internal static void SerialChanged(World world, uint oldSerial, Entity entity, Item[] children)
        {
            WorldEcs ecs = world.Ecs;

            // the Mobile keeps IsFlipped across the re-key; so must the persistent mirror bit
            AnimStateFlags keep = 0;

            if (ecs.Mirror.TryGetEntity(oldSerial, out EcsEntity old) && old.HasComponent<AnimationState>())
            {
                keep = old.GetComponent<AnimationState>().Flags & AnimStateFlags.Mirror;
            }

            ecs.Mirror.TryGetExtras(oldSerial, out MirrorExtras extras);

            // T9 S8c: the Mobile's timer and walk queue moved with the instance (its fields, which Sync pushed into the new
            // record); they live in the record now, so the reference carries them as the fields did
            EcsEntity oldMobile = default;
            bool motion = entity is Mobile && ecs.Mirror.TryGetEntity(oldSerial, true, out oldMobile)
                && oldMobile.HasComponent<AnimationTimer>() && oldMobile.HasComponent<Steps>();
            AnimationTimer timer = motion ? oldMobile.GetComponent<AnimationTimer>() : AnimationTimer.MobileDefault;
            Steps steps = motion ? oldMobile.GetComponent<Steps>() : default;

            ecs.Mirror.Remove(oldSerial);
            ecs.Sync(entity);

            if (entity is Mobile moved && !entity.IsDestroyed && ecs.Mirror.TryGetEntity(entity.Serial, true, out EcsEntity fresh))
            {
                fresh.GetComponent<AnimationTimer>() = timer;
                fresh.GetComponent<Steps>() = steps;
                WorldEcs.WriteClock(moved, fresh);

                // the old Sync computed the inputs from the fields, queue included
                ecs.CommitMobileInputs(moved);
            }

            if (ecs.Mirror.TryGetEntity(entity.Serial, out _) && !entity.IsDestroyed)
            {
                ecs.Mirror.CarryMobileExtras(in extras, entity.Serial);
            }

            if (keep != 0 && ecs.Mirror.TryGetEntity(entity.Serial, out EcsEntity rekeyed) && rekeyed.HasComponent<AnimationState>())
            {
                rekeyed.GetComponent<AnimationState>().Flags |= keep;
            }

            // each child's Sync takes its new Container (a new parent: detached); the children are re-stamped in their order
            foreach (Item child in children)
            {
                ecs.Sync(child);
                ecs.CommitChildOrder(child);
            }
        }
    }

    /// <summary>
    /// T9 child-list slice B: the list half of <c>World.UnlinkFromContainer</c>, which the client no longer has. The tests'
    /// old-path copies still keep their own child lists (the reference the new path's ECS children are compared with), so
    /// their calls into World's removals unlink from the list first, as World did.
    /// </summary>
    internal static class LegacyLists
    {
        internal static void Unlink(World world, Item item)
        {
            if (item != null && item.Container != 0xFFFF_FFFF)
            {
                // the tables' lookup, as World's before T9 (world_entity_index): the reference keeps the old semantics
                LegacyWorldLookups.GetHeld(world, item.Container)?.Remove(item);
            }
        }

        internal static void RemoveItemFromContainer(World world, Item item)
        {
            Unlink(world, item);
            world.RemoveItemFromContainer(item);
        }

        internal static bool RemoveItem(World world, uint serial, bool forceRemove = false)
        {
            Item item = world.Items.Get(serial);

            if (item != null && !item.IsDestroyed)
            {
                Unlink(world, item);
            }

            return world.RemoveItem(serial, forceRemove);
        }

        /// <summary>An old-path world's child list: the children it links that are not destroyed (a destroyed parent's
        /// cascade no longer unlinks them).</summary>
        internal static System.Collections.Generic.List<uint> Children(Entity e)
        {
            var list = new System.Collections.Generic.List<uint>();

            for (ChildNode i = e?.Items; i != null; i = i.NextNode)
            {
                if (!i.Owner.IsDestroyed)
                {
                    list.Add(i.Owner.Serial);
                }
            }

            return list;
        }
    }
}
