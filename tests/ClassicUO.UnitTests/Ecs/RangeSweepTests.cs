using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T5c: the range sweep and the world-map pass as a system over Position / MobileInputs, against the legacy loops (copied
    // below as the reference since T9 deleted world_sweep_system's off path and CUO_SWEEP_CHECK) over the same constructed
    // worlds.
    public class RangeSweepTests
    {
        private const uint PLAYER = 0x0000_0100;
        private const int RANGE = 24;

        private sealed class Scene
        {
            public World World;
            public WorldWriterMobileTests.TestPlayer Player;
            public readonly List<string> MapCalls = new List<string>();
            public readonly Dictionary<uint, Mobile> Mobs = new Dictionary<uint, Mobile>();
            public readonly Dictionary<uint, Item> Items = new Dictionary<uint, Item>();
        }

        // the player stands at 1000,1000 with the range centre on it
        private static Scene Build(int centerX = 1000, int centerY = 1000)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out WorldWriterMobileTests.TestPlayer player, PLAYER);
            var scene = new Scene { World = world, Player = player };
            world.RangeSize.X = centerX;
            world.RangeSize.Y = centerY;
            world.MapCallObserver = scene.MapCalls.Add;

            return scene;
        }

        private static Mobile AddMobile(Scene s, uint serial, int x, int y, NotorietyFlag noto = NotorietyFlag.Innocent, Flags flags = Flags.None)
        {
            Mobile m = s.World.GetOrCreateMobile(serial);
            m.TestSetGraphic(0x190);
            m.TestSetFlags(flags);
            m.NotorietyFlag = noto;
            m.SetInWorldTile((ushort)x, (ushort)y, 0);
            s.World.Ecs.Sync(m);
            s.World.Ecs.PushInputs(m);
            s.World.Ecs.PushClock(m);
            s.Mobs[serial] = m;

            return m;
        }

        private static void QueueSteps(Scene s, Mobile m, params (int x, int y)[] steps)
        {
            foreach ((int x, int y) in steps)
            {
                m.AddStep(new Mobile.Step { X = x, Y = y, Z = 0, Direction = 2 });
            }

            s.World.Ecs.PushInputs(m);
            s.World.Ecs.PushClock(m);
        }

        private static Item AddGround(Scene s, uint serial, int x, int y, bool multi = false, int bonus = 0)
        {
            Item i = s.World.GetOrCreateItem(serial);
            i.TestSetGraphic(0xEED);
            i.SetInWorldTile((ushort)x, (ushort)y, 0);

            if (multi)
            {
                i.IsMulti = true;

                if (bonus != 0)
                {
                    typeof(Item).GetProperty(nameof(Item.MultiDistanceBonus))!.SetValue(i, bonus);
                }
            }

            s.World.Ecs.Sync(i);
            s.Items[serial] = i;

            return i;
        }

        // every kind of mobile around the range edge
        private static void FillMobiles(Scene s)
        {
            AddMobile(s, 0x1001, 1000 + RANGE, 1000);                  // on the edge: stays
            AddMobile(s, 0x1002, 1000 + RANGE + 1, 1000);              // one beyond: goes
            AddMobile(s, 0x1003, 1000, 1000 + RANGE + 1);
            AddMobile(s, 0x1004, 1000 - RANGE, 1000);                  // west edge: stays
            AddMobile(s, 0x1005, 1000 - RANGE - 1, 1000);              // goes
            AddMobile(s, 0x1006, 1000 + RANGE + 5, 1000 + RANGE + 5, NotorietyFlag.Ally); // an ally beyond
            AddMobile(s, 0x1007, 1000 + 3, 1000 + 3, flags: Flags.Hidden);
            AddMobile(s, 0x1008, 1000 + 30, 1000, flags: Flags.Frozen);            // dead/hidden-like flags do not matter
            AddMobile(s, 0x1009, 0xFFFF, 0xFFFF);                      // unplaced: beyond

            // stepping: in range now, last step beyond (goes); beyond now, last step inside (stays)
            Mobile a = AddMobile(s, 0x1010, 1010, 1000);
            QueueSteps(s, a, (1015, 1000), (1030, 1000));
            Mobile b = AddMobile(s, 0x1011, 1030, 1000);
            QueueSteps(s, b, (1020, 1000), (1005, 1000));
            Mobile c = AddMobile(s, 0x1012, 1000, 1010);
            QueueSteps(s, c, (1000, 1025));                              // beyond in y only

            // a destroyed mobile World still holds
            Mobile gone = AddMobile(s, 0x1013, 1000 + 40, 1000);
            gone.Destroy();
        }

        private static List<uint> Held(World w) => w.Mobiles.Values.Where(m => !m.IsDestroyed).Select(m => m.Serial).OrderBy(x => x).ToList();

        private static List<uint> LegacyMobileSet(World w) =>
            w.Mobiles.Values.Where(m => !m.IsDestroyed && m.Distance > w.ClientViewRange).Select(m => m.Serial).ToList();

        // the legacy loops before T5c (world_sweep_system off), the reference the system is compared with
        private static void LegacyMobileSweep(World w)
        {
            foreach (Mobile mob in w.Mobiles.Values.ToList())
            {
                if (mob.Distance > w.ClientViewRange)
                {
                    w.Writer.SweepMobile(mob);
                }
            }
        }

        private static void LegacyItemLoop(World w)
        {
            foreach (Item item in w.Items.Values.ToList())
            {
                if (item.OnGround && item.Distance > w.ClientViewRange)
                {
                    if (!item.IsMulti || w.HouseManager.TryToRemove(item, w.ClientViewRange))
                    {
                        w.Writer.SweepItem(item);
                    }
                }
            }
        }

        private static List<string> LegacyMapCalls(World w)
        {
            var calls = new List<string>();

            foreach (Mobile mob in w.Mobiles.Values)
            {
                if (mob.IsDestroyed)
                {
                    continue;
                }

                if (mob.NotorietyFlag == NotorietyFlag.Ally)
                {
                    calls.Add(Call(w, mob, true));
                }
                else if (w.Party.Leader != 0 && w.Party.Contains(mob))
                {
                    calls.Add(Call(w, mob, false));
                }
            }

            return calls;
        }

        private static string Call(World w, Mobile mob, bool guild) =>
            $"{mob.Serial:X8} {mob.X},{mob.Y} hp {ClassicUO.Utility.MathHelper.PercetangeOf(mob.Hits, mob.HitsMax)} map {w.MapIndex} guild {guild} name {mob.Name}";

        [Fact]
        public void The_Selection_Equals_The_Legacy_Set_In_Table_Order()
        {
            Scene s = Build();
            FillMobiles(s);

            List<uint> system = s.World.SelectSweptMobiles().Select(m => m.Serial).ToList();
            List<uint> legacy = LegacyMobileSet(s.World);

            legacy.Should().Contain(new uint[] { 0x1002, 0x1003, 0x1005, 0x1006, 0x1008, 0x1009, 0x1010, 0x1012 });
            legacy.Should().NotContain(new uint[] { 0x1001, 0x1004, 0x1007, 0x1011, PLAYER });
            system.Should().Equal(legacy);
        }

        [Fact]
        public void The_Selection_Order_Follows_The_Table_After_Removals_Reuse_Slots()
        {
            Scene s = Build();
            FillMobiles(s);
            // the removal the client makes (destroyed, its record gone, its slot freed): a bare Mobiles.Remove would leave a live
            // mobile with a record, which World.GetMobile still finds (the index, not the table, is the lookup)
            s.World.RemoveMobile(0x1002, forceRemove: true).Should().BeTrue();
            AddMobile(s, 0x1020, 1000 + 40, 1000); // takes the freed slot

            s.World.SelectSweptMobiles().Select(m => m.Serial).Should().Equal(LegacyMobileSet(s.World));
        }

        [Fact]
        public void The_Player_Is_Never_Swept_Even_Beyond_A_Moved_Centre()
        {
            Scene s = Build(1200, 1200);
            AddMobile(s, 0x1001, 1200, 1200);

            s.World.SelectSweptMobiles().Select(m => m.Serial).Should().Equal(LegacyMobileSet(s.World));
            s.World.SelectSweptMobiles().Should().NotContain(m => m.Serial == PLAYER);
        }

        [Fact]
        public void The_Sweep_Removes_The_Same_Mobiles_With_The_System_On_And_Off()
        {
            Scene on = Build(), off = Build();
            FillMobiles(on);
            FillMobiles(off);

            on.World.SweepMobiles();
            LegacyMobileSweep(off.World);

            Held(on.World).Should().Equal(Held(off.World));
            Held(on.World).Should().Contain(new uint[] { PLAYER, 0x1001, 0x1004, 0x1007, 0x1011 });
            Held(on.World).Should().NotContain(new uint[] { 0x1002, 0x1003, 0x1005, 0x1006, 0x1008, 0x1009, 0x1010, 0x1012 });

            // the ECS agrees: the removed mobiles' entities are gone, the kept ones remain
            on.World.Ecs.Mirror.TryGetEntity(0x1002, out EcsEntity _).Should().BeFalse();
            on.World.Ecs.Mirror.TryGetEntity(0x1001, out EcsEntity _).Should().BeTrue();
        }

        [Fact]
        public void The_Sweep_Of_A_Second_Frame_Removes_Nothing_More()
        {
            Scene s = Build();
            FillMobiles(s);
            s.World.SweepMobiles();
            List<uint> held = Held(s.World);
            s.World.SweepMobiles();

            Held(s.World).Should().Equal(held);
        }

        // ---- items

        private static void FillItems(Scene s)
        {
            AddGround(s, 0x4000_0001, 1000 + RANGE, 1000);          // edge: stays
            AddGround(s, 0x4000_0002, 1000 + RANGE + 1, 1000);      // goes
            AddGround(s, 0x4000_0003, 1000, 1000 - RANGE - 1);      // goes
            AddGround(s, 0x4000_0004, 1000 - RANGE, 1000 - RANGE);  // stays
            AddGround(s, 0x4000_0005, 1010, 1010);
            AddGround(s, 0x4000_0006, 1000 + 40, 1000, multi: true);               // a multi with no house: goes
            AddGround(s, 0x4000_0007, 1000 + 26, 1000, multi: true, bonus: 5);     // a multi in range by its bonus: stays
            AddGround(s, 0x4000_0008, 1000 + 60, 1000, multi: true, bonus: 5);     // a house beyond even with the bonus: goes

            // a contained and a worn item: never swept, even at far positions
            Item bag = AddGround(s, 0x4000_0010, 1005, 1000);
            Item inside = s.World.GetOrCreateItem(0x4000_0011);
            inside.TestSetGraphic(0xEED);
            inside.TestSetContainer(bag.Serial);
            bag.PushToBack(inside);
            inside.X = 1000 + 90;
            inside.Y = 1000;
            s.World.Ecs.Sync(inside);
            s.Items[inside.Serial] = inside;

            Item dead = AddGround(s, 0x4000_0012, 1000 + 50, 1000); // destroyed, still held
            dead.Destroy();

            s.World.HouseManager.Add(0x4000_0007, new House(s.World, 0x4000_0007, 1, false));
            s.World.HouseManager.Add(0x4000_0008, new House(s.World, 0x4000_0008, 1, false));
        }

        // the active-items / all-items loops' sweep, as World.Update runs it
        private static void SweepItemLoop(World w)
        {
            w.PrepareItemSweep();

            foreach (Item item in w.Items.Values)
            {
                if (w.ItemBeyond(item))
                {
                    w.SweepItemBeyond(item);
                }
            }

            w.FinishItemSweep();
        }

        [Fact]
        public void The_Item_Sweep_Removes_The_Same_Items_With_The_System_On_And_Off()
        {
            Scene on = Build(), off = Build();
            FillItems(on);
            FillItems(off);

            SweepItemLoop(on.World);
            LegacyItemLoop(off.World);

            List<uint> Left(World w) => w.Items.Values.Where(i => !i.IsDestroyed).Select(i => i.Serial).OrderBy(x => x).ToList();

            Left(on.World).Should().Equal(Left(off.World));
            Left(on.World).Should().Contain(new uint[] { 0x4000_0001, 0x4000_0004, 0x4000_0005, 0x4000_0007, 0x4000_0010, 0x4000_0011 });
            Left(on.World).Should().NotContain(new uint[] { 0x4000_0002, 0x4000_0003, 0x4000_0006, 0x4000_0008 });

            // the removed house is forgotten, the one in range is kept
            on.World.HouseManager.TryGetHouse(0x4000_0008, out _).Should().BeFalse();
            on.World.HouseManager.TryGetHouse(0x4000_0007, out _).Should().BeTrue();
            off.World.HouseManager.TryGetHouse(0x4000_0008, out _).Should().BeFalse();
        }

        // Codex P2 #1: a dying mobile's equipment is re-keyed to 0x80000000 | mobile serial; Item.OnGround treats that container
        // as no container, so the legacy sweep removes it when it is out of range
        private static void FillDyingEquipment(Scene s)
        {
            AddMobile(s, 0x1001, 1005, 1000);

            Item far = s.World.GetOrCreateItem(0x4000_0022);
            far.TestSetGraphic(0xEED);
            far.TestSetContainer(0x1001 | 0x8000_0000);
            far.TestSetLayer(Layer.Shirt);
            far.X = 1000 + 60;
            far.Y = 1000;
            s.World.Ecs.Sync(far);
            s.Items[far.Serial] = far;

            Item near = s.World.GetOrCreateItem(0x4000_0023);
            near.TestSetGraphic(0xEED);
            near.TestSetContainer(0x1001 | 0x8000_0000);
            near.TestSetLayer(Layer.Pants);
            near.X = 1000 + 3;
            near.Y = 1000;
            s.World.Ecs.Sync(near);
            s.Items[near.Serial] = near;
        }

        [Fact]
        public void A_Dying_Mobiles_Equipment_Is_Swept_Like_The_Legacy_Rule_Does()
        {
            Scene on = Build(), off = Build();
            FillDyingEquipment(on);
            FillDyingEquipment(off);
            on.Items[0x4000_0022].OnGround.Should().BeTrue("the dying-mobile key is not a valid container serial");

            SweepItemLoop(on.World);
            LegacyItemLoop(off.World);

            off.Items[0x4000_0022].IsDestroyed.Should().BeTrue();
            on.Items[0x4000_0022].IsDestroyed.Should().BeTrue();
            on.Items[0x4000_0023].IsDestroyed.Should().BeFalse();
            off.Items[0x4000_0023].IsDestroyed.Should().BeFalse();
        }

        // Codex P2 #2: a multi that was destroyed but is still held has no ECS entity; the legacy loop still runs the house check
        [Fact]
        public void A_Destroyed_Held_Multi_Still_Gets_Its_House_Cleanup()
        {
            Scene on = Build(), off = Build();

            foreach (Scene s in new[] { on, off })
            {
                Item multi = AddGround(s, 0x4000_0030, 1000 + 60, 1000, multi: true);
                s.World.HouseManager.Add(multi.Serial, new House(s.World, multi.Serial, 1, false));
                multi.Destroy();
                multi.IsDestroyed.Should().BeTrue();
                s.World.Items.Get(multi.Serial).Should().BeSameAs(multi, "the table still holds it");
            }

            SweepItemLoop(on.World);
            LegacyItemLoop(off.World);

            off.World.HouseManager.TryGetHouse(0x4000_0030, out _).Should().BeFalse("the legacy loop cleans the house up");
            on.World.HouseManager.TryGetHouse(0x4000_0030, out _).Should().BeFalse("and so does the system path");
        }

        [Fact]
        public void The_Item_Sweep_Runs_In_Table_Order()
        {
            Scene s = Build();
            FillItems(s);
            var order = new List<uint>();
            s.World.PrepareItemSweep();

            foreach (Item item in s.World.Items.Values)
            {
                if (s.World.ItemBeyond(item) && !item.IsDestroyed)
                {
                    order.Add(item.Serial);
                }
            }

            order.Should().Equal(s.World.Items.Values.Where(i => !i.IsDestroyed && i.OnGround && i.Distance > RANGE).Select(i => i.Serial));
            s.World.FinishItemSweep();
        }

        // ---- the world-map pass

        private static void FillMap(Scene s, bool party)
        {
            AddMobile(s, 0x2001, 1001, 1000, NotorietyFlag.Ally);
            AddMobile(s, 0x2002, 1002, 1000);                                  // innocent: not on the map
            AddMobile(s, 0x2003, 1003, 1000, NotorietyFlag.Criminal);          // party member
            AddMobile(s, 0x2004, 1004, 1000, NotorietyFlag.Ally);              // an ally that is also a party member: guild
            AddMobile(s, 0x2005, 1005, 1000, NotorietyFlag.Enemy);             // not in the party
            Mobile dead = AddMobile(s, 0x2006, 1006, 1000, NotorietyFlag.Ally);
            dead.Destroy();
            OldScalarWrites.Hits(AddMobile(s, 0x2007, 1007, 1000, NotorietyFlag.Ally), 5);

            if (party)
            {
                s.World.Party.Leader = PLAYER;
                s.World.Party.Members[0] = new PartyMember(s.World, 0x2003);
                s.World.Party.Members[1] = new PartyMember(s.World, 0x2004);
                s.World.Party.Members[2] = new PartyMember(s.World, 0x9999); // a member the client does not hold
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_World_Map_Calls_Equal_The_Legacy_Pass(bool party)
        {
            Scene on = Build();
            FillMap(on, party);

            on.World.WorldMapPass();

            on.MapCalls.Should().Equal(LegacyMapCalls(on.World));
            on.MapCalls.Should().HaveCount(party ? 4 : 3);
            on.MapCalls.Should().Contain(c => c.StartsWith("00002004") && c.Contains("guild True"));

            if (party)
            {
                on.MapCalls.Should().Contain(c => c.StartsWith("00002003") && c.Contains("guild False"));
            }
            else
            {
                on.MapCalls.Should().NotContain(c => c.StartsWith("00002003"));
            }

            on.MapCalls.Select(c => c.Substring(0, 8)).Should().Equal(on.World.Mobiles.Values.Select(m => m.Serial.ToString("X8")).Where(x => on.MapCalls.Any(c => c.StartsWith(x))),
                "the calls follow the table order");
        }

        // ---- the system decides from the ECS, not from the legacy objects

        [Fact]
        public void The_Ecs_Position_Decides()
        {
            Scene s = Build();
            Mobile m = AddMobile(s, 0x1001, 1001, 1000);
            Item i = AddGround(s, 0x4000_0001, 1001, 1000);
            s.World.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity me).Should().BeTrue();
            s.World.Ecs.Mirror.TryGetEntity(i.Serial, out EcsEntity ie).Should().BeTrue();
            me.GetComponent<Position>().X = 1200;
            ie.GetComponent<Position>().X = 1200;

            s.World.SweepMobiles();
            SweepItemLoop(s.World);

            m.IsDestroyed.Should().BeTrue("the system removed it on the ECS position");
            i.IsDestroyed.Should().BeTrue();
        }

        [Fact]
        public void The_Map_Pass_Takes_Allies_From_The_Pushed_Notoriety()
        {
            Scene s = Build();
            AddMobile(s, 0x2001, 1001, 1000);
            s.World.Ecs.Mirror.TryGetEntity(0x2001, out EcsEntity e).Should().BeTrue();
            e.GetComponent<MobileInputs>().Notoriety = (byte)NotorietyFlag.Ally;

            s.World.WorldMapPass();

            s.MapCalls.Should().ContainSingle().Which.Should().StartWith("00002001");
        }

        // ---- the system alone, over a bare store

        [Fact]
        public void The_Store_System_Selects_By_Position_And_Splits_Out_The_Stepping_Mobiles()
        {
            var mirror = new WorldMirror();
            var sys = new RangeSweepSystem(mirror.Store);

            void Mob(uint serial, ushort x, ushort y, MobileClockFlags f)
            {
                EcsEntity e = mirror.UpsertMobile(serial, 0x190, 0, x, y, 0, 0, 0, false);
                e.AddComponent(new MobileClock { Flags = f });
            }

            Mob(1, 100, 100, MobileClockFlags.None);
            Mob(2, 125, 100, MobileClockFlags.None);
            Mob(3, 126, 100, MobileClockFlags.None);
            Mob(4, 100, 126, MobileClockFlags.None);
            Mob(5, 300, 300, MobileClockFlags.Stepping);
            Mob(6, 100, 100, MobileClockFlags.Stepping | MobileClockFlags.HandlesOpening);
            Mob(7, 400, 400, MobileClockFlags.None); // the player: skipped
            mirror.UpsertItem(0x4000_0001, 0x0EED, 0, 200, 200, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);
            mirror.UpsertItem(0x4000_0002, 0x0EED, 0, 110, 100, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);

            var beyond = new List<uint>();
            var stepping = new List<uint>();
            sys.CollectMobiles(100, 100, 25, 7, beyond, stepping);
            beyond.OrderBy(x => x).Should().Equal(3u, 4u);
            stepping.OrderBy(x => x).Should().Equal(5u, 6u);

            beyond.Clear();
            sys.CollectItems(100, 100, 25, beyond);
            beyond.Should().Equal(0x4000_0001u);

            RangeSweepRules.Beyond(125, 100, 100, 100, 25).Should().BeFalse();
            RangeSweepRules.Beyond(126, 100, 100, 100, 25).Should().BeTrue();
            RangeSweepRules.Beyond(100, 74, 100, 100, 25).Should().BeTrue();
        }
    }
}
