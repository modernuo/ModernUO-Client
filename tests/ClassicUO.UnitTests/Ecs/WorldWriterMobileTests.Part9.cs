using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 9 (ecs-transition-plan.md, T1): the value-taking re-key commit and the remaining removal sites. Same
    // method as parts 4-8: a copy of the old code on one World, the funnel on another, identical starts, everything compared.
    public partial class WorldWriterMobileTests
    {
        // ---- group 1: the death re-key commits the ECS from its values, before the cache writes

        [Theory]
        [MemberData(nameof(RekeyCases))]
        public void RekeyDeadMobile_Commits_The_Ecs_From_Its_Values_Before_The_Cache_Writes(Start start, RekeyStart rk)
        {
            World world = BuildRekey(start, rk);
            world.Writer.ThrowOnViolation = true;
            uint from = rk == RekeyStart.AlreadyDead ? DEAD : MOB;
            Mobile m = world.Mobiles.Get(from);
            var children = new List<Item>();

            for (ChildNode i = m.Items; i != null; i = i.NextNode)
            {
                children.Add((Item)i.Owner);
            }

            var mobileCommits = new List<uint>();
            var childCommits = new List<(uint Serial, uint Container)>();

            world.Ecs.MobileCommitObserver = (mob, v) =>
            {
                mob.Should().BeSameAs(m);
                mob.Serial.Should().Be(from, "the cache still has the old serial when the ECS is written");
                world.Mobiles.Get(from).Should().BeSameAs(m, "the table is not written yet");
                if (from != DEAD)
                {
                    world.Mobiles.Get(DEAD).Should().NotBeSameAs(m);
                }
                v.Serial.Should().Be(DEAD, "the commit takes the computed serial");
                mobileCommits.Add(v.Serial);
            };

            world.Ecs.CommitObserver = (item, v) =>
            {
                item.Container.Should().Be(from, "the child's cache is not written yet");
                m.Serial.Should().Be(from);
                childCommits.Add((v.Serial, v.Container));
            };

            world.Writer.RekeyDeadMobile(m).Should().Be(DEAD);
            world.Ecs.MobileCommitObserver = null;
            world.Ecs.CommitObserver = null;

            mobileCommits.Should().Equal(start == Start.DestroyedHeld ? new uint[0] : new[] { DEAD }, "one record commit (none for a destroyed mobile)");
            childCommits.Should().Equal(children.Where(c => !c.IsDestroyed && ReferenceEquals(world.Items.Get(c.Serial), c)).Select(c => (c.Serial, DEAD)), "each child once, with the new parent");
            world.Ecs.Mirror.TryGetEntity(from, out _).Should().Be(from == DEAD && start != Start.DestroyedHeld, "the old record is gone (a second death re-keys onto itself)");
        }

        [Fact]
        public void RekeyDeadMobile_Writes_The_Mirror_As_Often_As_The_Old_Path()
        {
            World world = BuildRekey(Start.Standing, RekeyStart.Plain), reference = BuildRekey(Start.Standing, RekeyStart.Plain);
            long oldMobile = reference.Ecs.MobileMirrorWrites, oldItems = reference.Ecs.ItemMirrorWrites;
            long newMobile = world.Ecs.MobileMirrorWrites, newItems = world.Ecs.ItemMirrorWrites;
            OldRekey(reference, reference.Mobiles.Get(MOB));
            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB));
            (world.Ecs.MobileMirrorWrites - newMobile).Should().Be(reference.Ecs.MobileMirrorWrites - oldMobile).And.Be(1);
            (world.Ecs.ItemMirrorWrites - newItems).Should().Be(reference.Ecs.ItemMirrorWrites - oldItems).And.BeGreaterThan(0);
            Inputs(world, DEAD).Should().Be(Inputs(reference, DEAD));
        }

        // ---- group 2: the removal sites outside the converted callers

        private const uint VAULT = 0x4000_7702, COIN9 = 0x4000_7703, PACK9 = 0x4000_7704, KEPT = 0x4000_7705, GROUND = 0x4000_7706, LOOT9 = 0x4000_7708, PENDING = 0x4000_7709;

        // OpenBank's bank plus a box in it holding a coin; the player's backpack with an item; a ground item and an item in
        // the ground bag
        private static Item OpenBank9(World world, PlayerMobile p)
        {
            Item bank = OpenBank(world, p);
            Item vault = world.GetOrCreateItem(VAULT);
            vault.Graphic = 0xE7D;
            vault.Container = BANK;
            bank.PushToBack(vault);
            Item coin = world.GetOrCreateItem(COIN9);
            coin.Graphic = 0xEED;
            coin.Amount = 7;
            coin.Container = VAULT;
            vault.PushToBack(coin);
            world.Ecs.Sync(vault);
            world.Ecs.Sync(coin);
            world.Ecs.Sync(world.Items.Get(GOLD));
            world.Ecs.Sync(bank);

            Item pack = world.GetOrCreateItem(PACK9);
            pack.Graphic = 0xE75;
            pack.Layer = Layer.Backpack;
            pack.Container = p.Serial;
            p.PushToBack(pack);
            Item kept = world.GetOrCreateItem(KEPT);
            kept.Graphic = 0xF0E;
            kept.Container = PACK9;
            pack.PushToBack(kept);
            world.Ecs.Sync(pack);
            world.Ecs.Sync(kept);

            Item ground = world.GetOrCreateItem(GROUND);
            ground.Graphic = 0x1F14;
            ground.SetInWorldTile(1003, 1000, 0);
            Item loot = world.GetOrCreateItem(LOOT9);
            loot.Graphic = 0x1F15;
            loot.Container = BAG;
            world.Items.Get(BAG).PushToBack(loot);
            world.Ecs.Sync(loot);
            WorldWriterTests.AttachLists(world);

            return bank;
        }

        // a new item whose placement its handler never made, put into the parent
        private static WorldWriter.Placement PendingIn9(World world, uint parent)
        {
            Item it = world.Writer.UpsertWorldItem(PENDING, 0xEED, 0, 1, 1010, 1010, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            it.Container = parent;
            world.Get(parent).PushToBack(it);
            WorldWriterTests.AttachLists(world);

            return p;
        }

        // the base revision's CloseBank (its gump lookup aside)
        internal static void OldCloseBank(PlayerMobile p)
        {
            Item bank = p.FindItemByLayer(Layer.Bank);

            if (bank != null && bank.Opened)
            {
                if (!bank.IsEmpty)
                {
                    ChildNode first = bank.Items;

                    while (first != null)
                    {
                        ChildNode next = first.NextNode;
                        p.World.RemoveItem((Item)first.Owner, true);
                        first = next;
                    }

                    bank.Items = null;
                }

                bank.Opened = false;
            }
        }

        private static List<(uint, bool)> Destroyed9(World w) =>
            w.Mobiles.Values.Select(m => (m.Serial, m.IsDestroyed)).Concat(w.Items.Values.Select(i => (i.Serial, i.IsDestroyed))).OrderBy(x => x.Item1).ToList();

        [Fact]
        public void CloseBank_Equals_The_Old_Removals()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;
            OpenBank9(oldWorld, a);
            Item bankB = OpenBank9(newWorld, b);
            PendingIn9(newWorld, VAULT);
            PendingIn9(oldWorld, VAULT);

            Pair(() => OldCloseBank(a), () => b.CloseBank());

            Destroyed9(newWorld).Should().Equal(Destroyed9(oldWorld));
            AssertSameWorld(oldWorld, newWorld);
            bankB.Opened.Should().BeFalse();
            newWorld.State.HasChildren(bankB).Should().BeFalse();
            newWorld.Items.Get(GOLD).Should().BeNull();
            newWorld.Items.Get(VAULT).Should().BeNull();
            newWorld.Items.Get(COIN9).Should().BeNull("the bank's contents at any depth");
            newWorld.Items.Get(KEPT).Should().NotBeNull();
            newWorld.Ecs.Mirror.TryGetEntity(COIN9, out _).Should().BeFalse();
            newWorld.Writer.OutstandingPlacements.Should().Be(0, "the removed item's placement is cancelled");
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RemoveBankContents_Rejects_Another_Worlds_Player_And_Nesting_And_Leaves_The_Bank_Open(bool throws)
        {
            World world = BuildPlayer(out TestPlayer p), other = BuildPlayer(out TestPlayer foreign);
            world.Writer.ThrowOnViolation = throws;
            Item bank = OpenBank9(world, p);
            OpenBank9(other, foreign);

            Action act = () => world.Writer.RemoveBankContents(foreign, other.Items.Get(BANK)).Should().BeFalse();

            if (throws)
            {
                act.Should().Throw<ArgumentException>();
            }
            else
            {
                act();
            }

            other.Items.Get(COIN9).IsDestroyed.Should().BeFalse();

            // inside another object's operation (a fold): a nesting
            world.Ecs.BeginFold(world.Mobiles.Get(OTHER));

            if (throws)
            {
                ((Action)(() => p.CloseBank())).Should().Throw<InvalidOperationException>();
            }
            else
            {
                p.CloseBank();
            }

            world.Ecs.EndFold(world.Mobiles.Get(OTHER));
            bank.Opened.Should().BeTrue("a rejected removal leaves the bank open");
            world.Items.Get(COIN9).Should().NotBeNull();
            world.Items.Get(GOLD).IsDestroyed.Should().BeFalse();
            world.Writer.Violations.Should().Be(2);
        }

        [Fact]
        public void CloseBank_From_A_Position_Callback_Joins_The_Operation_On_The_Player()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;
            OpenBank9(oldWorld, a);
            OpenBank9(newWorld, b);
            bool owned = false;

            a.OnMoved = p => OldCloseBank(p);
            b.OnMoved = p =>
            {
                p.CloseBank();
                owned = newWorld.Writer.InScope && newWorld.Writer.Owns(p);
            };

            Pair(() => a.SetInWorldTile(1001, 1000, 0), () => newWorld.Writer.MoveMobile(b, 1001, 1000, 0));

            Destroyed9(newWorld).Should().Equal(Destroyed9(oldWorld));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Items.Get(COIN9).Should().BeNull();
            owned.Should().BeTrue("the joined removal left the outer operation open and owning the player");
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void RemovePickedUpItem_Equals_The_Old_Removal_And_Rejects_What_This_World_Does_Not_Hold()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;
            OpenBank9(oldWorld, a);
            OpenBank9(newWorld, b);
            PendingIn9(oldWorld, VAULT);
            PendingIn9(newWorld, VAULT);

            Pair(() => oldWorld.RemoveItem(oldWorld.Items.Get(VAULT), true), () => newWorld.Writer.RemovePickedUpItem(newWorld.Items.Get(VAULT)));

            Destroyed9(newWorld).Should().Equal(Destroyed9(oldWorld));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Items.Get(COIN9).Should().BeNull("the picked-up box's contents go with it");
            newWorld.Writer.OutstandingPlacements.Should().Be(0, "the removed item's placement is cancelled");

            newWorld.Writer.ThrowOnViolation = false;
            Item stranger = Item.Create(newWorld, GOLD);
            newWorld.Writer.RemovePickedUpItem(stranger);
            newWorld.Items.Get(GOLD).IsDestroyed.Should().BeFalse();
            newWorld.Writer.Violations.Should().Be(1);
        }

        // the base revision's InternalMapChangeClear(noplayer: true), its only call
        internal static void OldMapChangeClear(World world)
        {
            var toRemove = new List<uint>();

            foreach (Item item in world.Items.Values)
            {
                if (world.Player != null && !world.Player.IsDestroyed && item.RootContainer == world.Player)
                {
                    continue;
                }

                if (item.OnGround && item.IsMulti)
                {
                    world.HouseManager.Remove(item.Serial);
                }

                toRemove.Add(item);
            }

            foreach (uint serial in toRemove)
            {
                world.RemoveItem(serial, true);
            }

            toRemove.Clear();

            foreach (Mobile mob in world.Mobiles.Values)
            {
                if (world.Player != null && !world.Player.IsDestroyed && mob == world.Player)
                {
                    continue;
                }

                toRemove.Add(mob);
            }

            foreach (uint serial in toRemove)
            {
                world.RemoveMobile(serial, true);
            }
        }

        [Fact]
        public void ClearForMapChange_Equals_The_Old_Removals()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;
            OpenBank9(oldWorld, a);
            OpenBank9(newWorld, b);
            PendingIn9(oldWorld, BAG);
            PendingIn9(newWorld, BAG);

            Pair(() => OldMapChangeClear(oldWorld), () => newWorld.Writer.ClearForMapChange(true));

            Destroyed9(newWorld).Should().Equal(Destroyed9(oldWorld));
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Items.Get(KEPT).Should().NotBeNull("the player's items stay");
            newWorld.Items.Get(COIN9).Should().NotBeNull("the bank is the player's too");
            newWorld.Items.Get(GROUND).Should().BeNull();
            newWorld.Items.Get(LOOT9).Should().BeNull();
            newWorld.Mobiles.Get(OTHER).Should().BeNull();
            newWorld.Writer.OutstandingPlacements.Should().Be(0, "the removed item's placement is cancelled");
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Writer.Owns(b).Should().BeFalse("the scope's ownership of the whole World ends with it");
        }

        [Fact]
        public void ClearForMapChange_Keeps_The_Placements_Of_What_It_Keeps()
        {
            World world = BuildPlayer(out TestPlayer p);
            OpenBank9(world, p);
            PendingIn9(world, PACK9);
            world.Writer.ClearForMapChange(true);
            world.Writer.OutstandingPlacements.Should().Be(1, "the player's item stays, and so does its placement");
        }

        // the base revision's LoginScene removals
        internal static void OldLoginRemovals(World world)
        {
            foreach (Item item in world.Items.Values)
            {
                world.RemoveItem(item);
            }

            foreach (Mobile mobile in world.Mobiles.Values)
            {
                world.RemoveMobile(mobile);
            }
        }

        [Fact]
        public void ClearForLogin_Equals_The_Old_Removals_And_The_Clears_Do_Not_Nest()
        {
            World oldWorld = BuildPlayer(out TestPlayer a), newWorld = BuildPlayer(out TestPlayer b);
            newWorld.Writer.ThrowOnViolation = true;
            OpenBank9(oldWorld, a);
            OpenBank9(newWorld, b);
            PendingIn9(oldWorld, BAG);
            PendingIn9(newWorld, BAG);

            Pair(() => OldLoginRemovals(oldWorld), () => newWorld.Writer.ClearForLogin());

            Destroyed9(newWorld).Should().Equal(Destroyed9(oldWorld));
            Destroyed9(newWorld).Should().OnlyContain(x => x.Item2);
            AssertSameWorld(oldWorld, newWorld);
            newWorld.Writer.OutstandingPlacements.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Writer.Owns(b).Should().BeFalse();

            World w = BuildPlayer(out TestPlayer wp);
            OpenBank9(w, wp);
            w.Writer.ThrowOnViolation = false;
            w.Ecs.BeginFold(w.Mobiles.Get(OTHER));
            w.Writer.ClearForLogin();
            w.Writer.ClearForMapChange(true);
            w.Writer.RemovePickedUpItem(w.Items.Get(VAULT));
            w.Ecs.EndFold(w.Mobiles.Get(OTHER));
            w.Writer.Violations.Should().Be(3);
            Destroyed9(w).Should().OnlyContain(x => !x.Item2, "a rejected removal removes nothing");
        }

        // the funnel-check tests' world: the player with its open bank (a box with a coin in it), a backpack, ground items,
        // and a position callback that closes the bank once
        internal static World BuildBankWorld(out PlayerMobile player)
        {
            World world = BuildPlayer(out TestPlayer p);
            OpenBank9(world, p);
            p.OnMoved = x => x.CloseBank();
            player = p;

            return world;
        }
    }

    [Collection(nameof(FunnelCheckCollection))]
    public class RemovalSitesFunnelCheckTests : IDisposable
    {
        public RemovalSitesFunnelCheckTests()
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
        public void The_Removal_Sites_Make_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            for (int site = 0; site < 5; site++)
            {
                World world = WorldWriterMobileTests.BuildBankWorld(out PlayerMobile p);
                FunnelCheck.Reset();

                switch (site)
                {
                    case 0: p.CloseBank(); break;
                    case 1: world.Writer.RemovePickedUpItem(world.Items.Get(0x4000_7702)); break;
                    case 2: world.Writer.ClearForMapChange(true); break;
                    case 3: world.Writer.ClearForLogin(); break;
                    case 4: world.Writer.MoveMobile(p, 1001, 1000, 0); break; // the joined CloseBank (BuildBankWorld's callback)
                }

                FunnelCheck.Total.Should().Be(0, $"site {site}");
            }
        }

        [Fact]
        public void A_Direct_Removal_Names_The_Caller_Past_The_Removal_Plumbing()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            World world = WorldWriterMobileTests.BuildBankWorld(out _);
            FunnelCheck.Reset();
            RemoveDirectly(world);

            FunnelCheck.Total.Should().BeGreaterThan(0);
            FunnelCheck.Sites.Keys.Select(k => k.Item2).Should().OnlyContain(c => c.Contains(" from RemovalSitesFunnelCheckTests.RemoveDirectly"), "the trace goes past Destroy and World.RemoveItem");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RemoveDirectly(World world) => world.RemoveItem(0x4000_7702, true);
    }
}
