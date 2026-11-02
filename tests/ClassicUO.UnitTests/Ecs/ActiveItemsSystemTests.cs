using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Friflo.Engine.ECS;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T5d: World.Update as ordered stages, and the active item list owned by the ECS side (world_active_items_system).
    // The system's list and its ItemActive tags are checked against the legacy rules over the same constructed worlds.
    public class ActiveItemsSystemTests
    {
        private const uint PLAYER = 0x0000_0100;

        private sealed class Scene
        {
            public World World;
            public readonly List<string> Mismatches = new List<string>();
            public readonly List<WorldStage> Stages = new List<WorldStage>();
        }

        private static Scene Build(bool system)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out WorldWriterMobileTests.TestPlayer player, PLAYER);

            // the mobile stage is not under test (its updates need the animation data): the world holds no mobile
            foreach (uint serial in world.Mobiles.Values.Select(m => m.Serial).ToList())
            {
                world.Mobiles.Remove(serial);
            }

            var scene = new Scene { World = world };
            world.RangeSize.X = 1000;
            world.RangeSize.Y = 1000;
            world.ActiveItemsSystemOverride = system;
            world.ActiveItemsCheckOverride = true;
            world.ActiveItemsObserver = scene.Mismatches.Add;
            world.StageObserver = scene.Stages.Add;

            return scene;
        }

        private static Item AddItem(Scene s, uint serial, ushort graphic, int x = 1000, int y = 1000)
        {
            Item i = s.World.GetOrCreateItem(serial);
            i.Graphic = graphic;
            i.LastAnimationChangeTime = long.MaxValue / 2; // a corpse is not due for a frame (that needs the animation data)
            i.SetInWorldTile((ushort)x, (ushort)y, 0);
            s.World.Ecs.Sync(i);

            return i;
        }

        private static List<uint> Active(World w) => w.ActiveItems.Select(i => i.Serial).ToList();

        private static int Tagged(World w) => w.Ecs.Mirror.Store.Query().AllTags(Tags.Get<ItemActive>()).Count;

        [Fact]
        public void The_Stages_Run_In_The_Order_The_Body_Had()
        {
            foreach (bool system in new[] { false, true })
            {
                Scene s = Build(system);
                s.World.Update();

                s.Stages.Should().Equal(
                    WorldStage.ChunkLoading,
                    WorldStage.ObjectToRemove,
                    WorldStage.Mobiles,
                    WorldStage.ActiveItems,
                    WorldStage.Effects,
                    WorldStage.WorldText,
                    WorldStage.WorldMapPrune,
                    WorldStage.EcsBookkeeping);
            }
        }

        [Fact]
        public void Without_A_Player_No_Stage_Runs()
        {
            var world = new World();
            var stages = new List<WorldStage>();
            world.StageObserver = stages.Add;
            world.Update();
            stages.Should().BeEmpty();
        }

        [Fact]
        public void The_Setting_Is_On_By_Default()
        {
            ClassicUO.Configuration.Settings.GlobalSettings.WorldActiveItemsSystem.Should().BeTrue();
        }

        // corpses (items whose handles just opened open a gump, which needs the client: the in-client checker covers them), destroy, a re-synced record: the list order and the survivors are
        // those of the legacy list with the system off, and the tags equal the list with it on
        [Fact]
        public void Membership_And_Order_Match_The_Legacy_List()
        {
            Scene off = Build(false), on = Build(true);

            foreach (Scene s in new[] { off, on })
            {
                Item c1 = AddItem(s, 0x4000_0001, 0x2006);
                Item plain = AddItem(s, 0x4000_0002, 0x0EED);
                Item c2 = AddItem(s, 0x4000_0003, 0x2006);
                AddItem(s, 0x4000_0004, 0x2006);
                Item c3 = AddItem(s, 0x4000_0005, 0x2006);

                foreach (Item i in new[] { c1, c2, c3 })
                {
                    s.World.QueueItemUpdate(i);
                }

                s.World.QueueItemUpdate(c1); // twice: once
                s.World.QueueItemUpdate(s.World.Items[0x4000_0004]);
                s.World.QueueItemUpdate(plain); // queued but needs nothing: leaves on the first update
            }

            Active(on.World).Should().Equal(Active(off.World));
            Active(on.World).Should().Equal(0x4000_0001u, 0x4000_0003u, 0x4000_0004u, 0x4000_0005u, 0x4000_0002u); // corpses queue themselves in creation order
            Tagged(on.World).Should().Be(5);
            Tagged(off.World).Should().Be(0);

            for (int frame = 0; frame < 6; frame++)
            {
                foreach (Scene s in new[] { off, on })
                {
                    switch (frame)
                    {
                        case 1:
                            s.World.Items[0x4000_0003].Graphic = 0x0EED; // no longer a corpse: swap-removed
                            break;

                        case 2:
                            s.World.Ecs.Sync(s.World.Items[0x4000_0005]); // the record refreshed
                            break;

                        case 3:
                            s.World.Items[0x4000_0001].Destroy(); // destroyed while listed
                            break;

                        case 4:
                            s.World.Items[0x4000_0003].Graphic = 0x2006; // a corpse again, queued again
                            s.World.QueueItemUpdate(s.World.Items[0x4000_0003]);
                            break;
                    }

                    s.World.Update();
                }

                Active(on.World).Should().Equal(Active(off.World), $"frame {frame}");
                on.World.Items.Keys.OrderBy(x => x).Should().Equal(off.World.Items.Keys.OrderBy(x => x), $"frame {frame}");
                Tagged(on.World).Should().Be(on.World.ActiveItemCount, $"frame {frame}");
                Tagged(off.World).Should().Be(0);
            }

            on.Mismatches.Should().BeEmpty();
            off.Mismatches.Should().BeEmpty();
            on.World.ActiveItemsMismatches.Should().Be(0);
            on.World.ActiveItemsChecks.Should().BeGreaterThan(0);
            on.World.Items.ContainsKey(0x4000_0001).Should().BeFalse("destroyed items leave Items");
        }

        // mutant guard: a tag the list does not explain is reported
        [Fact]
        public void The_Checker_Reports_A_Stray_Tag_And_A_Missing_Tag()
        {
            Scene s = Build(true);
            Item c = AddItem(s, 0x4000_0001, 0x2006);
            Item other = AddItem(s, 0x4000_0002, 0x0EED);
            s.World.QueueItemUpdate(c);

            c.TryScalarEntity(out EcsEntity ce).Should().BeTrue();
            other.TryScalarEntity(out EcsEntity oe).Should().BeTrue();

            ce.RemoveTag<ItemActive>();
            s.World.Update();
            s.Mismatches.Should().NotBeEmpty("a listed item without its tag");

            s.Mismatches.Clear();
            ce.AddTag<ItemActive>();
            oe.AddTag<ItemActive>();
            s.World.Update();
            s.Mismatches.Should().NotBeEmpty("a tagged entity that is not listed");
        }

        // P2 (Codex): a replaced mirror record (a death re-key shares a serial between a mobile and its corpse) starts without
        // the tag; the item is still listed, so Add would not repair it. The record write reconciles the tag.
        [Fact]
        public void A_Replaced_Record_Of_A_Listed_Item_Keeps_Its_Tag()
        {
            World world = WorldWriterMobileTests.BuildDead();
            world.ActiveItemsSystemOverride = true;
            Item corpse = world.Items.Get(WorldWriterMobileTests.DEAD);
            Mobile m = world.Mobiles.Get(WorldWriterMobileTests.DEAD);
            world.Ecs.Sync(corpse);
            world.QueueItemUpdate(corpse);
            world.ActiveItems.Should().Contain(corpse);
            Tagged(world).Should().Be(1);

            world.Ecs.Sync(m); // the mobile's record replaces the item's
            world.Ecs.Sync(corpse); // and the item's replaces it again: a new entity

            corpse.TryScalarEntity(out EcsEntity e).Should().BeTrue();
            e.Tags.Has<ItemActive>().Should().BeTrue();
            world.QueueItemUpdate(corpse); // already listed: no repair from the list
            Tagged(world).Should().Be(1);
        }

        // P3 (Codex): the checker's duplicate test is by reference; a destroyed corpse and its successor share a serial
        [Fact]
        public void The_Checker_Tells_Two_Items_Of_One_Serial_Apart()
        {
            Scene s = Build(true);
            Item first = AddItem(s, 0x4000_0001, 0x2006);
            s.World.QueueItemUpdate(first);
            first.Destroy();
            s.World.Items.Remove(0x4000_0001);
            Item second = AddItem(s, 0x4000_0001, 0x2006);
            s.World.QueueItemUpdate(second);
            s.World.ActiveItemCount.Should().Be(2);

            s.World.Update();

            s.Mismatches.Should().NotContain(x => x.Contains("listed twice"));
        }
    }
}
