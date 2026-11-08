using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 7 (ecs-transition-plan.md, T1), group 1: the small writers. Same method as parts 4-6: a copy of the old
    // handler code on one World, the funnel on another, identical starts, everything compared (AssertSameWorld, which since
    // part 7 also compares MiscOf: the price, the speed mode, the dead flag and the war mode).
    public partial class WorldWriterMobileTests
    {
        private const uint CORPSE = 0x4000_7800, CLOOT = 0x4000_7801, NEWCLOOT = 0x4000_7802;

        // funnel part 7: the other cache-only and input fields the small writers touch
        internal static string MiscOf(Entity e) =>
            e is Item i ? $"price {i.Price}" :
            e is Mobile m ? $"speed {m.SpeedMode} dead {m.IsDead} war {m.InWarMode}" : "";

        // ---- the old handler code (pre-T1), one write each as the handlers made it

        internal static void OldSetName(Entity e, string name) => OldScalarWrites.Name(e, name);

        internal static void OldShopEntry(Item it, uint price, string name)
        {
            OldScalarWrites.Price(it, price);
            OldScalarWrites.Name(it, name);
        }

        internal static void NewShopEntry(World world, Item it, uint price, string name)
        {
            world.Writer.SetPrice(it, price);
            world.Writer.SetName(it, name);
        }

        // 0x89, the old loop body for one entry that is not the backpack
        internal static void OldCorpseEquipment(World world, Entity corpse, uint serial, Layer layer)
        {
            Item item = world.GetOrCreateItem(serial);

            world.RemoveItemFromContainer(item);
            item.Container = corpse.Serial;
            item.Layer = layer;
            corpse.PushToBack(item);
            world.Ecs.Sync(item);
            WorldWriterTests.AttachLists(world);
        }

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        [InlineData(Start.DestroyedHeld)]
        public void The_Name_Price_Speed_And_Dead_Writers_Equal_The_Old_Path(Start start)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = true;

            foreach ((uint serial, string name) in new[] { (MOB, "Iolo"), (BAG, "a bag"), (OTHER, ""), (MOB, null), (BAG, "a bag of holding") })
            {
                // from the tables (World.Get skips a destroyed held mobile; the handlers never pass one)
                Entity a = (Entity)oldWorld.Mobiles.Get(serial) ?? oldWorld.Items.Get(serial), b = (Entity)newWorld.Mobiles.Get(serial) ?? newWorld.Items.Get(serial);
                Pair(() => OldSetName(a, name), () => newWorld.Writer.SetName(b, name));
                AssertSameWorld(oldWorld, newWorld);
            }

            Item oldBag = oldWorld.Items.Get(BAG), newBag = newWorld.Items.Get(BAG);
            Pair(() => OldShopEntry(oldBag, 120, "a bag: 1"), () => NewShopEntry(newWorld, newBag, 120, "a bag: 1"));
            AssertSameWorld(oldWorld, newWorld);
            newBag.Price.Should().Be(120u);
            Pair(() => OldShopEntry(oldBag, 0, ""), () => NewShopEntry(newWorld, newBag, 0, ""));
            AssertSameWorld(oldWorld, newWorld);

            foreach (uint serial in new[] { MOB, OTHER })
            {
                Mobile a = oldWorld.Mobiles.Get(serial), b = newWorld.Mobiles.Get(serial);
                Pair(() => { a.SpeedMode = CharacterSpeedType.FastUnmount; oldWorld.Ecs.PushInputs(a); }, () => newWorld.Writer.SetSpeedMode(b, CharacterSpeedType.FastUnmount));
                Pair(() => a.IsDead = true, () => newWorld.Writer.SetDead(b, true));
                AssertSameWorld(oldWorld, newWorld);
                Pair(() => a.IsDead = false, () => newWorld.Writer.SetDead(b, false));
                // war mode on another mobile: the setter is empty (war mode is a flag), the inputs are committed as pushed
                Pair(() => a.InWarMode = true, () => newWorld.Writer.SetWarMode(b, true));
                AssertSameWorld(oldWorld, newWorld);
            }

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void SetDead_And_SetWarMode_Commit_The_Inputs_Once()
        {
            World world = BuildPlayer(out TestPlayer player);
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e);
            long pushes = e.GetComponent<MobileInputs>().Pushes;

            world.Writer.SetWarMode(player, true);
            ((long)e.GetComponent<MobileInputs>().Pushes - pushes).Should().Be(1L);
            (e.GetComponent<MobileInputs>().Flags & AnimStateFlags.WarMode).Should().NotBe((AnimStateFlags)0);

            world.Writer.SetDead(player, true);
            ((long)e.GetComponent<MobileInputs>().Pushes - pushes).Should().Be(2L);
            (e.GetComponent<MobileInputs>().Flags & AnimStateFlags.Dead).Should().NotBe((AnimStateFlags)0);
        }

        [Fact]
        public void The_Players_War_Mode_And_Speed_Mode_Equal_The_Old_Path()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            b.Funnel = true; // the new World's callbacks walk through the converted Walk path
            newWorld.Writer.ThrowOnViolation = true;

            foreach (bool war in new[] { true, true, false })
            {
                Pair(() => a.InWarMode = war, () => newWorld.Writer.SetWarMode(b, war));
                AssertSameWorld(oldWorld, newWorld);
            }

            Pair(() => { a.SpeedMode = CharacterSpeedType.CantRun; oldWorld.Ecs.PushInputs(a); }, () => newWorld.Writer.SetSpeedMode(b, CharacterSpeedType.CantRun));
            Pair(() => OldScalarWrites.Name(a, "Avatar"), () => newWorld.Writer.SetName(b, "Avatar"));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);
        }

        // a corpse on the ground with one item already in it; MOB wears ITEM (Build), BAG lies on the ground
        private static World BuildCorpse(Start start)
        {
            World world = Build(start);
            Item corpse = world.GetOrCreateItem(CORPSE);
            corpse.Graphic = 0x2006;
            corpse.SetInWorldTile(1002, 1000, 0);
            Item loot = world.GetOrCreateItem(CLOOT);
            loot.Graphic = 0x1F03;
            loot.Container = CORPSE;
            loot.Layer = Layer.Robe;
            corpse.PushToBack(loot);
            world.Ecs.Sync(loot);
            WorldWriterTests.AttachLists(world);

            return world;
        }

        private static List<uint> MirrorChildren(World world, uint serial) =>
            world.Ecs.Mirror.GetEquipment(serial).Select(x => x.GetComponent<Serial>().Value)
                .Concat(new uint[] { 0 })
                .Concat(world.Ecs.Mirror.GetContents(serial).Select(x => x.GetComponent<Serial>().Value)).ToList();

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.DestroyedHeld)]
        public void CorpseEquipment_Equals_The_Old_Path(Start start)
        {
            World oldWorld = BuildCorpse(start), newWorld = BuildCorpse(start);
            newWorld.Writer.ThrowOnViolation = true;
            Entity oldCorpse = oldWorld.Get(CORPSE), newCorpse = newWorld.Get(CORPSE);

            // the worn shirt, the bag from the ground, a new item, the item already in the corpse (to the end), the shirt again
            foreach ((uint serial, Layer layer) in new[] { (ITEM, Layer.Shirt), (BAG, Layer.Pants), (NEWCLOOT, Layer.Helmet), (CLOOT, Layer.Robe), (ITEM, Layer.Cloak) })
            {
                Pair(() => OldCorpseEquipment(oldWorld, oldCorpse, serial, layer), () => newWorld.Writer.PlaceCorpseEquipment(newCorpse, serial, layer).Should().NotBeNull());
                AssertSameWorld(oldWorld, newWorld);
                ChildSerials(newCorpse).Should().Equal(OldChildSerials(oldCorpse));
                MirrorChildren(newWorld, CORPSE).Should().Equal(MirrorChildren(oldWorld, CORPSE), "the mirror's equipment index order");
                MirrorChildren(newWorld, MOB).Should().Equal(MirrorChildren(oldWorld, MOB));
            }

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.OutstandingPlacements.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void PlaceCorpseEquipment_Commits_The_Detached_State_Then_The_Final_One()
        {
            World world = BuildCorpse(Start.Standing);
            var seen = new List<(uint Container, Layer Layer, uint CacheContainer)>();
            world.Ecs.CommitObserver = (item, v) => seen.Add((v.Container, v.Layer, item.Container));

            world.Writer.PlaceCorpseEquipment(world.Get(CORPSE), ITEM, Layer.Pants);
            world.Ecs.CommitObserver = null;

            seen.Should().Equal((0xFFFF_FFFFu, Layer.Shirt, MOB), (CORPSE, Layer.Pants, MOB));
        }

        [Fact]
        public void PlaceCorpseEquipment_Cancels_The_Items_Placement()
        {
            World world = BuildCorpse(Start.Standing);
            Item item = world.Writer.UpsertWorldItem(NEWCLOOT, 0x1F03, 0, 1, 1003, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement placement);
            item.Should().NotBeNull();
            world.Writer.OutstandingPlacements.Should().Be(1);

            world.Writer.PlaceCorpseEquipment(world.Get(CORPSE), NEWCLOOT, Layer.Helmet).Should().BeSameAs(item);

            world.Writer.OutstandingPlacements.Should().Be(0);
            world.Writer.ThrowOnViolation = true;
            Action place = () => world.Writer.PlaceWorldItem(in placement);
            place.Should().Throw<ArgumentException>();
            item.Container.Should().Be(CORPSE);
        }

        [Fact]
        public void The_Small_Writers_Reject_What_This_World_Does_Not_Hold()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildCorpse(Start.Standing), other = BuildCorpse(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Mobile stranger = Mobile.Create(world, 0x0000_3777);
                Item foreign = other.Items.Get(BAG);
                Item foreignCorpse = other.Items.Get(CORPSE);
                string before = MiscOf(stranger) + StatusOf(stranger), beforeItem = MiscOf(foreign) + StatusOf(foreign);
                List<uint> corpseBefore = ChildSerials(foreignCorpse);

                var acts = new Action[]
                {
                    () => world.Writer.SetName(stranger, "x"),
                    () => world.Writer.SetName(foreign, "x"),
                    () => world.Writer.SetPrice(foreign, 9),
                    () => world.Writer.SetSpeedMode(stranger, CharacterSpeedType.CantRun),
                    () => world.Writer.SetWarMode(stranger, true),
                    () => world.Writer.SetDead(stranger, true),
                    () => world.Writer.PlaceCorpseEquipment(foreignCorpse, ITEM, Layer.Pants)
                };

                foreach (Action act in acts)
                {
                    if (throws)
                    {
                        act.Should().Throw<ArgumentException>();
                    }
                    else
                    {
                        act();
                    }
                }

                (MiscOf(stranger) + StatusOf(stranger)).Should().Be(before);
                (MiscOf(foreign) + StatusOf(foreign)).Should().Be(beforeItem);
                ChildSerials(foreignCorpse).Should().Equal(corpseBefore);
                world.Items.Get(ITEM).Container.Should().Be(MOB, "the rejected entry changed nothing");
                world.Writer.Violations.Should().Be(acts.Length);
                world.Writer.InScope.Should().BeFalse();
            }
        }

        [Fact]
        public void The_Detached_World_Player_Takes_The_Small_Writes_As_Before_T1()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildPlayer(out TestPlayer player);
                world.Writer.ThrowOnViolation = throws;
                world.Writer.PlaceInContainer(MOB, 0xE76, 1, 10, 10, 0, BAG, out _).Should().NotBeNull();
                world.Mobiles.Get(MOB).Should().BeNull();

                world.Writer.SetWarMode(player, true);
                world.Writer.SetSpeedMode(player, CharacterSpeedType.FastUnmount);
                world.Writer.SetName(player, "ghost");
                world.Writer.SetDead(player, true);

                world.Writer.Violations.Should().Be(0);
                MiscOf(player).Should().Be("speed FastUnmount dead True war True");
                // T9: the detached player has no record, so the name is accepted but not kept (a misconfigured server's case)
                player.Name.Should().BeNull();
            }
        }

        [Fact]
        public void The_Small_Writers_Do_Not_Nest()
        {
            World world = BuildCorpse(Start.Standing);
            world.Writer.ThrowOnViolation = false;
            Mobile m = world.Mobiles.Get(MOB);

            // a nested call from inside an operation (the fold of another mobile is open): rejected, nothing changes
            world.Ecs.BeginFold(world.Mobiles.Get(OTHER));
            world.Writer.SetName(m, "x");
            world.Writer.SetWarMode(m, true);
            world.Writer.SetDead(m, true);
            world.Writer.PlaceCorpseEquipment(world.Get(CORPSE), NEWCLOOT, Layer.Pants).Should().BeNull();
            world.Ecs.EndFold(world.Mobiles.Get(OTHER));

            world.Writer.Violations.Should().Be(4);
            m.Name.Should().NotBe("x");
            m.IsDead.Should().BeFalse();
            world.Items.Get(NEWCLOOT).Should().BeNull("a rejected entry creates no item");
        }

        // ---- group 2: the death re-key (0xAF DisplayDeath): the mobile moves to serial | 0x80000000 with its children

        // the old handler block, from the owner lookup to OnSerialChanged (LegacyRekeyReference since T9 child-list slice B)
        internal static void OldRekey(World world, Mobile owner)
        {
            uint serial = owner.Serial | 0x80000000;

            if (world.Mobiles.Remove(owner.Serial))
            {
                LegacyRekeyReference.Rekey(world, owner, serial);
            }
        }

        public enum RekeyStart { Plain, Flipped, DeadSlotTaken, AlreadyDead }

        private static World BuildRekey(Start start, RekeyStart rk)
        {
            World world = Build(start);
            Mobile m = world.Mobiles.Get(MOB);

            if (rk == RekeyStart.Flipped && world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e) && e.HasComponent<AnimationState>())
            {
                e.GetComponent<AnimationState>().Flags |= AnimStateFlags.Mirror;
            }
            else if (rk == RekeyStart.DeadSlotTaken)
            {
                // an earlier death of the same serial still in the table (its re-keyed mobile not yet removed)
                Mobile earlier = world.GetOrCreateMobile(OTHER);
                world.Mobiles.Remove(OTHER);
                world.Mobiles[DEAD] = earlier;
                earlier.Serial = DEAD;
                LegacyRekeyReference.SerialChanged(world, OTHER, earlier);
            }
            else if (rk == RekeyStart.AlreadyDead)
            {
                OldRekey(world, m);
            }

            return world;
        }

        public static IEnumerable<object[]> RekeyCases()
        {
            foreach (Start s in new[] { Start.Standing, Start.Walking, Start.DestroyedHeld })
            {
                foreach (RekeyStart r in Enum.GetValues(typeof(RekeyStart)))
                {
                    yield return new object[] { s, r };
                }
            }
        }

        [Theory]
        [MemberData(nameof(RekeyCases))]
        public void RekeyDeadMobile_Equals_The_Old_Path(Start start, RekeyStart rk)
        {
            World oldWorld = BuildRekey(start, rk), newWorld = BuildRekey(start, rk);
            newWorld.Writer.ThrowOnViolation = true;
            uint from = rk == RekeyStart.AlreadyDead ? DEAD : MOB;
            Mobile a = oldWorld.Mobiles.Get(from), b = newWorld.Mobiles.Get(from);

            Pair(() => OldRekey(oldWorld, a), () => newWorld.Writer.RekeyDeadMobile(b).Should().Be(DEAD));
            AssertSameWorld(oldWorld, newWorld, DEAD);
            AssertSameWorld(oldWorld, newWorld, MOB);
            newWorld.Mobiles.Get(DEAD).Should().BeSameAs(b);
            ChildSerials(b).Should().Equal(OldChildSerials(a));
            MirrorChildren(newWorld, DEAD).Should().Equal(MirrorChildren(oldWorld, DEAD));
            MirrorChildren(newWorld, MOB).Should().Equal(MirrorChildren(oldWorld, MOB));

            if (newWorld.Ecs.Mirror.TryGetEntity(DEAD, out EcsEntity eb) && eb.HasComponent<AnimationState>())
            {
                oldWorld.Ecs.Mirror.TryGetEntity(DEAD, out EcsEntity ea);
                eb.GetComponent<AnimationState>().Flags.Should().Be(ea.GetComponent<AnimationState>().Flags, "the mirror bit carried over");
            }

            // then the handler's death animation on the re-keyed mobile
            Time.Ticks = NOW + 333;
            Pair(() => OldAnimate(a, AnimPacket.Death), () => NewAnimate(newWorld, b, AnimPacket.Death));
            Time.Ticks = NOW;
            AssertSameWorld(oldWorld, newWorld, DEAD);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void RekeyDeadMobile_Rejects_What_This_World_Does_Not_Hold_And_Does_Not_Nest()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = Build(Start.Standing), other = Build(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Mobile foreign = other.Mobiles.Get(MOB);
                Mobile stranger = Mobile.Create(world, MOB);
                var acts = new Action[] { () => world.Writer.RekeyDeadMobile(foreign), () => world.Writer.RekeyDeadMobile(stranger) };

                foreach (Action act in acts)
                {
                    if (throws)
                    {
                        act.Should().Throw<ArgumentException>();
                    }
                    else
                    {
                        act();
                    }
                }

                (foreign.Serial, stranger.Serial).Should().Be((MOB, MOB));
                other.Mobiles.Get(MOB).Should().BeSameAs(foreign);
                world.Mobiles.Get(MOB).Should().NotBeSameAs(stranger);
                world.Mobiles.Get(DEAD).Should().BeNull();
                world.Writer.Violations.Should().Be(2);
            }

            World w = Build(Start.Standing);
            w.Writer.ThrowOnViolation = false;
            w.Ecs.BeginFold(w.Mobiles.Get(OTHER));
            w.Writer.RekeyDeadMobile(w.Mobiles.Get(MOB)).Should().Be(0u);
            w.Ecs.EndFold(w.Mobiles.Get(OTHER));
            w.Mobiles.Get(MOB).Serial.Should().Be(MOB);
            w.Items.Get(ITEM).Container.Should().Be(MOB);
            w.Writer.Violations.Should().Be(1);
        }

        // ---- group 3: the per-frame frame advance and the end of a corpse animation (ProcessAnimation split). The animation
        // data is not loaded in the tests: the caller's frame count is a parameter of both paths (the old body from the data
        // lookup on). The oracle reads and writes the timer's private fields itself, as the base revision (0b388a043) did,
        // not through AnimationTimerState/ApplyAnimationTimer, which the operation uses (Codex audit of part 7, P3 #1)

        private static readonly System.Reflection.FieldInfo FInterval = TimerField("_animationInterval"),
            FRepeatMode = TimerField("_animationRepeateMode"), FRepeatModeCount = TimerField("_animationRepeatModeCount"),
            FRepeat = TimerField("_animationRepeat"), FForward = TimerField("_isAnimationForwardDirection");

        private static System.Reflection.FieldInfo TimerField(string name) =>
            typeof(Mobile).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Mobile.{name} not found");

        // the private timer fields: interval, repeat mode, repeat mode count, repeat, forward
        internal static (byte Interval, ushort Mode, ushort ModeCount, bool Repeat, bool Forward) TimerFieldsOf(Mobile m) =>
            ((byte)FInterval.GetValue(m), (ushort)FRepeatMode.GetValue(m), (ushort)FRepeatModeCount.GetValue(m),
                (bool)FRepeat.GetValue(m), (bool)FForward.GetValue(m));

        internal static void SetTimerFields(Mobile m, byte interval, ushort mode, ushort modeCount, bool repeat, bool forward)
        {
            FInterval.SetValue(m, interval);
            FRepeatMode.SetValue(m, mode);
            FRepeatModeCount.SetValue(m, modeCount);
            FRepeat.SetValue(m, repeat);
            FForward.SetValue(m, forward);
        }

        internal static void OldAdvance(Mobile m, int frames)
        {
            int currentDelay = Constants.CHARACTER_ANIMATION_DELAY;

            if (frames > 0)
            {
                // the base revision's reads and writes of the timer fields (Mobile.ProcessAnimation at 0b388a043)
                (byte interval, ushort mode, ushort modeCount, bool repeat, bool forward) = TimerFieldsOf(m);
                var timer = new AnimationTimer
                {
                    Frame = m.AnimIndex,
                    FrameCount = m.AnimationFrameCount,
                    Interval = interval,
                    RepeatMode = mode,
                    RepeatModeCount = modeCount,
                    Repeat = repeat,
                    Forward = forward,
                    FromServer = m.AnimationFromServer
                };

                currentDelay = AnimationTiming.Advance(ref timer, frames, out AnimationAdvance advance);

                m.AnimationFrameCount = timer.FrameCount;
                FRepeatMode.SetValue(m, timer.RepeatMode);
                FRepeatModeCount.SetValue(m, timer.RepeatModeCount);
                FRepeat.SetValue(m, timer.Repeat);

                if (advance == AnimationAdvance.Finished)
                {
                    m.SetAnimation(0xFF);
                }
                else if (advance == AnimationAdvance.Wrapped && (m.Serial & 0x80000000) != 0)
                {
                    m.World.CorpseManager.Remove(0, m.Serial);
                    m.World.RemoveMobile(m.Serial);
                }

                m.AnimIndex = timer.Frame;
            }
            else if ((m.Serial & 0x80000000) != 0)
            {
                m.World.CorpseManager.Remove(0, m.Serial);
                m.World.RemoveMobile(m.Serial);
            }

            m.LastAnimationChangeTime = Time.Ticks + currentDelay;
        }

        internal static void NewAdvance(World world, Mobile m, int frames)
        {
            if (world.Writer.AdvanceAnimation(m, frames, out int frame, out long due))
            {
                world.Writer.RemoveDeadMobile(m, frame, due);
            }
        }

        public enum AdvanceStart { Repeating, Once, Idle, Dead }

        private static World BuildAdvance(AdvanceStart start)
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);

            switch (start)
            {
                case AdvanceStart.Repeating: OldAnimate(m, AnimPacket.CharacterAnimation); break;
                case AdvanceStart.Once: OldAnimate(m, AnimPacket.NewCharacterAnimation); break;
                case AdvanceStart.Dead:
                    OldRekey(world, m);
                    world.CorpseManager.Add(0x4000_7900, DEAD, m.Direction, false);
                    OldAnimate(m, AnimPacket.Death);

                    break;
            }

            return world;
        }

        public static IEnumerable<object[]> AdvanceCases()
        {
            foreach (AdvanceStart s in Enum.GetValues(typeof(AdvanceStart)))
            {
                foreach (int frames in new[] { 5, 1, 0 })
                {
                    yield return new object[] { s, frames };
                }
            }
        }

        [Theory]
        [MemberData(nameof(AdvanceCases))]
        public void The_Frame_Advance_Equals_The_Old_Path(AdvanceStart start, int frames)
        {
            World oldWorld = BuildAdvance(start), newWorld = BuildAdvance(start);
            newWorld.Writer.ThrowOnViolation = true;
            uint serial = start == AdvanceStart.Dead ? DEAD : MOB;
            Mobile a = oldWorld.Mobiles.Get(serial), b = newWorld.Mobiles.Get(serial);

            // frame by frame, as Update runs it (it stops once the mobile is destroyed); the bystander too
            for (int i = 1; i <= 24 && !a.IsDestroyed; i++)
            {
                Time.Ticks = NOW + (uint)(i * 150);
                Pair(() => OldAdvance(a, frames), () => NewAdvance(newWorld, b, frames));
                Pair(() => OldAdvance(oldWorld.Mobiles.Get(OTHER), 3), () => NewAdvance(newWorld, newWorld.Mobiles.Get(OTHER), 3));
                AssertSameWorld(oldWorld, newWorld, serial);
                (b.AnimIndex, b.LastAnimationChangeTime, b.IsDestroyed).Should().Be((a.AnimIndex, a.LastAnimationChangeTime, a.IsDestroyed), $"frame {i}");
                newWorld.CorpseManager.Exists(0, DEAD).Should().Be(oldWorld.CorpseManager.Exists(0, DEAD));
            }

            Time.Ticks = NOW;

            if (start == AdvanceStart.Dead)
            {
                b.IsDestroyed.Should().BeTrue("the corpse animation ended and the mobile was removed");
                newWorld.CorpseManager.Exists(0, DEAD).Should().BeFalse();
            }

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        // ---- group 4: the per-frame offset interpolation between steps (Mobile.ProcessSteps' walk offset, BoatMovingManager's
        // boat offset, its components' and its passengers'). ProcessSteps needs Client.Game (its frame delay): its offset
        // writes are compared here as the operation it now calls; the boat runs the real manager against OldBoat

        internal static void OldInterpolateStep(Mobile m, sbyte x, sbyte y, sbyte z)
        {
            m.OffsetZ = z;
            m.OffsetX = x;
            m.OffsetY = y;
        }

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        [InlineData(Start.DestroyedHeld)]
        public void InterpolateStep_Equals_The_Old_Walk_Offset(Start start)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = true;
            Mobile a = oldWorld.Mobiles.Get(MOB), b = newWorld.Mobiles.Get(MOB);
            long clocks = newWorld.Ecs.ClockWrites;

            foreach ((sbyte x, sbyte y, sbyte z) in new (sbyte, sbyte, sbyte)[] { (4, -2, 1), (-22, 11, -3), (0, 0, 0), (9, 9, 0) })
            {
                Pair(() => OldInterpolateStep(a, x, y, z), () => newWorld.Writer.InterpolateStep(b, x, y, z));
                AssertSameWorld(oldWorld, newWorld);
                (b.OffsetX, b.OffsetY, b.OffsetZ).Should().Be(((float)x, (float)y, (float)z));
                b.Offset.Should().Be(a.Offset);
            }

            (newWorld.Ecs.ClockWrites - clocks).Should().Be(0);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        private static House AddBoatHouse(World world)
        {
            var house = new House(world, BOAT, 1, false);

            for (int i = 0; i < 3; i++)
            {
                var c = new Multi(world);
                c.Offset = new Microsoft.Xna.Framework.Vector3(i, -i, 0);
                house.Components.Add(c);
            }

            world.HouseManager.Add(BOAT, house);

            return house;
        }

        [Fact]
        public void The_Boat_Offsets_With_A_House_Equal_The_Old_Path()
        {
            World oldWorld = BuildBoat(Start.Standing), newWorld = BuildBoat(Start.Standing);
            newWorld.Writer.ThrowOnViolation = true;
            House oldHouse = AddBoatHouse(oldWorld), newHouse = AddBoatHouse(newWorld);
            var old = new OldBoat(oldWorld);
            BoatMovingManager boats = newWorld.BoatMovingManager;

            void Both(Action<OldBoat> o, Action<BoatMovingManager> n, uint ticks)
            {
                Time.Ticks = ticks;
                Pair(() => o(old), () => n(boats));
                Time.Ticks = NOW;
                AssertSameWorld(oldWorld, newWorld);

                foreach (uint serial in new[] { BOAT, MOB, BAG })
                {
                    BoatSnapshot(newWorld, serial).Should().Be(BoatSnapshot(oldWorld, serial), $"0x{serial:X8} at {ticks}");
                }

                newHouse.Components.Select(c => c.Offset).Should().Equal(oldHouse.Components.Select(c => c.Offset), $"components at {ticks}");
            }

            // a step north-east with both passengers; three frames mid-step (no step completes: a completion regenerates the
            // house, which needs the multi data), then the reset
            Both(o => o.AddStep(BOAT, 1001, 999, 2, Direction.Right), n => n.AddStep(BOAT, 3, Direction.Right, Direction.Right, 1001, 999, 2), NOW + 100);
            Both(o => { o.PushItemToList(MOB, 1, 0, -2); o.PushItemToList(BAG, -1, 1, 0); }, n => { n.PushItemToList(BOAT, MOB, 1, 0, -2); n.PushItemToList(BOAT, BAG, -1, 1, 0); }, NOW + 100);

            foreach (uint t in new uint[] { 150, 260, 410 })
            {
                Both(o => o.Update(BOAT), n => n.Update(), NOW + t);
            }

            Microsoft.Xna.Framework.Vector3 mid = newWorld.Items.Get(BOAT).Offset;
            mid.Should().NotBe(default(Microsoft.Xna.Framework.Vector3), "mid-step");
            newHouse.Components.Should().OnlyContain(c => c.Offset == mid);
            newWorld.Mobiles.Get(MOB).Offset.Should().Be(mid);
            Both(o => o.ClearSteps(BOAT), n => n.ClearSteps(BOAT), NOW + 450);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void JumpBoat_Equals_The_Old_Jump_And_Rejects_Another_Worlds()
        {
            World oldWorld = BuildBoat(Start.Standing), newWorld = BuildBoat(Start.Standing);
            newWorld.Writer.ThrowOnViolation = true;
            Item a = oldWorld.Items.Get(BOAT), b = newWorld.Items.Get(BOAT);

            foreach ((ushort x, ushort y, sbyte z) in new (ushort, ushort, sbyte)[] { (1003, 998, 0), (1003, 995, -5) })
            {
                Pair(() => a.SetInWorldTile(x, y, z), () => newWorld.Writer.JumpBoat(b, x, y, z));
                AssertSameWorld(oldWorld, newWorld);
                BoatSnapshot(newWorld, BOAT).Should().Be(BoatSnapshot(oldWorld, BOAT));
                (b.X, b.Y, b.Z).Should().Be((x, y, z));
            }

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();

            foreach (bool throws in new[] { true, false })
            {
                World world = BuildBoat(Start.Standing), other = BuildBoat(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Item foreign = other.Items.Get(BOAT);
                Action act = () => world.Writer.JumpBoat(foreign, 5, 5, 0);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }

                (foreign.X, foreign.Y).Should().Be(((ushort)1000, (ushort)1000));
                world.Writer.Violations.Should().Be(1);
            }
        }

        [Fact]
        public void The_Offset_Operations_Write_A_Stale_Walker_And_Reject_Another_Worlds()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildBoat(Start.Standing), other = BuildBoat(Start.Standing);
                world.Writer.ThrowOnViolation = throws;

                // ProcessSteps passes what the update loops iterate: a stale instance of this World is written as before T1
                Mobile stale = Mobile.Create(world, MOB);
                world.Writer.InterpolateStep(stale, 3, 4, 5);
                stale.Offset.Should().Be(new Microsoft.Xna.Framework.Vector3(3, 4, 5));
                world.Writer.Violations.Should().Be(0);

                Mobile foreign = other.Mobiles.Get(MOB);
                Item foreignBoat = other.Items.Get(BOAT);
                House foreignHouse = AddBoatHouse(other);
                Mobile strangerOnDeck = Mobile.Create(world, OTHER);

                foreach (Action act in new Action[]
                {
                    () => world.Writer.InterpolateStep(foreign, 1, 2, 3),
                    () => world.Writer.InterpolateBoat(foreignBoat, 1, 2, 3),
                    () => world.Writer.FollowBoatComponents(foreignBoat, foreignHouse),
                    () => world.Writer.FollowBoat(foreign, new Microsoft.Xna.Framework.Vector3(1, 2, 3)),
                    () => world.Writer.FollowBoat(strangerOnDeck, new Microsoft.Xna.Framework.Vector3(1, 2, 3))
                })
                {
                    if (throws)
                    {
                        act.Should().Throw<ArgumentException>();
                    }
                    else
                    {
                        act();
                    }
                }

                (foreign.Offset, foreignBoat.Offset, strangerOnDeck.Offset).Should().Be((default(Microsoft.Xna.Framework.Vector3), default(Microsoft.Xna.Framework.Vector3), default(Microsoft.Xna.Framework.Vector3)));
                foreignHouse.Components.Select(c => c.Offset.X).Should().Equal(0f, 1f, 2f);
                world.Writer.Violations.Should().Be(5);
            }

            World w = BuildBoat(Start.Standing);
            w.Writer.ThrowOnViolation = false;
            w.Ecs.BeginFold(w.Mobiles.Get(OTHER));
            w.Writer.InterpolateStep(w.Mobiles.Get(MOB), 1, 1, 1);
            w.Writer.InterpolateBoat(w.Items.Get(BOAT), 1, 1, 1);
            w.Ecs.EndFold(w.Mobiles.Get(OTHER));
            (w.Mobiles.Get(MOB).Offset, w.Items.Get(BOAT).Offset).Should().Be((default(Microsoft.Xna.Framework.Vector3), default(Microsoft.Xna.Framework.Vector3)), "a nested call is rejected");
            w.Writer.Violations.Should().Be(2);
        }

        // ---- the timer helpers against explicit expected states (Codex audit of part 7, P3 #1: the two-world test above no
        // longer shares them, and these pin what they read and write)

        [Fact]
        public void AnimationTimerState_Reads_Every_Timer_Field()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);

            foreach (bool flip in new[] { false, true })
            {
                m.AnimIndex = (byte)(flip ? 6 : 3);
                m.AnimationFrameCount = (byte)(flip ? 2 : 7);
                m.AnimationFromServer = !flip;
                SetTimerFields(m, (byte)(flip ? 0 : 2), (ushort)(flip ? 9 : 4), (ushort)(flip ? 1 : 5), repeat: !flip, forward: flip);

                AnimationTimer t = m.AnimationTimerState;

                (t.Frame, t.FrameCount, t.Interval, t.RepeatMode, t.RepeatModeCount, t.Repeat, t.Forward, t.FromServer).Should().Be(
                    flip
                        ? ((byte)6, (byte)2, (byte)0, (ushort)9, (ushort)1, false, true, false)
                        : ((byte)3, (byte)7, (byte)2, (ushort)4, (ushort)5, true, false, true));
            }
        }

        [Fact]
        public void ApplyAnimationTimer_Writes_The_Frame_Count_And_Repeat_State_Only()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);

            foreach (bool flip in new[] { false, true })
            {
                m.AnimIndex = 3;
                m.AnimationFrameCount = 7;
                m.AnimationFromServer = true;
                SetTimerFields(m, 2, 4, 5, repeat: flip, forward: false);
                var timer = new AnimationTimer
                {
                    Frame = 1, FrameCount = 9, Interval = 6, RepeatMode = 11, RepeatModeCount = 12, Repeat = !flip, Forward = true,
                    FromServer = false
                };

                m.ApplyAnimationTimer(in timer);

                (m.AnimIndex, m.AnimationFrameCount, m.AnimationFromServer).Should().Be(((byte)3, (byte)9, true), "the frame index is the caller's write");
                TimerFieldsOf(m).Should().Be(((byte)2, (ushort)11, (ushort)12, !flip, false), "interval and direction are SetAnimation's");
            }
        }

        // the operation (and its helpers) frame by frame against hand-computed states: (AnimIndex, AnimationFrameCount,
        // repeat mode, repeat mode count, repeat, from server, due - now)
        private static void ExpectAdvances(World world, Mobile m, int frames, params (byte Frame, byte Count, ushort Mode, ushort ModeCount, bool Repeat, bool Server, int Delay)[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                Time.Ticks = NOW + (uint)((i + 1) * 1000);
                world.Writer.AdvanceAnimation(m, frames, out _, out _).Should().BeFalse();
                (byte interval, ushort mode, ushort modeCount, bool repeat, bool forward) = TimerFieldsOf(m);

                (m.AnimIndex, m.AnimationFrameCount, mode, modeCount, repeat, m.AnimationFromServer, (int)(m.LastAnimationChangeTime - Time.Ticks))
                    .Should().Be(expected[i], $"advance {i + 1}");
            }

            Time.Ticks = NOW;
            world.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void A_Reverse_Server_Animation_Counts_Down_Then_Finishes_Into_The_Default_Action()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);
            Time.Ticks = NOW;
            m.SetAnimation(7, interval: 1, frameCount: 3, repeatCount: 1, repeat: false, forward: false, fromServer: true);
            (m.AnimIndex, m.AnimationFrameCount).Should().Be(((byte)3, (byte)3));

            // 80 + 80 * (interval + 1) ms per server frame; the fourth step runs below zero: the last data frame (frames - 1),
            // the repeat count ends, SetAnimation(0xFF) resets the timer, then the frame index is written; after it a client
            // animation (80 ms) that wraps
            ExpectAdvances(world, m, 5,
                (2, 3, 1, 1, false, true, 240),
                (1, 3, 1, 1, false, true, 240),
                (0, 3, 1, 1, false, true, 240),
                (4, 0, 0, 0, false, false, 240),
                (0, 0, 0, 0, false, false, 80));
            TimerFieldsOf(m).Should().Be(((byte)0, (ushort)0, (ushort)0, false, false));
        }

        [Fact]
        public void A_Repeated_Server_Animation_Runs_Its_Count_Then_Its_Repeat_Then_Loops()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);
            Time.Ticks = NOW;
            m.SetAnimation(9, interval: 0, frameCount: 2, repeatCount: 2, repeat: true, forward: true, fromServer: true);
            (m.AnimIndex, m.AnimationFrameCount).Should().Be(((byte)0, (byte)0));

            // the first step takes the data's frame count (3); each wrap takes one from the repeat mode; at zero the repeat
            // flag is used up (the count follows the mode, 0) and the animation goes on wrapping without finishing
            ExpectAdvances(world, m, 3,
                (1, 3, 2, 2, true, true, 160),
                (2, 3, 2, 2, true, true, 160),
                (0, 3, 1, 2, true, true, 160),
                (1, 3, 1, 2, true, true, 160),
                (2, 3, 1, 2, true, true, 160),
                (0, 3, 0, 0, false, true, 160),
                (1, 3, 0, 0, false, true, 160),
                (2, 3, 0, 0, false, true, 160),
                (0, 3, 0, 0, false, true, 160));
            TimerFieldsOf(m).Forward.Should().BeTrue();
        }

        [Fact]
        public void AdvanceAnimation_Pushes_No_Clock_Of_Its_Own()
        {
            World world = BuildAdvance(AdvanceStart.Once);
            Mobile m = world.Mobiles.Get(MOB);
            Time.Ticks = NOW + 500;
            long writes = world.Ecs.ClockWrites;

            world.Writer.AdvanceAnimation(m, 4, out _, out _).Should().BeFalse();
            Time.Ticks = NOW;

            (world.Ecs.ClockWrites - writes).Should().Be(0, "the update loops push the clock after Update, as before T1");
            m.LastAnimationChangeTime.Should().BeGreaterThan(NOW + 500);
        }

        [Fact]
        public void The_Frame_Advance_Operations_Reject_What_This_World_Does_Not_Hold()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildAdvance(AdvanceStart.Dead), other = BuildAdvance(AdvanceStart.Dead);
                world.Writer.ThrowOnViolation = throws;
                Mobile foreign = other.Mobiles.Get(DEAD);
                Mobile stranger = Mobile.Create(world, DEAD);
                int before = foreign.UpdateStateHash();
                var acts = new Action[] { () => world.Writer.AdvanceAnimation(foreign, 5, out _, out _), () => world.Writer.RemoveDeadMobile(stranger, 1, NOW), () => world.Writer.RemoveDeadMobile(foreign, 1, NOW) };

                foreach (Action act in acts)
                {
                    if (throws)
                    {
                        act.Should().Throw<ArgumentException>();
                    }
                    else
                    {
                        act();
                    }
                }

                foreign.UpdateStateHash().Should().Be(before);
                foreign.IsDestroyed.Should().BeFalse();
                stranger.IsDestroyed.Should().BeFalse();
                world.Mobiles.Get(DEAD).IsDestroyed.Should().BeFalse();
                other.CorpseManager.Exists(0, DEAD).Should().BeTrue();
                world.CorpseManager.Exists(0, DEAD).Should().BeTrue();
                world.Writer.Violations.Should().Be(3);
            }

            // a stale instance of this World (not held): written as before T1, unfolded, no violation
            World w = BuildAdvance(AdvanceStart.Repeating);
            w.Writer.ThrowOnViolation = true;
            Mobile stale = Mobile.Create(w, MOB);
            Time.Ticks = NOW + 300;
            w.Writer.AdvanceAnimation(stale, 3, out _, out _).Should().BeFalse();
            Time.Ticks = NOW;
            stale.LastAnimationChangeTime.Should().BeGreaterThan(NOW);
            w.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void The_Frame_Advance_Does_Not_Nest()
        {
            World world = BuildAdvance(AdvanceStart.Dead);
            world.Writer.ThrowOnViolation = false;
            Mobile m = world.Mobiles.Get(DEAD);
            int before = m.UpdateStateHash();
            world.Ecs.BeginFold(world.Mobiles.Get(OTHER));
            Time.Ticks = NOW + 300;
            world.Writer.AdvanceAnimation(m, 5, out _, out _).Should().BeFalse();
            world.Writer.RemoveDeadMobile(m, 1, NOW);
            Time.Ticks = NOW;
            world.Ecs.EndFold(world.Mobiles.Get(OTHER));

            world.Writer.Violations.Should().Be(2);
            m.UpdateStateHash().Should().Be(before);
            m.IsDestroyed.Should().BeFalse();
        }
    }

    [Collection(nameof(FunnelCheckCollection))]
    public class SmallWritersFunnelCheckTests : IDisposable
    {
        public SmallWritersFunnelCheckTests()
        {
            WorldWriterMobileTests.PrefillHitsTexts();
            FunnelCheck.Reset();
            FunnelCheck.Enabled = true;
        }

        public void Dispose()
        {
            FunnelCheck.Enabled = false;
            FunnelCheck.Reset();
        }

        [Fact]
        public void The_Small_Writers_Make_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            Time.Ticks = 100_000;
            var world = new World();
            Mobile m = world.GetOrCreateMobile(0x0000_3300);
            m.Graphic = 0x190;
            m.SetInWorldTile(1000, 1000, 0);
            Item bag = world.GetOrCreateItem(0x4000_7300);
            bag.SetInWorldTile(1001, 1000, 0);
            Item corpse = world.GetOrCreateItem(0x4000_7302);
            corpse.Graphic = 0x2006;
            corpse.SetInWorldTile(1002, 1000, 0);
            FunnelCheck.Reset();

            world.Writer.SetName(m, "m");
            world.Writer.SetName(bag, "bag");
            world.Writer.SetPrice(bag, 5);
            world.Writer.SetSpeedMode(m, CharacterSpeedType.CantRun);
            world.Writer.SetWarMode(m, true);
            world.Writer.SetDead(m, true);
            world.Writer.PlaceCorpseEquipment(corpse, 0x4000_7300, Layer.Pants).Should().BeSameAs(bag);
            world.Writer.PlaceCorpseEquipment(m, 0x4000_7304, Layer.Shirt).Should().NotBeNull(); // a worn child, re-keyed below
            world.Writer.PlaceCorpseEquipment(corpse, 0x4000_7303, Layer.Shirt).Should().NotBeNull();
            world.Writer.RekeyDeadMobile(m).Should().Be(0x8000_3300u);
            Time.Ticks = 100_500;
            world.Writer.AdvanceAnimation(m, 4, out _, out _);
            world.Writer.AdvanceAnimation(m, 0, out int f0, out long d0).Should().BeTrue();
            world.Writer.RemoveDeadMobile(m, f0, d0);
            m.IsDestroyed.Should().BeTrue();

            // the removed mobile has no record left, so its name reads null (T9: no cache)
            (m.Name, bag.Name, bag.Price, m.SpeedMode, m.IsDead, bag.Container).Should().Be(((string)null, "bag", 5u, CharacterSpeedType.CantRun, true, 0x4000_7302u));
            FunnelCheck.Total.Should().Be(0, "0x98 and the other name writers, 0x74's price, 0xBF.26, 0x72, 0xBF.19's dead flag, 0x89, 0xAF's re-key, the frame advance and the corpse animation's end");
        }

        [Fact]
        public void The_Offset_Interpolation_Makes_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            Time.Ticks = 100_000;
            var world = new World();
            Mobile m = world.GetOrCreateMobile(0x0000_3300);
            m.Graphic = 0x190;
            m.SetInWorldTile(1000, 1000, 0);
            Item boat = world.GetOrCreateItem(0x4000_7400);
            boat.Graphic = 0x3E4E;
            boat.SetInWorldTile(1001, 1000, 0);
            var house = new House(world, boat.Serial, 1, false);
            house.Components.Add(new Multi(world));
            FunnelCheck.Reset();

            world.Writer.InterpolateStep(m, 5, -5, 1);
            world.Writer.InterpolateBoat(boat, 2, 3, 0);
            world.Writer.FollowBoatComponents(boat, house);
            world.Writer.FollowBoat(m, boat.Offset);
            world.Writer.JumpBoat(boat, 1002, 1000, 0);

            (m.Offset, house.Components[0].Offset).Should().Be((boat.Offset, boat.Offset));
            FunnelCheck.Total.Should().Be(0, "ProcessSteps' walk offset, the boat's offset, its components' and its passengers', 0xF6's jump");
        }
    }
}
