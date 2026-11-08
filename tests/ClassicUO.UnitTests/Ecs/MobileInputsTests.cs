using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // W5: MobileInputs are pushed from the Mobile's mutation sites instead of sampled every frame.
    // The in-client checks (CUO_MOBILE_INPUT_CHECK every sampled frame, the pushed-vs-fresh comparison in -ecscheck) cover
    // the paths that need the running client (packets, the player's walk, CV_7000 rules); these cover the rules and the
    // pushes a World without a client can drive.
    public class MobileInputsTests
    {
        private const uint SERIAL = 0x0000_2000;

        private static MobileInputs Compute(MobileInputSource src)
        {
            MobileInputs o = default;
            MobileInputRules.Compute(in src, ref o);

            return o;
        }

        [Theory]
        [InlineData(0x0190, 0x0190)]
        [InlineData(0x0192, 0x0190)]
        [InlineData(0x0193, 0x0191)]
        [InlineData(0x02B6, 667)]
        [InlineData(0x02B7, 666)]
        [InlineData(0x00C8, 0x00C8)]
        public void AnimationBody_Maps_Dead_Bodies_To_The_Living_Ones(int graphic, int body)
        {
            MobileInputRules.AnimationBody((ushort)graphic).Should().Be((ushort)body);
            Compute(new MobileInputSource { Graphic = (ushort)graphic }).Body.Should().Be((ushort)body);
        }

        [Fact]
        public void Flags_Follow_The_Mobile_Rules()
        {
            Compute(new MobileInputSource { Graphic = 0x190, WarMode = true }).Flags.Should().Be(AnimStateFlags.WarMode | AnimStateFlags.Human);
            Compute(new MobileInputSource { Graphic = 0xC8, Running = true }).Flags.Should().Be(AnimStateFlags.Running);
            Compute(new MobileInputSource { Graphic = 0xC8, IsDead = true }).Flags.Should().Be(AnimStateFlags.Dead);
            Compute(new MobileInputSource { Graphic = 0xC8, Female = true }).Flags.Should().Be(AnimStateFlags.Female);
            Compute(new MobileInputSource { Graphic = 0xC8, Flags = Flags.Hidden }).Flags.Should().Be(AnimStateFlags.Hidden);

            // IsFlying reuses the poisoned bit on CV_7000+ only
            Compute(new MobileInputSource { Graphic = 0xC8, Flags = Flags.Poisoned }).Flags.Should().Be(AnimStateFlags.None);
            Compute(new MobileInputSource { Graphic = 0xC8, Flags = Flags.Poisoned, CV7000 = true }).Flags.Should().Be(AnimStateFlags.Flying);

            // Mobile.IsGargoyle's precedence: (CV_7000 && 0x29A) || 0x29B
            Compute(new MobileInputSource { Graphic = 0x29A }).Flags.Should().NotHaveFlag(AnimStateFlags.GargoyleProp);
            Compute(new MobileInputSource { Graphic = 0x29A, CV7000 = true }).Flags.Should().HaveFlag(AnimStateFlags.GargoyleProp);
            Compute(new MobileInputSource { Graphic = 0x29B }).Flags.Should().HaveFlag(AnimStateFlags.GargoyleProp);
            Compute(new MobileInputSource { Graphic = 666 }).Flags.Should().NotHaveFlag(AnimStateFlags.GargoyleBody);
            Compute(new MobileInputSource { Graphic = 666, CV7000 = true }).Flags.Should().HaveFlag(AnimStateFlags.GargoyleBody);

            (MobileInputRules.PUSHED & (AnimStateFlags.Mounted | AnimStateFlags.Walking | AnimStateFlags.FromServer | AnimStateFlags.Mirror |
                AnimStateFlags.Visible | AnimStateFlags.Shadow)).Should().Be(AnimStateFlags.None, "mounted comes from the slots, the rest is per frame");
        }

        [Fact]
        public void Facing_Is_The_Front_Steps_Direction_Masked_Only_When_Running()
        {
            Compute(new MobileInputSource { Direction = 0x83 }).Facing.Should().Be(3, "no steps: Direction & 7");

            MobileInputs walk = Compute(new MobileInputSource { Direction = 1, HasSteps = true, StepDirection = 0x85 });
            walk.Facing.Should().Be(0x85, "ProcessSteps masks a walking step's direction only when it runs");
            walk.Flags.Should().Be(AnimStateFlags.HasSteps);

            MobileInputs run = Compute(new MobileInputSource { Direction = 1, HasSteps = true, StepDirection = 0x85, StepRun = true });
            run.Facing.Should().Be(5);
            run.Flags.Should().Be(AnimStateFlags.HasSteps | AnimStateFlags.StepRun);
        }

        [Fact]
        public void Hue_Bits_Follow_IsPoisoned_IsParalyzed_IsYellowHits_And_Notoriety()
        {
            Compute(new MobileInputSource { Flags = Flags.Poisoned }).HueBits.Should().Be(MobileHueBits.Poisoned);
            Compute(new MobileInputSource { Flags = Flags.Poisoned, CV7000 = true }).HueBits.Should().Be(MobileHueBits.None, "CV_7000+ uses SetSAPoison");
            Compute(new MobileInputSource { SAPoisoned = true, CV7000 = true }).HueBits.Should().Be(MobileHueBits.Poisoned);
            Compute(new MobileInputSource { Flags = Flags.Frozen }).HueBits.Should().Be(MobileHueBits.Paralyzed);
            Compute(new MobileInputSource { Flags = Flags.YellowBar }).HueBits.Should().Be(MobileHueBits.YellowHits);

            MobileInputs invul = Compute(new MobileInputSource { Notoriety = NotorietyFlag.Invulnerable });
            invul.HueBits.Should().Be(MobileHueBits.Invulnerable);
            invul.Notoriety.Should().Be((byte)NotorietyFlag.Invulnerable);
        }

        [Fact]
        public void Walking_Uses_The_Mobiles_Uint_Expression_And_Delay()
        {
            MobileInputs npc = Compute(new MobileInputSource { LastStepTime = 1000 });
            npc.WalkingDelay.Should().Be((uint)Constants.WALKING_DELAY);
            Compute(new MobileInputSource { PlayerType = true }).WalkingDelay.Should().Be((uint)Constants.PLAYER_WALKING_DELAY);

            MobileInputRules.IsWalking(in npc, 1000).Should().BeTrue();
            MobileInputRules.IsWalking(in npc, 1000 + (uint)Constants.WALKING_DELAY - 1).Should().BeTrue();
            MobileInputRules.IsWalking(in npc, 1000 + (uint)Constants.WALKING_DELAY).Should().BeFalse();

            // Ticks below the delay wrap to a huge value, as `LastStepTime > Time.Ticks - WALKING_DELAY` does
            MobileInputs early = Compute(new MobileInputSource { LastStepTime = 10 });
            MobileInputRules.IsWalking(in early, 20).Should().BeFalse();
        }

        [Fact]
        public void Mounted_Needs_A_Mount_Item_That_Is_Not_A_Tiller()
        {
            AnimationRules.IsMountedBy(0, 0x3EA0, 0).Should().BeFalse("no mount item");
            AnimationRules.IsMountedBy(0x4000_0001, 0x3E96, 0).Should().BeFalse("a boat's tiller (IsDrivingBoat)");
            AnimationRules.IsMountedBy(0x4000_0001, 0x3EA0, 0).Should().BeTrue();
            AnimationRules.IsMountedBy(0x4000_0001, 0x3E9B, 0).Should().BeTrue("ethereal unicorn");
            AnimationRules.IsMountedBy(0x4000_0001, 0x1234, 0xFFFF).Should().BeFalse("mount body 0xFFFF");
        }

        [Fact]
        public void Mirror_Marks_The_Rider_Dirty_On_Every_Mount_Change()
        {
            var mirror = new WorldMirror();
            const uint MOUNT = 0x4000_0010;

            mirror.UpsertMobile(SERIAL, 0x190, 0, 10, 10, 0, 0, 0, false);
            mirror.TryGetEntity(SERIAL, out EcsEntity m).Should().BeTrue();
            m.HasComponent<MobileInputs>().Should().BeTrue("every mobile entity carries the pushed inputs");
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertItem(MOUNT, 0x3EA0, 0, 0, 0, 0, 0, 0, 1, SERIAL, (byte)Layer.Mount, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("mounting");
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertItem(MOUNT, 0x3E96, 0, 0, 0, 0, 0, 0, 1, SERIAL, (byte)Layer.Mount, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("the mount item's graphic changed");
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertItem(MOUNT, 0x3EA0, 0, 10, 10, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("dismounting (the item left the rider)");
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertItem(MOUNT, 0x3EA0, 0, 0, 0, 0, 0, 0, 1, SERIAL, (byte)Layer.Mount, false);
            m.RemoveTag<EquipmentDirty>();
            mirror.Remove(MOUNT);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("dismounting (the item was deleted)");
        }

        // Codex audit P2: the mirror's equipment index is keyed by the item's container, so it holds a mount that arrived before
        // its rider (0x2E with no parent yet, never attached to the rider's list). Mounted must follow the rider's own list.
        [Fact]
        public void Mounted_Ignores_A_Mount_That_Arrived_Before_Its_Rider_And_Serial_Reuse()
        {
            const uint MOUNT = 0x4000_0020;
            var tiles = System.Array.Empty<ClassicUO.Assets.StaticTiles>();
            var world = new World();

            Item orphan = world.GetOrCreateItem(MOUNT);
            orphan.Graphic = 0x3EA0;
            orphan.Layer = Layer.Mount;
            orphan.Container = SERIAL;
            world.Ecs.Sync(orphan);

            Mobile rider = world.GetOrCreateMobile(SERIAL);
            rider.Graphic = 0x190;
            world.Ecs.Sync(rider);
            world.Ecs.Mirror.GetEquipment(SERIAL).Count.Should().Be(1, "the index holds the orphan by its container");
            MobileAnimationSystem.IsMounted(rider, tiles).Should().BeFalse("the rider's own list doesn't hold it, as IsMounted sees it");

            // attached later (a new equip packet): the list changes and the mirror marks the slots dirty
            world.Ecs.Mirror.TryGetEntity(SERIAL, out EcsEntity e).Should().BeTrue();
            e.RemoveTag<EquipmentDirty>();
            rider.PushToBack(orphan);
            world.Ecs.Sync(orphan);
            world.Ecs.CommitChildOrder(orphan); // the hand-linked push attaches it in the mirror, as the writer's attach does
            e.Tags.Has<EquipmentDirty>().Should().BeTrue("attaching marks the rider dirty");
            MobileAnimationSystem.IsMounted(rider, tiles).Should().BeTrue();

            // a boat's tiller on the mount layer isn't riding
            orphan.Graphic = 0x3E96;
            MobileAnimationSystem.IsMounted(rider, tiles).Should().BeFalse();
            orphan.Graphic = 0x3EA0;

            // the rider leaves; a new mobile reuses the serial while the item still names it as container
            // (World.RemoveMobile removes a rider's children with it, so none stays attached to a gone rider: the mirror gives
            // children attached under a serial to a new record for it. The item only still names the serial.)
            world.Ecs.Mirror.DetachChild(MOUNT).Should().BeTrue();
            world.Mobiles.Remove(SERIAL);
            world.Ecs.Mirror.Remove(SERIAL);
            Mobile next = world.GetOrCreateMobile(SERIAL);
            next.Graphic = 0x190;
            world.Ecs.Sync(next);
            world.Ecs.Mirror.TryGetEntity(SERIAL, out EcsEntity e2).Should().BeTrue();
            e2.Tags.Has<EquipmentDirty>().Should().BeTrue("a new entity starts dirty, so its slots are rebuilt from the new mobile");
            world.Ecs.Mirror.GetEquipment(SERIAL).Count.Should().Be(1);
            MobileAnimationSystem.IsMounted(next, tiles).Should().BeFalse("the old rider's mount isn't in the new mobile's list");
        }

        private static MobileInputs Pushed(World world, uint serial)
        {
            world.Ecs.Mirror.TryGetEntity(serial, out EcsEntity e).Should().BeTrue();

            return e.GetComponent<MobileInputs>();
        }

        private static void ShouldMatchFresh(World world, Mobile m)
        {
            MobileInputs fresh = default;
            MobileInputSource src = MobileInputRules.From(m, false);
            MobileInputRules.Compute(in src, ref fresh);
            MobileInputs pushed = Pushed(world, m.Serial);

            pushed.Flags.Should().Be(fresh.Flags);
            pushed.Body.Should().Be(fresh.Body);
            pushed.Facing.Should().Be(fresh.Facing);
            pushed.Notoriety.Should().Be(fresh.Notoriety);
            pushed.HueBits.Should().Be(fresh.HueBits);
            pushed.LastStepTime.Should().Be(fresh.LastStepTime);
            pushed.WalkingDelay.Should().Be(fresh.WalkingDelay);
        }

        [Fact]
        public void Mobile_Setters_And_Step_Queue_Push_Their_Inputs()
        {
            // the setters' own pushes write the GameObject only (no funnel commit): ecs_small_fields reads the fields
            var world = new World { SmallFieldsOverride = false };
            Mobile m = world.GetOrCreateMobile(SERIAL);
            m.Graphic = 0x190;
            world.Ecs.Sync(m);
            ShouldMatchFresh(world, m);
            Pushed(world, SERIAL).Flags.Should().Be(AnimStateFlags.Human);

            m.IsFemale = true;
            Pushed(world, SERIAL).Flags.Should().HaveFlag(AnimStateFlags.Female);

            m.IsDead = true;
            Pushed(world, SERIAL).Flags.Should().HaveFlag(AnimStateFlags.Dead);
            m.IsDead = false;
            Pushed(world, SERIAL).Flags.Should().NotHaveFlag(AnimStateFlags.Dead);

            m.NotorietyFlag = NotorietyFlag.Invulnerable;
            Pushed(world, SERIAL).HueBits.Should().HaveFlag(MobileHueBits.Invulnerable);
            m.NotorietyFlag = NotorietyFlag.Murderer;
            Pushed(world, SERIAL).Notoriety.Should().Be((byte)NotorietyFlag.Murderer);

            m.IsRunning = true;
            Pushed(world, SERIAL).Flags.Should().HaveFlag(AnimStateFlags.Running);

            uint before = Pushed(world, SERIAL).Pushes;
            m.SetSAPoison(true);
            Pushed(world, SERIAL).Pushes.Should().BeGreaterThan(before);

            // flags and graphic reach the ECS through Sync, like the mirror's own components
            m.Flags = Flags.Hidden | Flags.WarMode;
            world.Ecs.Sync(m);
            Pushed(world, SERIAL).Flags.Should().HaveFlag(AnimStateFlags.Hidden).And.HaveFlag(AnimStateFlags.WarMode);
            m.Graphic = 0x193;
            world.Ecs.Sync(m);
            Pushed(world, SERIAL).Body.Should().Be(0x191);
            Pushed(world, SERIAL).Flags.Should().HaveFlag(AnimStateFlags.Dead);

            // the direction setter syncs
            m.Direction = Direction.East;
            Pushed(world, SERIAL).Facing.Should().Be((byte)Direction.East);

            // steps: enqueue, then clear
            m.X = 100;
            m.Y = 100;
            m.EnqueueStep(101, 100, 0, Direction.East, false).Should().BeTrue();
            MobileInputs stepping = Pushed(world, SERIAL);
            stepping.Flags.Should().HaveFlag(AnimStateFlags.HasSteps);
            stepping.LastStepTime.Should().Be(m.LastStepTime);
            ShouldMatchFresh(world, m);

            m.ClearSteps();
            Pushed(world, SERIAL).Flags.Should().NotHaveFlag(AnimStateFlags.HasSteps);
            ShouldMatchFresh(world, m);
        }

        [Fact]
        public void A_Replaced_Mobile_Does_Not_Push_Into_Its_Successors_Entity()
        {
            // the setters' own pushes write the GameObject only (no funnel commit): ecs_small_fields reads the fields
            var world = new World { SmallFieldsOverride = false };
            Mobile old = world.GetOrCreateMobile(SERIAL);
            old.Graphic = 0x190;
            old.IsFemale = true;
            world.Ecs.Sync(old);
            Pushed(world, SERIAL).Flags.Should().HaveFlag(AnimStateFlags.Female);

            // the serial leaves and comes back as a new object (out of range and back, or reused); destroying needs the client,
            // so the mirror entity is dropped as Entity.Destroy's OnDestroyed does
            world.Mobiles.Remove(SERIAL);
            world.Ecs.Mirror.Remove(SERIAL);
            Mobile next = world.GetOrCreateMobile(SERIAL);
            next.Should().NotBeSameAs(old);
            next.Graphic = 0x190;
            world.Ecs.Sync(next);
            Pushed(world, SERIAL).Flags.Should().NotHaveFlag(AnimStateFlags.Female, "a new entity starts from the new mobile's state");

            old.IsFemale = true;
            old.IsRunning = true;
            old.NotorietyFlag = NotorietyFlag.Invulnerable;
            MobileInputs after = Pushed(world, SERIAL);
            after.Flags.Should().NotHaveFlag(AnimStateFlags.Female).And.NotHaveFlag(AnimStateFlags.Running);
            after.HueBits.Should().Be(MobileHueBits.None);
            ShouldMatchFresh(world, next);
        }
    }
}
