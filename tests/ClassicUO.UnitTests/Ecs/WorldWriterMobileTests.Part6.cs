using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility;
using FluentAssertions;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 6 (ecs-transition-plan.md, T1): the placement fold of another mobile (the 0x77 E2 row), then group 4.
    // Same method as parts 4 and 5: a copy of the old code on one World, the funnel on another, identical starts,
    // everything compared (AssertSameWorld).
    public partial class WorldWriterMobileTests
    {
        static WorldWriterMobileTests() => PrefillHitsTexts();

        // the Hits/HitsMax setters refresh HitsPercentage and create its cached text (RenderedText needs the UO fonts): the
        // static cache is filled with stand-ins (not destroyed, so UpdateHits creates none)
        internal static void PrefillHitsTexts()
        {
            var cache = (RenderedText[])typeof(Entity).GetField("_hitsPercText", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

            for (int i = 0; i < cache.Length; i++)
            {
                cache[i] ??= (RenderedText)RuntimeHelpers.GetUninitializedObject(typeof(RenderedText));
            }
        }

        // ---- PlaceMobile of a mobile other than the player: folded (the tile's Sync and push rewrote UpsertMobile's values)

        [Theory]
        [InlineData(Start.None, 7)]
        [InlineData(Start.Standing, 1)]
        [InlineData(Start.Walking, 2)]
        [InlineData(Start.Unplaced, 3)]
        [InlineData(Start.FullQueue, 2)]
        public void PlaceMobile_Of_Another_Mobile_Writes_No_Second_Record(Start start, int packet)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            MobPacket p = Packets[packet];
            long writes = 0;

            Pair
            (
                () => OldUpsertMobile(oldWorld, p),
                () => NewUpsertMobile(newWorld, p, _ => writes = newWorld.Ecs.MobileMirrorWrites)
            );

            newWorld.Ecs.MobileMirrorWrites.Should().Be(writes, "the placement of another mobile is folded into UpsertMobile's commit");
            newWorld.Ecs.IsFolding.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);
        }

        // ---- group 4, status and hits: 0xA1, 0xA2, 0xA3, 0x2D, 0x11, 0x16/0x17 (the handlers' world writes, old and new)

        internal static void OldUpdateHitpoints(World world, uint serial, ushort hitsMax, ushort hits)
        {
            Entity entity = world.Get(serial);

            if (entity == null)
            {
                return;
            }

            OldScalarWrites.HitsMax(entity, hitsMax);
            OldScalarWrites.Hits(entity, hits);

            if (entity.HitsRequest == HitsRequestStatus.Pending)
            {
                entity.HitsRequest = HitsRequestStatus.Received;
            }
        }

        internal static void NewUpdateHitpoints(World world, uint serial, ushort hitsMax, ushort hits)
        {
            Entity entity = world.Get(serial);

            if (entity == null)
            {
                return;
            }

            world.Writer.SetHits(entity, hitsMax, hits);
        }

        internal static void OldUpdateMana(World world, uint serial, ushort max, ushort value, bool stamina)
        {
            Mobile mobile = world.Mobiles.Get(serial);

            if (mobile == null)
            {
                return;
            }

            if (stamina)
            {
                OldScalarWrites.StaminaMax(mobile, max);
                OldScalarWrites.Stamina(mobile, value);
            }
            else
            {
                OldScalarWrites.ManaMax(mobile, max);
                OldScalarWrites.Mana(mobile, value);
            }
        }

        internal static void NewUpdateMana(World world, uint serial, ushort max, ushort value, bool stamina)
        {
            Mobile mobile = world.Mobiles.Get(serial);

            if (mobile == null)
            {
                return;
            }

            if (stamina)
            {
                world.Writer.SetStamina(mobile, max, value);
            }
            else
            {
                world.Writer.SetMana(mobile, max, value);
            }
        }

        internal struct AttrPacket
        {
            public uint Serial;
            public ushort HitsMax, Hits, ManaMax, Mana, StaminaMax, Stamina;
        }

        internal static void OldMobileAttributes(World world, in AttrPacket a)
        {
            Entity entity = world.Get(a.Serial);

            if (entity == null)
            {
                return;
            }

            OldScalarWrites.HitsMax(entity, a.HitsMax);
            OldScalarWrites.Hits(entity, a.Hits);

            if (entity.HitsRequest == HitsRequestStatus.Pending)
            {
                entity.HitsRequest = HitsRequestStatus.Received;
            }

            if (SerialHelper.IsMobile(a.Serial))
            {
                if (!(entity is Mobile mobile))
                {
                    return;
                }

                OldScalarWrites.ManaMax(mobile, a.ManaMax);
                OldScalarWrites.Mana(mobile, a.Mana);
                OldScalarWrites.StaminaMax(mobile, a.StaminaMax);
                OldScalarWrites.Stamina(mobile, a.Stamina);
            }
        }

        internal static void NewMobileAttributes(World world, in AttrPacket a)
        {
            Entity entity = world.Get(a.Serial);

            if (entity == null)
            {
                return;
            }

            world.Writer.SetHits(entity, a.HitsMax, a.Hits);

            if (SerialHelper.IsMobile(a.Serial))
            {
                if (!(entity is Mobile mobile))
                {
                    return;
                }

                world.Writer.SetMana(mobile, a.ManaMax, a.Mana);
                world.Writer.SetStamina(mobile, a.StaminaMax, a.Stamina);
            }
        }

        internal struct StatusPacket
        {
            public uint Serial;
            public string Name;
            public ushort Hits, HitsMax;
            public bool Renamable;
            public byte Type; // 0: no extended part; 3: Renaissance; 5: ML (the race)
            public bool Female;
            public ushort Stamina, StaminaMax, Mana, ManaMax;
            public byte Race;
        }

        // CharacterStatus' world writes (the prints, the window title, the stats that are not world state and UoAssist are
        // left out: they are the same code on both paths)
        internal static void OldCharacterStatus(World world, in StatusPacket s)
        {
            Entity entity = world.Get(s.Serial);

            if (entity == null)
            {
                return;
            }

            OldScalarWrites.Name(entity, s.Name);
            OldScalarWrites.Hits(entity, s.Hits);
            OldScalarWrites.HitsMax(entity, s.HitsMax);

            if (entity.HitsRequest == HitsRequestStatus.Pending)
            {
                entity.HitsRequest = HitsRequestStatus.Received;
            }

            if (SerialHelper.IsMobile(s.Serial))
            {
                if (!(entity is Mobile mobile))
                {
                    return;
                }

                mobile.TestSetIsRenamable(s.Renamable);

                if (s.Type > 0)
                {
                    mobile.TestSetIsFemale(s.Female);

                    if (mobile == world.Player)
                    {
                        OldScalarWrites.Stamina(world.Player, s.Stamina);
                        OldScalarWrites.StaminaMax(world.Player, s.StaminaMax);
                        OldScalarWrites.Mana(world.Player, s.Mana);
                        OldScalarWrites.ManaMax(world.Player, s.ManaMax);

                        if (s.Type >= 5)
                        {
                            world.Player.TestSetRace((RaceType)(s.Race == 0 ? 1 : s.Race));
                        }
                    }
                }
            }
        }

        internal static void NewCharacterStatus(World world, in StatusPacket s)
        {
            Entity entity = world.Get(s.Serial);

            if (entity == null)
            {
                return;
            }

            world.Writer.SetStatus(entity, s.Name, s.Hits, s.HitsMax);

            if (SerialHelper.IsMobile(s.Serial))
            {
                if (!(entity is Mobile mobile))
                {
                    return;
                }

                bool extended = s.Type > 0;
                world.Writer.SetMobileStatus(mobile, s.Renamable, extended, extended && s.Female);

                if (extended && mobile == world.Player)
                {
                    world.Writer.SetStamina(world.Player, s.StaminaMax, s.Stamina);
                    world.Writer.SetMana(world.Player, s.ManaMax, s.Mana);

                    if (s.Type >= 5)
                    {
                        world.Writer.SetRace(world.Player, (RaceType)(s.Race == 0 ? 1 : s.Race));
                    }
                }
            }
        }

        // 0x16/0x17: (type, enabled) entries; cv7000 as Client.Game.UO.Version >= CV_7000
        internal static void OldHealthbar(World world, uint serial, (ushort Type, bool Enabled)[] list, bool cv7000)
        {
            Mobile mobile = world.Mobiles.Get(serial);

            if (mobile == null)
            {
                return;
            }

            foreach ((ushort type, bool enabled) in list)
            {
                if (type == 1)
                {
                    if (cv7000)
                    {
                        mobile.SetSAPoison(enabled);
                    }
                    else if (enabled)
                    {
                        mobile.TestSetFlags(mobile.Flags | Flags.Poisoned);
                    }
                    else
                    {
                        mobile.TestSetFlags(mobile.Flags & ~Flags.Poisoned);
                    }
                }
                else if (type == 2)
                {
                    if (enabled)
                    {
                        mobile.TestSetFlags(mobile.Flags | Flags.YellowBar);
                    }
                    else
                    {
                        mobile.TestSetFlags(mobile.Flags & ~Flags.YellowBar);
                    }
                }
            }

            world.Ecs.Sync(mobile);
        }

        internal static void NewHealthbar(World world, uint serial, (ushort Type, bool Enabled)[] list, bool cv7000)
        {
            Mobile mobile = world.Mobiles.Get(serial);

            if (mobile == null)
            {
                return;
            }

            Flags flags = mobile.Flags;
            bool setSAPoison = false, saPoison = false;

            foreach ((ushort type, bool enabled) in list)
            {
                if (type == 1)
                {
                    if (cv7000)
                    {
                        setSAPoison = true;
                        saPoison = enabled;
                    }
                    else if (enabled)
                    {
                        flags |= Flags.Poisoned;
                    }
                    else
                    {
                        flags &= ~Flags.Poisoned;
                    }
                }
                else if (type == 2)
                {
                    if (enabled)
                    {
                        flags |= Flags.YellowBar;
                    }
                    else
                    {
                        flags &= ~Flags.YellowBar;
                    }
                }
            }

            world.Writer.SetHealthbar(mobile, flags, setSAPoison, saPoison);
        }

        public enum StatusStart { Fresh, Known, Pending, ZeroMax }

        // MOB, BAG and OTHER with a status start: Known has a name, hits, mana and stamina; Pending also a pending hits
        // request; ZeroMax had a HitsMax and then a zero one (the percentage stays from before, so the write order shows)
        private static World BuildStatus(Start start, StatusStart status)
        {
            World world = Build(start);

            foreach (Entity e in new Entity[] { world.Mobiles.Get(MOB), world.Items.Get(BAG), world.Mobiles.Get(OTHER) })
            {
                if (e == null || status == StatusStart.Fresh)
                {
                    continue;
                }

                OldScalarWrites.Name(e, "start");
                OldScalarWrites.HitsMax(e, 100);
                OldScalarWrites.Hits(e, 77);

                if (e is Mobile m)
                {
                    OldScalarWrites.ManaMax(m, 50);
                    OldScalarWrites.Mana(m, 20);
                    OldScalarWrites.StaminaMax(m, 60);
                    OldScalarWrites.Stamina(m, 30);
                }

                if (status == StatusStart.Pending)
                {
                    e.HitsRequest = HitsRequestStatus.Pending;
                }
                else if (status == StatusStart.ZeroMax)
                {
                    OldScalarWrites.HitsMax(e, 0);
                }
            }

            return world;
        }

        public static IEnumerable<object[]> StatusCases()
        {
            foreach (Start s in new[] { Start.None, Start.Standing, Start.Walking, Start.DestroyedHeld })
            {
                foreach (StatusStart st in Enum.GetValues(typeof(StatusStart)))
                {
                    yield return new object[] { s, st };
                }
            }
        }

        [Theory]
        [MemberData(nameof(StatusCases))]
        public void The_Hits_Mana_Stamina_And_Attributes_Equal_The_Old_Path(Start start, StatusStart status)
        {
            World oldWorld = BuildStatus(start, status), newWorld = BuildStatus(start, status);
            newWorld.Writer.ThrowOnViolation = true;

            // 0xA1 on a mobile, an item and a missing serial; a zero HitsMax after a non-zero one, and back
            var hitsPackets = new (uint Serial, ushort Max, ushort Hits)[]
            {
                (MOB, 120, 60), (BAG, 40, 40), (0x0000_3999, 1, 1), (MOB, 0, 5), (OTHER, 0, 0), (MOB, 90, 200)
            };

            foreach ((uint serial, ushort max, ushort hits) in hitsPackets)
            {
                Pair(() => OldUpdateHitpoints(oldWorld, serial, max, hits), () => NewUpdateHitpoints(newWorld, serial, max, hits));
                AssertSameWorld(oldWorld, newWorld);
            }

            // 0xA2, 0xA3
            Pair(() => OldUpdateMana(oldWorld, MOB, 70, 35, false), () => NewUpdateMana(newWorld, MOB, 70, 35, false));
            Pair(() => OldUpdateMana(oldWorld, MOB, 80, 79, true), () => NewUpdateMana(newWorld, MOB, 80, 79, true));
            Pair(() => OldUpdateMana(oldWorld, OTHER, 1, 2, true), () => NewUpdateMana(newWorld, OTHER, 1, 2, true));
            Pair(() => OldUpdateMana(oldWorld, BAG, 1, 2, false), () => NewUpdateMana(newWorld, BAG, 1, 2, false));
            AssertSameWorld(oldWorld, newWorld);

            // 0x2D on a mobile, then an item (the item branch: no mana or stamina), then a zero HitsMax
            foreach (AttrPacket a in new[]
            {
                new AttrPacket { Serial = MOB, HitsMax = 150, Hits = 149, ManaMax = 10, Mana = 9, StaminaMax = 8, Stamina = 7 },
                new AttrPacket { Serial = BAG, HitsMax = 3, Hits = 1, ManaMax = 10, Mana = 9, StaminaMax = 8, Stamina = 7 },
                new AttrPacket { Serial = OTHER, HitsMax = 0, Hits = 9, ManaMax = 0, Mana = 0, StaminaMax = 1, Stamina = 1 }
            })
            {
                Pair(() => OldMobileAttributes(oldWorld, a), () => NewMobileAttributes(newWorld, a));
                AssertSameWorld(oldWorld, newWorld);
            }

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Theory]
        [MemberData(nameof(StatusCases))]
        public void CharacterStatus_Equals_The_Old_Path(Start start, StatusStart status)
        {
            World oldWorld = BuildStatus(start, status), newWorld = BuildStatus(start, status);
            newWorld.Writer.ThrowOnViolation = true;

            foreach (StatusPacket s in new[]
            {
                // the short form (no extended part), a mobile
                new StatusPacket { Serial = MOB, Name = "Iolo", Hits = 30, HitsMax = 60, Renamable = true },
                // the extended part: female (an input)
                new StatusPacket { Serial = MOB, Name = "Iolo", Hits = 61, HitsMax = 60, Renamable = false, Type = 3, Female = true },
                // the short form again: IsFemale stays
                new StatusPacket { Serial = MOB, Name = "Iolo", Hits = 62, HitsMax = 60, Renamable = true },
                // a zero HitsMax written after the hits (0x11's order), male again
                new StatusPacket { Serial = MOB, Name = "Dupre", Hits = 10, HitsMax = 0, Type = 6, Female = false },
                // an item's status, another mobile's, a missing serial
                new StatusPacket { Serial = BAG, Name = "a bag", Hits = 5, HitsMax = 10 },
                new StatusPacket { Serial = OTHER, Name = "Shamino", Hits = 1, HitsMax = 2, Type = 4, Female = true },
                new StatusPacket { Serial = 0x0000_3999, Name = "nobody", Hits = 1, HitsMax = 1 }
            })
            {
                Pair(() => OldCharacterStatus(oldWorld, s), () => NewCharacterStatus(newWorld, s));
                AssertSameWorld(oldWorld, newWorld);
            }

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(6)]
        public void CharacterStatus_Of_The_Player_Equals_The_Old_Path(byte type)
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            b.Funnel = true; // the new World's callbacks walk through the converted Walk path
            newWorld.Writer.ThrowOnViolation = true;

            foreach (StatusPacket s in new[]
            {
                new StatusPacket { Serial = MOB, Name = "Avatar", Hits = 90, HitsMax = 100, Renamable = false, Type = type, Female = true, Stamina = 40, StaminaMax = 41, Mana = 42, ManaMax = 43, Race = 3 },
                new StatusPacket { Serial = MOB, Name = "Avatar", Hits = 100, HitsMax = 100, Type = type, Female = false, Stamina = 1, StaminaMax = 2, Mana = 3, ManaMax = 4, Race = 0 }
            })
            {
                Pair(() => OldCharacterStatus(oldWorld, s), () => NewCharacterStatus(newWorld, s));
                AssertSameWorld(oldWorld, newWorld);
                StatusOf(b).Should().Be(StatusOf(a));
            }

            (b.Mana, b.ManaMax, b.Stamina, b.StaminaMax).Should().Be(((ushort)3, (ushort)4, (ushort)1, (ushort)2));
            newWorld.Writer.Violations.Should().Be(0);
        }

        public static IEnumerable<object[]> HealthbarCases()
        {
            for (int i = 0; i < HealthbarLists.Length; i++)
            {
                foreach (Start s in new[] { Start.Standing, Start.Walking, Start.DestroyedHeld })
                {
                    yield return new object[] { s, i, true };
                    yield return new object[] { s, i, false };
                }
            }
        }

        private static readonly (ushort Type, bool Enabled)[][] HealthbarLists =
        {
            new (ushort, bool)[0],
            new (ushort, bool)[] { (1, true) },
            new (ushort, bool)[] { (1, false), (2, true) },
            new (ushort, bool)[] { (2, true), (1, true), (3, true) },
            new (ushort, bool)[] { (1, true), (1, false), (2, true), (2, false) }
        };

        [Theory]
        [MemberData(nameof(HealthbarCases))]
        public void NewHealthbarUpdate_Equals_The_Old_Path(Start start, int list, bool cv7000)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = true;
            (ushort, bool)[] entries = HealthbarLists[list];

            // MOB starts poisoned (both ways), so clearing shows; OTHER starts clear
            foreach (World w in new[] { oldWorld, newWorld })
            {
                Mobile m = w.Mobiles.Get(MOB);

                if (m != null && !m.IsDestroyed)
                {
                    m.TestSetFlags(Flags.Poisoned | Flags.Hidden);
                    m.SetSAPoison(true);
                    w.Ecs.Sync(m);
                }
            }

            Pair(() => OldHealthbar(oldWorld, MOB, entries, cv7000), () => NewHealthbar(newWorld, MOB, entries, cv7000));
            AssertSameWorld(oldWorld, newWorld);

            Pair(() => OldHealthbar(oldWorld, OTHER, entries, !cv7000), () => NewHealthbar(newWorld, OTHER, entries, !cv7000));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void SetHealthbar_Commits_Its_Flags_Before_The_Writes()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e);
            long pushes = e.GetComponent<MobileInputs>().Pushes;
            long writes = world.Ecs.MobileMirrorWrites;
            string seen = null;
            world.Ecs.MobileCommitObserver = (mob, v) => seen = $"{v.Flags} cache {mob.Flags} sa {mob.SAPoisoned}";
            world.Writer.SetHealthbar(m, Flags.YellowBar | Flags.Poisoned, true, true);
            seen.Should().Be($"{Flags.YellowBar | Flags.Poisoned} cache {Flags.None} sa False", "the record is committed from the values, before the cache writes");
            (m.Flags, m.SAPoisoned).Should().Be((Flags.YellowBar | Flags.Poisoned, true));

            // folded: one record write and one inputs push (SetSAPoison's own push is folded)
            (world.Ecs.MobileMirrorWrites - writes).Should().Be(1);
            ((long)e.GetComponent<MobileInputs>().Pushes - pushes).Should().Be(1L);
        }

        [Fact]
        public void The_Detached_World_Player_Takes_The_Status_As_Before_T1()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildPlayer(out TestPlayer player);
                world.Writer.ThrowOnViolation = throws;
                // a 0x25 under the player's serial: the mobile goes, World.Player stays (not held)
                world.Writer.PlaceInContainer(MOB, 0xE76, 1, 10, 10, 0, BAG, out _).Should().NotBeNull();
                world.Mobiles.Get(MOB).Should().BeNull();
                world.Player.Should().BeSameAs(player);

                world.Writer.SetStatus(player, "ghost", 1, 2);
                world.Writer.SetMobileStatus(player, true, true, true);
                world.Writer.SetMana(player, 9, 8);
                world.Writer.SetStamina(player, 7, 6);
                world.Writer.SetRace(player, RaceType.ELF);
                world.Writer.SetHits(player, 5, 4);
                world.Writer.SetHealthbar(player, Flags.YellowBar, true, true);

                world.Writer.Violations.Should().Be(0);
                // T9: the detached player has no record, so the cold values are not kept (a misconfigured server's case); the
                // percentage, the hot fields and the inputs are written as before
                // (T9 S5: the traits too: renamable, race and sex; T9 S6: and the flags)
                StatusOf(player).Should().Be(" 0/0 80% None mana 0/0 stamina 0/0 renamable False race 0 sa True");
                (player.IsFemale, player.Flags).Should().Be((false, Flags.None));

                // any other instance World does not hold is still rejected
                Mobile stranger = Mobile.Create(world, 0x0000_3777);
                Action act = () => world.Writer.SetHits(stranger, 9, 9);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }

                stranger.Hits.Should().Be(0);
                world.Writer.Violations.Should().Be(1);
            }
        }

        [Fact]
        public void SetHits_And_SetStatus_Of_An_Item_This_World_Does_Not_Hold_Are_Rejected()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = Build(Start.Standing), other = Build(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Item foreign = other.Items.Get(BAG);
                Action hits = () => world.Writer.SetHits(foreign, 9, 9);
                Action status = () => world.Writer.SetStatus(foreign, "x", 9, 9);

                foreach (Action act in new[] { hits, status })
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

                StatusOf(foreign).Should().Be(StatusOf(Build(Start.Standing).Items.Get(BAG)));
                world.Writer.Violations.Should().Be(2);
            }
        }

        // ---- group 4.2, the server's animations: 0x6E, 0xE2, 0xAF's death animation, 0xBF.19 and 0xBF.2B's held frame

        public enum AnimPacket { CharacterAnimation, NewCharacterAnimation, Death, HeldFrame19, HeldFrame2B }

        internal static void OldAnimate(Mobile m, AnimPacket kind)
        {
            switch (kind)
            {
                case AnimPacket.CharacterAnimation: m.SetAnimation(14, 3, 7, 2, true, false, true); break;
                case AnimPacket.NewCharacterAnimation: m.SetAnimation(9, repeatCount: 1, repeat: false, forward: true, fromServer: true); break;
                case AnimPacket.Death:
                    m.SetAnimation(21, 0, 5, 1);
                    m.SetFrame(0);

                    break;
                case AnimPacket.HeldFrame19:
                    m.SetAnimation(17);
                    m.SetExecuteAnimation(false);
                    m.SetFrame(4);

                    break;
                default:
                    m.SetAnimation(6);
                    m.SetFrame(2);
                    m.SetExecuteAnimation(false);

                    break;
            }
        }

        internal static void NewAnimate(World world, Mobile m, AnimPacket kind)
        {
            switch (kind)
            {
                case AnimPacket.CharacterAnimation: world.Writer.AnimateMobile(m, 14, 3, 7, 2, true, false, true); break;
                case AnimPacket.NewCharacterAnimation: world.Writer.AnimateMobile(m, 9, repeatCount: 1, repeat: false, forward: true, fromServer: true); break;
                case AnimPacket.Death: world.Writer.AnimateMobile(m, 21, 0, 5, 1, frame: 0); break;
                case AnimPacket.HeldFrame19: world.Writer.AnimateMobile(m, 17, frame: 4, hold: true); break;
                default: world.Writer.AnimateMobile(m, 6, frame: 2, hold: true); break;
            }
        }

        public static IEnumerable<object[]> AnimCases()
        {
            foreach (Start s in new[] { Start.Standing, Start.Walking, Start.DestroyedHeld })
            {
                foreach (AnimPacket k in Enum.GetValues(typeof(AnimPacket)))
                {
                    yield return new object[] { s, k };
                }
            }
        }

        [Theory]
        [MemberData(nameof(AnimCases))]
        public void AnimateMobile_Equals_The_Old_Path(Start start, AnimPacket kind)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = true;

            // a later tick than the start's, so the due times differ from the start's clock
            Time.Ticks = NOW + 777;
            Pair(() => OldAnimate(oldWorld.Mobiles.Get(MOB), kind), () => NewAnimate(newWorld, newWorld.Mobiles.Get(MOB), kind));
            AssertSameWorld(oldWorld, newWorld);

            // then another kind on the bystander, a frame later
            Time.Ticks = NOW + 1555;
            AnimPacket next = (AnimPacket)(((int)kind + 1) % 5);
            Pair(() => OldAnimate(oldWorld.Mobiles.Get(OTHER), next), () => NewAnimate(newWorld, newWorld.Mobiles.Get(OTHER), next));
            Time.Ticks = NOW;
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
        }

        [Fact]
        public void AnimateMobile_Commits_The_Clock_Once()
        {
            World world = Build(Start.Standing);
            Mobile m = world.Mobiles.Get(MOB);
            Time.Ticks = NOW + 999;
            long pushes = world.Ecs.ClockWrites;
            world.Writer.AnimateMobile(m, 14, 3, 7, 2, true, false, true, frame: 1, hold: true);
            Time.Ticks = NOW;

            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e);
            e.GetComponent<MobileClock>().AnimDue.Should().Be(m.LastAnimationChangeTime);
            e.GetComponent<MobileClock>().IdleDue.Should().Be(m.IdleDue);
            (m.AnimIndex, m.ExecuteAnimation).Should().Be(((byte)1, false));
            // T9 S8c: each timer write keeps the clock itself (no count of commits: the record is the only copy)
        }

        [Fact]
        public void AnimateMobile_Of_A_Mobile_This_World_Does_Not_Hold_Changes_Nothing()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = Build(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Mobile stranger = Mobile.Create(world, MOB);
                int before = stranger.UpdateStateHash();
                RandomHelper.State r = RandomHelper.Save();
                Action act = () => world.Writer.AnimateMobile(stranger, 12, 2, 5, 1, frame: 3, hold: true);

                if (throws)
                {
                    act.Should().Throw<ArgumentException>();
                }
                else
                {
                    act();
                }

                stranger.UpdateStateHash().Should().Be(before);
                stranger.ExecuteAnimation.Should().BeTrue();
                RandomHelper.Save().Should().Be(r, "no idle delay drawn");
                world.Writer.Violations.Should().Be(1);
            }
        }

        // ---- group 4.3 (misc), the boat: BoatMovingManager's step completion, passengers, step clock and offset reset. The
        // real manager runs on the funnel World; OldBoat is its pre-T1 code for one boat on the other World.

        internal sealed class OldBoat
        {
            private struct Step
            {
                public int TimeDiff;
                public ushort X, Y;
                public sbyte Z;
                public Direction MovingDir;
            }

            private readonly World _world;
            private readonly List<Step> _steps = new List<Step>();
            private readonly List<(uint Serial, int X, int Y, int Z)> _items = new List<(uint, int, int, int)>();
            private uint _timePacket;

            public OldBoat(World world) => _world = world;

            public void AddStep(uint serial, ushort x, ushort y, sbyte z, Direction dir)
            {
                Item item = _world.Items.Get(serial);
                bool empty = _steps.Count == 0;

                if (empty)
                {
                    item.LastStepTime = Time.Ticks;
                }

                _steps.Add(new Step { TimeDiff = _timePacket == 0 || empty ? 500 : (int)(Time.Ticks - _timePacket), X = x, Y = y, Z = z, MovingDir = dir });
                _items.Clear(); // ClearEntities
                _timePacket = Time.Ticks;
            }

            public void PushItemToList(uint objSerial, int x, int y, int z) => _items.Add((objSerial, x, y, z));

            public void ClearSteps(uint serial)
            {
                if (_steps.Count == 0)
                {
                    return;
                }

                Item multiItem = _world.Items.Get(serial);
                SetOffset(_world, multiItem, default);

                foreach (var it in _items)
                {
                    Entity ent = _world.Get(it.Serial);

                    if (ent != null)
                    {
                        SetOffset(_world, ent, default);
                    }
                }

                _items.Clear();
                _steps.Clear();
            }

            public void Update(uint serial)
            {
                while (_steps.Count != 0)
                {
                    Step step = _steps[0];
                    Item item = _world.Items.Get(serial);
                    int maxDelay = step.TimeDiff;
                    int delay = (int)Time.Ticks - (int)item.LastStepTime;
                    bool removeStep = delay >= maxDelay;
                    bool directionChange = false;

                    if (item.X != step.X || item.Y != step.Y)
                    {
                        if (maxDelay != 0)
                        {
                            float steps = maxDelay / (float)Constants.CHARACTER_ANIMATION_DELAY;
                            float x = delay / (float)Constants.CHARACTER_ANIMATION_DELAY;
                            float y = x;
                            float oz = (sbyte)((step.Z - item.Z) * x * (4.0f / steps));
                            MovementSpeed.GetPixelOffset((byte)step.MovingDir, ref x, ref y, steps);
                            SetOffset(_world, item, new Microsoft.Xna.Framework.Vector3((sbyte)x, (sbyte)y, oz));
                        }
                    }
                    else
                    {
                        directionChange = true;
                        removeStep = true;
                    }

                    if (removeStep)
                    {
                        item.X = step.X;
                        item.Y = step.Y;
                        item.Z = step.Z;
                        item.UpdateScreenPosition();
                        SetOffset(_world, item, default);
                        _steps.RemoveAt(0);

                        if (item.IsPlaced)
                        {
                            item.AddToTile();
                        }

                        Inside(true, step.X, step.Y, step.Z, item);
                        item.LastStepTime = Time.Ticks;
                    }
                    else
                    {
                        // part 7, group 4: the boat's house components follow its offset (no house in the part 6 tests)
                        if (_world.HouseManager.TryGetHouse(item, out House house))
                        {
                            foreach (Multi c in house.Components)
                            {
                                c.Offset = item.ScreenOffset;
                            }
                        }

                        Inside(false, item.X, item.Y, item.Z, item);
                    }

                    if (!directionChange)
                    {
                        break;
                    }
                }
            }

            private void Inside(bool removeStep, int x, int y, int z, Item item)
            {
                foreach (var it in _items)
                {
                    Entity entity = _world.Get(it.Serial);

                    if (entity == null || entity.IsDestroyed)
                    {
                        continue;
                    }

                    if (removeStep)
                    {
                        entity.X = (ushort)(x - it.X);
                        entity.Y = (ushort)(y - it.Y);
                        entity.Z = (sbyte)(z - it.Z);
                        entity.UpdateScreenPosition();
                        SetOffset(_world, entity, default);

                        if (entity.IsPlaced)
                        {
                            entity.AddToTile();
                        }
                    }
                    else
                    {
                        SetOffset(_world, entity, item.ScreenOffset);
                    }
                }
            }
        }

        private const uint BOAT = 0x4000_7400;

        // the old code's offset writes, where each kind keeps its offset since T9 S7: a mobile's on the Mobile, an item's in
        // its ItemOffset component (the boat operations' ECS write)
        private static void SetOffset(World w, Entity e, Microsoft.Xna.Framework.Vector3 v)
        {
            if (e is Mobile m)
            {
                m.SetOffset(v);
            }
            else
            {
                ItemOffsets.Push(w, e, v);
            }
        }

        private static World BuildBoat(Start start)
        {
            World world = Build(start);
            Item boat = world.GetOrCreateItem(BOAT);
            boat.Graphic = 0x3E4E;
            boat.SetInWorldTile(1000, 1000, 0);
            boat.PendingBlock = 7; // parked: shows whether AddToTile ran (these Worlds have no map)
            world.Ecs.Sync(boat);
            boat.UpdateRealScreenPositionOnly(0, 0); // as after a frame: a step's screen refresh shows

            foreach (uint serial in new[] { MOB, BAG })
            {
                if (world.Get(serial) is Entity e && !e.IsDestroyed)
                {
                    e.PendingBlock = 7;
                    e.UpdateRealScreenPositionOnly(0, 0);
                }
            }

            return world;
        }

        private static string BoatSnapshot(World w, uint serial)
        {
            Entity e = w.Get(serial);

            return e == null ? "none" : $"{e.X},{e.Y},{e.Z} {e.ScreenOffset} {(e is Mobile em ? em.LastStepTime : e is Item ei ? ei.LastStepTime : 0u)} {e.PendingBlock} {e.IsPositionChanged}";
        }

        [Theory]
        [InlineData(Start.Standing)]
        [InlineData(Start.Walking)]
        [InlineData(Start.DestroyedHeld)]
        public void The_Boat_Steps_Equal_The_Old_Path(Start start)
        {
            World oldWorld = BuildBoat(start), newWorld = BuildBoat(start);
            newWorld.Writer.ThrowOnViolation = true;
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
            }

            // a step east with the mobile and the bag on deck; mid-step (offsets), then done (positions, tiles, the clock)
            Both(o => o.AddStep(BOAT, 1001, 1000, 0, Direction.East), n => n.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0), NOW + 100);
            Both(o => { o.PushItemToList(MOB, 1, 0, -2); o.PushItemToList(BAG, -1, 1, 0); }, n => { n.PushItemToList(BOAT, MOB, 1, 0, -2); n.PushItemToList(BOAT, BAG, -1, 1, 0); }, NOW + 100);
            Both(o => o.Update(BOAT), n => n.Update(), NOW + 300);
            Both(o => o.Update(BOAT), n => n.Update(), NOW + 700);
            newWorld.Items.Get(BOAT).X.Should().Be(1001, "the step completed");

            // a second step, mid-way, then the reset (the custom house's ClearSteps)
            Both(o => o.AddStep(BOAT, 1002, 1001, 1, Direction.Down), n => n.AddStep(BOAT, 3, Direction.Down, Direction.Down, 1002, 1001, 1), NOW + 800);
            Both(o => { o.PushItemToList(MOB, 1, 0, 0); o.PushItemToList(BAG, -1, 1, 0); }, n => { n.PushItemToList(BOAT, MOB, 1, 0, 0); n.PushItemToList(BOAT, BAG, -1, 1, 0); }, NOW + 800);
            Both(o => o.Update(BOAT), n => n.Update(), NOW + 1000);
            newWorld.Items.Get(BOAT).ScreenOffset.Should().NotBe(default(Microsoft.Xna.Framework.Vector3), "mid-step");
            Both(o => o.ClearSteps(BOAT), n => n.ClearSteps(BOAT), NOW + 1000);
            newWorld.Items.Get(BOAT).ScreenOffset.Should().Be(default(Microsoft.Xna.Framework.Vector3), "reset");
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void The_Boat_Operations_Reject_What_This_World_Does_Not_Hold()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildBoat(Start.Standing), other = BuildBoat(Start.Standing);
                world.Writer.ThrowOnViolation = throws;
                Item foreign = other.Items.Get(BOAT);
                SetOffset(other, foreign, new Microsoft.Xna.Framework.Vector3(3, 0, 0));
                string before = $"{foreign.X},{foreign.Y} {foreign.ScreenOffset} {foreign.LastStepTime}";

                foreach (Action act in new Action[]
                {
                    () => world.Writer.CompleteBoatStep(foreign, 5, 5, 0),
                    () => world.Writer.MoveBoatPassenger(foreign, 5, 5, 0),
                    () => world.Writer.SetBoatStepTime(foreign, 77),
                    () => world.Writer.ResetBoatOffset(foreign)
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

                $"{foreign.X},{foreign.Y} {foreign.ScreenOffset} {foreign.LastStepTime}".Should().Be(before);
                world.Writer.Violations.Should().Be(4);
            }
        }

        // ---- the Codex audit of part 6 (P2): a multi reload inside UpsertWorldItem ends the boat's movement
        // (LoadMulti -> BoatMovingManager.ClearSteps -> ResetBoatOffset). The resets are operations, so they used to nest in the
        // upsert's open scope: a CUO_DEVTOOLS build threw mid-upsert, a release build rejected them while ClearSteps went on
        // dropping the steps and passengers, leaving the mid-step offsets drawn. Both Worlds run the real manager (its own
        // operations are compared with the old code above); the old World reloads through the pre-T1 handler code.

        // a boat multi one step under way: mid-step, so the boat and both passengers (the mobile, the bag) have offsets
        private static World BuildMovingBoat()
        {
            World world = BuildBoat(Start.Standing);
            Item boat = world.Items.Get(BOAT);
            boat.IsMulti = true;
            world.Ecs.Sync(boat);
            BoatMovingManager boats = world.BoatMovingManager;

            Time.Ticks = NOW + 100;
            boats.AddStep(BOAT, 3, Direction.East, Direction.East, 1001, 1000, 0);
            boats.PushItemToList(BOAT, MOB, 1, 0, -2);
            boats.PushItemToList(BOAT, BAG, -1, 1, 0);
            Time.Ticks = NOW + 300;
            boats.Update();
            Time.Ticks = NOW;

            return world;
        }

        [Theory]
        [InlineData(true)] // CUO_DEVTOOLS: a nested reset threw mid-upsert
        [InlineData(false)] // release: a nested reset was rejected, the offsets stayed
        public void A_Multi_Reload_In_The_Upsert_Ends_The_Boat_Movement_As_Before_T1(bool throws)
        {
            World oldWorld = BuildMovingBoat(), newWorld = BuildMovingBoat();
            newWorld.Writer.ThrowOnViolation = throws;

            foreach (World w in new[] { oldWorld, newWorld })
            {
                foreach (uint serial in new[] { BOAT, MOB, BAG })
                {
                    w.Get(serial).ScreenOffset.Should().NotBe(default(Microsoft.Xna.Framework.Vector3), $"0x{serial:X8} mid-step");
                }
            }

            void Compare(string when)
            {
                AssertSameWorld(oldWorld, newWorld);

                foreach (uint serial in new[] { BOAT, MOB, BAG })
                {
                    BoatSnapshot(newWorld, serial).Should().Be(BoatSnapshot(oldWorld, serial), $"0x{serial:X8} {when} (throws: {throws})");
                }

                newWorld.HouseManager.TryGetHouse(BOAT, out _).Should().Be(oldWorld.HouseManager.TryGetHouse(BOAT, out _));
                newWorld.Items.Get(BOAT).WantUpdateMulti.Should().Be(oldWorld.Items.Get(BOAT).WantUpdateMulti);
                newWorld.Items.Get(BOAT).AllowedToDraw.Should().Be(oldWorld.Items.Get(BOAT).AllowedToDraw);
            }

            // 0xF3/0x1A: the boat multi moved (a reload: WantUpdateMulti), the handler's way (upsert, callbacks, placement)
            var p = new WorldWriterTests.Packet { Serial = BOAT, Graphic = 0x3E4E, Count = 1, X = 1005, Y = 1000, Z = 0, Direction = Direction.North, Type = 2 };
            WorldWriterTests.OldUpsert(oldWorld, in p);
            Item boat = newWorld.Writer.UpsertWorldItem(p.Serial, p.Graphic, p.GraphicInc, p.Count, p.X, p.Y, p.Z, p.Direction, p.Hue, p.Flags, p.Type, out bool created, out WorldWriter.Placement placement);
            boat.Should().BeSameAs(newWorld.Items.Get(BOAT));
            created.Should().BeFalse();
            newWorld.Writer.InScope.Should().BeFalse("the deferred resets ran before the upsert returned");
            newWorld.Get(MOB).ScreenOffset.Should().Be(default(Microsoft.Xna.Framework.Vector3), "the passenger's reset ran before the handler's callbacks");
            newWorld.Writer.PlaceWorldItem(placement);

            Compare("after the reload");
            oldWorld.HouseManager.TryGetHouse(BOAT, out _).Should().BeTrue("the multi loaded");

            foreach (uint serial in new[] { BOAT, MOB, BAG })
            {
                newWorld.Get(serial).ScreenOffset.Should().Be(default(Microsoft.Xna.Framework.Vector3), $"0x{serial:X8} reset");
            }

            // the steps and the passengers are gone in both: a later frame moves nothing
            Time.Ticks = NOW + 700;
            oldWorld.BoatMovingManager.Update();
            newWorld.BoatMovingManager.Update();
            Time.Ticks = NOW;
            Compare("a frame later");
            newWorld.Items.Get(BOAT).X.Should().Be(1005);

            // the boat sails on; a later upsert that reloads no multi clears nothing (no reset is left over)
            foreach (World w in new[] { oldWorld, newWorld })
            {
                Time.Ticks = NOW + 800;
                w.BoatMovingManager.AddStep(BOAT, 3, Direction.East, Direction.East, 1006, 1000, 0);
                w.BoatMovingManager.PushItemToList(BOAT, MOB, 1, 0, -2);
                Time.Ticks = NOW + 1000;
                w.BoatMovingManager.Update();
                Time.Ticks = NOW;
            }

            newWorld.Items.Get(BOAT).ScreenOffset.Should().NotBe(default(Microsoft.Xna.Framework.Vector3), "under way again");
            var coin = new WorldWriterTests.Packet { Serial = 0x4000_7401, Graphic = 0xEED, Count = 1, X = 1010, Y = 1000, Z = 0, Direction = Direction.North };
            WorldWriterTests.OldUpsert(oldWorld, in coin);
            newWorld.Writer.UpsertWorldItem(coin.Serial, coin.Graphic, coin.GraphicInc, coin.Count, coin.X, coin.Y, coin.Z, coin.Direction, coin.Hue, coin.Flags, coin.Type, out _, out WorldWriter.Placement coinPlacement);
            newWorld.Writer.PlaceWorldItem(coinPlacement);
            Compare("another item's upsert");
            newWorld.Items.Get(BOAT).ScreenOffset.Should().NotBe(default(Microsoft.Xna.Framework.Vector3), "still under way");

            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        // ---- group 4.3 (misc), 0xBF.1B: the spellbook's content

        private const uint BOOK = 0x4000_7500;

        internal static Item OldFillSpellbook(World world, uint serial, ushort graphic, uint spells0, uint spells1)
        {
            Item spellbook = world.GetOrCreateItem(serial);
            spellbook.Graphic = graphic;
            spellbook.Clear();
            world.Ecs.Sync(spellbook);

            for (int j = 0; j < 2; j++)
            {
                uint spells = j == 0 ? spells0 : spells1;

                for (int i = 0; i < 32; i++)
                {
                    if ((spells & (1 << i)) != 0)
                    {
                        ushort cc = (ushort)(j * 32 + i + 1);
                        Item spellItem = Item.Create(world, cc);
                        spellItem.Serial = cc;
                        spellItem.Graphic = 0x1F2E;
                        spellItem.TestSetAmount(cc);
                        spellItem.TestSetContainer(spellbook);
                        spellbook.PushToBack(spellItem);
                    }
                }
            }

            return spellbook;
        }

        // the book's children: the old path's own list, or (ecs, T9 child-list slice B: the funnel writes no list) the ECS's
        private static string SpellbookSnapshot(Item book, bool ecs = false)
        {
            var sb = new System.Text.StringBuilder($"{book.Serial:X8} {book.Graphic:X4}:");

            if (ecs)
            {
                foreach (Item it in book.World.State.Children(book))
                {
                    sb.Append($" {it.Serial}/{it.Graphic:X4}/{it.Amount}/{it.Container:X8}/{it.World == book.World}");
                }

                return sb.ToString();
            }

            for (ChildNode i = book.Items; i != null; i = i.NextNode)
            {
                var it = (Item)i.Owner;
                sb.Append($" {it.Serial}/{it.Graphic:X4}/{it.Amount}/{it.Container:X8}/{it.World == book.World}");
            }

            return sb.ToString();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FillSpellbook_Equals_The_Old_Path(bool existing)
        {
            World oldWorld = Build(Start.Standing), newWorld = Build(Start.Standing);
            newWorld.Writer.ThrowOnViolation = true;

            if (existing)
            {
                foreach (World w in new[] { oldWorld, newWorld })
                {
                    Item book = w.GetOrCreateItem(BOOK);
                    book.Graphic = 0x0EFA;
                    book.SetInWorldTile(1002, 1000, 0);
                }
            }

            // spells 1, 2, 32 (the sign bit), 33 and 64, then a second packet with fewer
            foreach ((ushort graphic, uint s0, uint s1) in new[] { ((ushort)0x2253, 0x8000_0003u, 0x8000_0001u), ((ushort)0x0EFA, 0x10u, 0u) })
            {
                Item a = null, b = null;
                Pair(() => a = OldFillSpellbook(oldWorld, BOOK, graphic, s0, s1), () => b = newWorld.Writer.FillSpellbook(BOOK, graphic, s0, s1));
                SpellbookSnapshot(b, ecs: true).Should().Be(SpellbookSnapshot(a));
                AssertSameWorld(oldWorld, newWorld);
            }

            SpellbookSnapshot(newWorld.Items.Get(BOOK), ecs: true).Should().EndWith($" 5/1F2E/5/{BOOK:X8}/True");
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }
    }

    [Collection(nameof(FunnelCheckCollection))]
    public class StatusFunnelCheckTests : IDisposable
    {
        public StatusFunnelCheckTests()
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
        public void The_Status_Operations_Make_No_Direct_Writes()
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
            FunnelCheck.Reset();

            world.Writer.SetHits(m, 100, 50);
            world.Writer.SetHits(bag, 10, 5);
            world.Writer.SetStatus(m, "m", 40, 100);
            world.Writer.SetStatus(bag, "bag", 4, 10);
            world.Writer.SetMobileStatus(m, true, true, true);
            world.Writer.SetMana(m, 20, 10);
            world.Writer.SetStamina(m, 30, 15);
            world.Writer.SetRace(m, RaceType.GARGOYLE);
            world.Writer.SetHealthbar(m, Flags.YellowBar | Flags.Poisoned, true, true);
            world.Writer.AnimateMobile(m, 14, 3, 7, 2, true, false, true, frame: 1, hold: true);
            world.Writer.CompleteBoatStep(bag, 1002, 1000, 0);
            world.Writer.MoveBoatPassenger(m, 1003, 1000, 0);
            world.Writer.SetBoatStepTime(bag, 5);
            world.Writer.ResetBoatOffset(m);
            world.Writer.ResetBoatOffset(bag);
            world.Writer.FillSpellbook(0x4000_7301, 0x2253, 0x8000_0003, 1).Should().NotBeNull();

            (m.Name, m.Hits, m.Mana, m.Stamina, m.Race, m.IsFemale, m.Flags).Should().Be(("m", (ushort)40, (ushort)10, (ushort)15, RaceType.GARGOYLE, true, Flags.YellowBar | Flags.Poisoned));
            FunnelCheck.Total.Should().Be(0, "0xA1, 0xA2, 0xA3, 0x2D, 0x11, 0x16/0x17, 0x6E/0xE2/0xAF/0xBF.19/0xBF.2B, the boat, 0xBF.1B");
        }
    }
}
