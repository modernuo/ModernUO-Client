using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // item_pool: World offers the destroyed items it drops from Items to its ItemPool; after the quarantine GetOrCreateItem
    // hands one out again, reset to a new Item's state. Off, nothing is pooled and every new serial is a new object.
    public class ItemPoolTests
    {
        private const uint PLAYER = 0x0000_0100;
        private const uint ROCK = 0x4000_2001, COIN = 0x4000_2002, BAG = 0x4000_2003, GEM = 0x4000_2004;

        private static World Build(bool pool)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out WorldWriterMobileTests.TestPlayer _, PLAYER);
            world.ItemPoolOverride = pool;

            return world;
        }

        private static Item AddItem(World w, uint serial, uint container = 0xFFFF_FFFF)
        {
            Item i = w.GetOrCreateItem(serial);

            if (container == 0xFFFF_FFFF)
            {
                i.SetInWorldTile(1000, 1001, 0);
            }

            w.Ecs.Sync(i);
            i.TestSetGraphic(0x0EED);

            if (container != 0xFFFF_FFFF)
            {
                i.TestSetContainer(container);
            }

            return i;
        }

        // a commit stamped one quarantine ago, so a Take at the current clock (whatever other tests set it to) finds it ready
        private static void CommitReady(World w) => w.ItemPool.Commit(Time.Ticks - ItemPool.QuarantineMs - 1);

        [Fact]
        public void The_Setting_Defaults_On()
        {
            new ClassicUO.Configuration.Settings().ItemPool.Should().BeTrue();
        }

        [Fact]
        public void Off_Nothing_Is_Pooled_And_A_New_Serial_Is_A_New_Object()
        {
            World w = Build(false);
            bool createdBefore = w.ItemPoolCreated; // the test World may have been built with the setting's default (on)
            Item rock = AddItem(w, ROCK);
            Item coin = AddItem(w, COIN);
            w.RemoveItem(ROCK, true);
            w.RemoveItem(COIN);
            w.DropDestroyedItems();
            w.ReleasePools();
            w.ItemPoolCreated.Should().Be(createdBefore, "with the switch off the World does not create its pool");
            CommitReady(w);

            w.ItemPool.Offered.Should().Be(0);
            w.ItemPool.Count.Should().Be(0);
            Item bag = AddItem(w, BAG);
            bag.Should().NotBeSameAs(rock).And.NotBeSameAs(coin);
            rock.Incarnation.Should().Be(0);
        }

        [Fact]
        public void On_A_Swept_Or_Force_Removed_Item_Comes_Back_Reset_After_The_Quarantine()
        {
            ItemPool.CheckOverride = true;
            long mismatches = ItemPool.Mismatches;

            try
            {
                World w = Build(true);
                Item rock = AddItem(w, ROCK);
                Item coin = AddItem(w, COIN);
                rock.TestSetHue(0x21);
                w.RemoveItem(ROCK, true); // forced: dropped from Items at once
                w.RemoveItem(COIN); // swept by DropDestroyedItems
                w.DropDestroyedItems();

                w.ItemPool.Offered.Should().Be(2);
                w.ItemPool.PendingCount.Should().Be(2);
                rock.InItemPool.Should().BeTrue();

                CommitReady(w);
                w.ItemPool.Retired.Should().Be(2);
                rock.IsDestroyed.Should().BeTrue("a quarantined item stays destroyed");

                // past the quarantine (the oldest first: rock, then coin)
                uint incarnation = rock.Incarnation;
                Item bag = AddItem(w, BAG);

                bag.Should().BeSameAs(rock);
                bag.Serial.Should().Be(BAG);
                bag.Incarnation.Should().Be(incarnation + 1);
                bag.IsDestroyed.Should().BeFalse();
                bag.Hue.Should().Be(0);
                bag.InItemPool.Should().BeFalse();
                w.FindItem(BAG).Should().BeSameAs(bag);
                w.FindItem(ROCK).Should().BeNull();
                w.Holds(bag).Should().BeTrue();

                Item gem = AddItem(w, GEM);
                gem.Should().BeSameAs(coin);
                w.ItemPool.Reused.Should().Be(2);
                w.ItemPool.Count.Should().Be(0);
                AddItem(w, ROCK).Should().NotBeSameAs(rock).And.NotBeSameAs(coin, "the pool is empty: a new Item");

                ItemPool.Mismatches.Should().Be(mismatches, "every reused item equals a new Item after the reset");
                ItemPool.Checks.Should().BeGreaterThan(0);
            }
            finally
            {
                ItemPool.CheckOverride = null;
            }
        }

        [Fact]
        public void Retired_Items_Come_Back_Only_After_The_Quarantine_Oldest_First()
        {
            World w = Build(true);
            // stamped just before the clock, so the GetOrCreateItem between the commits (at Time.Ticks) finds none ready
            uint t0 = Time.Ticks - 100;
            Item rock = AddItem(w, ROCK);
            w.RemoveItem(ROCK, true);
            w.ItemPool.Commit(t0);
            Item coin = AddItem(w, COIN);
            coin.Should().NotBeSameAs(rock);
            w.RemoveItem(COIN, true);
            w.ItemPool.Commit(t0 + 600);

            w.ItemPool.Take(t0).Should().BeNull();
            w.ItemPool.Take(t0 + ItemPool.QuarantineMs - 1).Should().BeNull("still in quarantine");
            w.ItemPool.Take(t0 + ItemPool.QuarantineMs).Should().BeSameAs(rock);
            w.ItemPool.Take(t0 + ItemPool.QuarantineMs).Should().BeNull();
            w.ItemPool.Take(t0 + 600 + ItemPool.QuarantineMs).Should().BeSameAs(coin);
        }

        [Fact]
        public void A_Container_And_Its_Contents_Are_Pooled_Together()
        {
            World w = Build(true);
            int before = w.Items.Count;
            AddItem(w, BAG);
            w.Writer.PlaceInContainer(COIN, 0x0EED, 5, 10, 10, 0, BAG, out _);
            w.Writer.PlaceInContainer(GEM, 0x0F10, 1, 20, 20, 0, BAG, out _);
            w.Items.Count.Should().Be(before + 3);
            w.RemoveItem(BAG, true);
            CommitReady(w);

            w.ItemPool.Retired.Should().Be(3);
            w.Items.Count.Should().Be(before);

            // reused by the funnel: a container's content comes back in its new container with the new values only
            AddItem(w, ROCK);
            Item coin = w.Writer.PlaceInContainer(0x4000_2010, 0x0EEA, 7, 1, 2, 0, ROCK, out _);
            coin.InItemPool.Should().BeFalse();
            coin.Container.Should().Be(ROCK);
            coin.Amount.Should().Be(7);
            w.ItemPool.Reused.Should().Be(2);
        }

        [Fact]
        public void Items_Still_Listed_Are_Refused_And_The_Selection_Is_Scrubbed()
        {
            World w = Build(true);
            Item rock = AddItem(w, ROCK);
            Item coin = AddItem(w, COIN);
            w.RemoveItem(ROCK, true);
            w.RemoveItem(COIN, true);

            coin.InTextOwners = true; // the scene has not dropped it from World.TextOwners yet
            SelectedObject.LastLeftDownObject = rock;

            CommitReady(w);

            w.ItemPool.Retired.Should().Be(1);
            w.ItemPool.Refused.Should().Be(1);
            coin.InItemPool.Should().BeFalse();
            SelectedObject.LastLeftDownObject.Should().BeNull();
        }

        [Fact]
        public void Detached_And_Live_Items_Never_Reach_The_Pool()
        {
            World w = Build(true);
            Item preview = Item.Create(w, 0x4000_3000);
            w.ItemPool.Offer(preview); // alive
            Item rock = AddItem(w, ROCK);
            w.ItemPool.Offer(rock); // alive and held

            CommitReady(w);

            w.ItemPool.Offered.Should().Be(0);
            w.ItemPool.Count.Should().Be(0);
        }

        [Fact]
        public void A_Closed_Pool_Keeps_Nothing()
        {
            World w = Build(true);
            AddItem(w, ROCK);
            w.RemoveItem(ROCK, true);
            CommitReady(w);
            w.ItemPool.Count.Should().Be(1);

            w.ReleasePools();
            w.ItemPool.Count.Should().Be(0);
            AddItem(w, COIN);
            w.RemoveItem(COIN, true);
            CommitReady(w);
            w.ItemPool.Count.Should().Be(0);
        }

        [Fact]
        public void Texts_And_Damage_Do_Not_Follow_A_Reused_Owner()
        {
            World w = Build(true);
            Item rock = AddItem(w, ROCK);
            var text = TextObject.Create(w);
            text.Owner = rock;
            text.OwnerIncarnation = rock.Incarnation;
            var damage = new OverheadDamage(w, rock);

            text.LiveOwnerEntity.Should().BeSameAs(rock);
            damage.ParentReused.Should().BeFalse();

            w.RemoveItem(ROCK, true);
            text.LiveOwnerEntity.Should().BeSameAs(rock, "destroyed but not reused: as before the pool");
            CommitReady(w);
            AddItem(w, BAG).Should().BeSameAs(rock);

            text.LiveOwnerEntity.Should().BeNull("the owner was reused for another item");
            damage.ParentReused.Should().BeTrue();
        }

        [Fact]
        public void A_Bench_Pick_Target_Key_Rejects_A_Reused_Item()
        {
            World w = Build(true);
            Item rock = AddItem(w, ROCK);
            var key = new DevBench.PickKey(rock);
            key.Matches(rock).Should().BeTrue();

            w.RemoveItem(ROCK, true);
            key.Matches(rock).Should().BeFalse("destroyed");
            CommitReady(w);
            AddItem(w, BAG).Should().BeSameAs(rock);
            key.Matches(rock).Should().BeFalse("reused for another serial (Incarnation and Serial differ)");
            new DevBench.PickKey(rock).Matches(rock).Should().BeTrue();
            default(DevBench.PickKey).Matches(null).Should().BeFalse();
        }

        // ---- the reset: every field back to a new Item's value ----

        private static IEnumerable<FieldInfo> AllFields(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    yield return f;
                }
            }
        }

        // a value different from the current one (references: always a new non-null instance)
        private static object Dirty(Type t, object current, int salt)
        {
            if (t == typeof(bool)) return !(bool)current;
            if (t == typeof(byte)) return (byte)((byte)current + 7 + salt);
            if (t == typeof(sbyte)) return (sbyte)((sbyte)current + 7 + salt);
            if (t == typeof(short)) return (short)((short)current + 7 + salt);
            if (t == typeof(ushort)) return (ushort)((ushort)current + 7 + salt);
            if (t == typeof(int)) return (int)current + 7 + salt;
            if (t == typeof(uint)) return (uint)current + 7u + (uint)salt;
            if (t == typeof(long)) return (long)current + 7 + salt;
            if (t == typeof(ulong)) return (ulong)current + 7ul + (ulong)salt;
            if (t == typeof(float)) return (float)current + 7.5f + salt;
            if (t == typeof(double)) return (double)current + 7.5 + salt;
            if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(current) + 1);

            Type underlying = Nullable.GetUnderlyingType(t);

            if (underlying != null)
            {
                // a null Nullable boxes to null: dirty the underlying default (assignable to the Nullable field)
                return Dirty(underlying, current ?? Activator.CreateInstance(underlying), salt);
            }

            if (t.IsValueType)
            {
                object boxed = RuntimeHelpers.GetObjectValue(current);
                int i = 0;

                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    f.SetValue(boxed, Dirty(f.FieldType, f.GetValue(boxed), salt + i++));
                }

                return boxed;
            }

            if (t == typeof(string)) return "dirty" + salt;
            if (t.IsArray) return Array.CreateInstance(t.GetElementType(), 1 + salt % 3);

            if (t.IsAbstract || t.IsInterface)
            {
                if (t.IsAssignableFrom(typeof(Static)))
                {
                    return RuntimeHelpers.GetUninitializedObject(typeof(Static));
                }

                throw new NotSupportedException($"no sentinel for abstract type {t}");
            }

            return RuntimeHelpers.GetUninitializedObject(t);
        }

        private static void DirtyAll(Item item)
        {
            int salt = 0;

            foreach (FieldInfo f in AllFields(typeof(Item)))
            {
                if (f.FieldType == typeof(World))
                {
                    continue;
                }

                object before = f.GetValue(item);
                f.SetValue(item, Dirty(f.FieldType, before, salt++));
                object stored = f.GetValue(item);

                (f.FieldType.IsValueType ? !Equals(before, stored) : stored != null && !ReferenceEquals(before, stored))
                    .Should().BeTrue($"{f.DeclaringType.Name}.{f.Name} must change, or the reset check could not see it");
            }
        }

        [Fact]
        public void ResetForReuse_Puts_Every_Item_Field_Back_To_The_Constructors_Value()
        {
            var world = new World();
            var fresh = new Item(world);
            var reused = new Item(world);
            DirtyAll(reused);
            uint incarnation = reused.Incarnation;

            reused.ResetForReuse();

            reused.Incarnation.Should().Be(incarnation + 1);
            ReferenceEquals(reused.World, world).Should().BeTrue();
            ItemPool.ResetDifferences(fresh, reused).Should().BeEmpty();
        }

        [Fact]
        public void The_Checker_Sees_Every_Field_And_Catches_One_Left_Out()
        {
            var world = new World();
            var fresh = new Item(world);
            var dirty = new Item(world);
            DirtyAll(dirty);

            // the checker's field list is the whole chain's (World and Incarnation are skipped by the compare)
            ItemPool.Fields.Select(f => f.Name).Should().BeEquivalentTo(AllFields(typeof(Item)).Select(f => f.Name));
            ItemPool.ResetDifferences(fresh, dirty).Count().Should().Be(AllFields(typeof(Item)).Count() - 2);

            dirty.ResetForReuse();
            FieldInfo cache = typeof(GameObject).GetField("_textExpiredContainer", BindingFlags.Instance | BindingFlags.NonPublic);
            cache.SetValue(dirty, Dirty(cache.FieldType, null, 0));
            ItemPool.ResetDifferences(fresh, dirty).Should().ContainSingle().Which.Should().Contain("_textExpiredContainer");
        }
    }
}
