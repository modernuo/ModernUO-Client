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

namespace ClassicUO.UnitTests.Ecs
{
    // T4 (ecs-transition-plan.md, "thin handles; the ECS owns scalar state") and T9 (cold-caches): the getters of Name, Hits,
    // HitsMax, Stamina, StaminaMax, Mana, ManaMax and Price read the entity's mirror record through its cached handle, and
    // the defaults (null, 0) for an entity without a held record (no cache behind them since T9). Over the funnel's
    // operations, including the death re-key, removal, serial reuse and objects the mirror does not hold.
    public class EcsScalarsTests
    {
        private const uint MOB = 0x0000_3900, VENDOR = 0x0000_3901, STAT = 0x0000_3902, C = 0x4000_7903, B = 0x4000_7902;

        // ---- the reads: the record through the handle, the default without one

        private static readonly (string Name, Func<Entity, object> Read)[] Fields =
        {
            ("Name", e => e.TryScalarEntity(out var x) ? EcsScalars.Name(x) : null),
            ("Hits", e => e.TryScalarEntity(out var x) ? EcsScalars.Hits(x) : (ushort)0),
            ("HitsMax", e => e.TryScalarEntity(out var x) ? EcsScalars.HitsMax(x) : (ushort)0),
            ("Stamina", e => e is Mobile m ? (m.TryScalarEntity(out var x) ? EcsScalars.Stamina(x) : (ushort)0) : null),
            ("StaminaMax", e => e is Mobile m ? (m.TryScalarEntity(out var x) ? EcsScalars.StaminaMax(x) : (ushort)0) : null),
            ("Mana", e => e is Mobile m ? (m.TryScalarEntity(out var x) ? EcsScalars.Mana(x) : (ushort)0) : null),
            ("ManaMax", e => e is Mobile m ? (m.TryScalarEntity(out var x) ? EcsScalars.ManaMax(x) : (ushort)0) : null),
            ("Price", e => e is Item i ? (i.TryScalarEntity(out var x) ? EcsScalars.Price(x) : 0u) : null),
        };

        private static object Getter(Entity e, string field) => field switch
        {
            "Name" => e.Name,
            "Hits" => e.Hits,
            "HitsMax" => e.HitsMax,
            "Stamina" => (e as Mobile)?.Stamina,
            "StaminaMax" => (e as Mobile)?.StaminaMax,
            "Mana" => (e as Mobile)?.Mana,
            "ManaMax" => (e as Mobile)?.ManaMax,
            "Price" => (e as Item)?.Price,
            _ => throw new ArgumentException(field)
        };

        // every field of every entity: the getter is the read above
        private static void AssertSame(IEnumerable<Entity> entities)
        {
            foreach (Entity e in entities)
            {
                foreach (var (name, read) in Fields)
                {
                    Getter(e, name).Should().Be(read(e), $"{name} getter of 0x{e.Serial:X8}");
                }
            }
        }

        private static IEnumerable<Entity> All(World world) => world.Mobiles.Values.Cast<Entity>().Concat(world.Items.Values);

        private static World Build()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.SetMana(m, 80, 70);
            world.Writer.SetStamina(m, 90, 85);
            // only the pools on a mobile without hits: Vitals added by the pool write
            Mobile vendor = world.Mobiles.Get(VENDOR);
            world.Writer.SetStamina(vendor, 5, 4);
            world.Writer.SetMana(world.Mobiles.Get(STAT), 3, 2);

            return world;
        }

        private static List<string> Check(World world)
        {
            var reported = new List<string>();
            world.Ecs.MismatchObserver = reported.Add;
            world.Ecs.RunCheck(false);
            world.Ecs.MismatchObserver = null;

            return reported;
        }

        [Fact]
        public void Every_Converted_Field_Reads_Its_Record()
        {
            World world = Build();

            Check(world).Should().BeEmpty();
            AssertSame(All(world));

            Mobile m = world.Mobiles.Get(MOB);
            m.TryScalarEntity(out var e).Should().BeTrue();
            (EcsScalars.Name(e), EcsScalars.Hits(e), EcsScalars.HitsMax(e), EcsScalars.Mana(e), EcsScalars.ManaMax(e), EcsScalars.Stamina(e), EcsScalars.StaminaMax(e))
                .Should().Be(("Moe the Brave", (ushort)55, (ushort)60, (ushort)70, (ushort)80, (ushort)85, (ushort)90));
            world.Items.Get(C).TryScalarEntity(out var c).Should().BeTrue();
            EcsScalars.Price(c).Should().Be(120u);

            // absent components read as the GameObject defaults
            world.Mobiles.Get(VENDOR).TryScalarEntity(out var v).Should().BeTrue();
            (EcsScalars.Name(v), EcsScalars.Hits(v), EcsScalars.Mana(v), EcsScalars.Stamina(v)).Should().Be(((string)null, (ushort)0, (ushort)0, (ushort)4));
            world.Items.Get(B).TryScalarEntity(out var b).Should().BeTrue();
            EcsScalars.Price(b).Should().Be(0u);
        }

        [Fact]
        public void The_Pools_And_The_Hits_Share_Vitals_Without_Overwriting_Each_Other()
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB);

            world.Writer.SetHits(m, 70, 65);
            world.Writer.SetStatus(m, "Moe", 64, 70);
            world.Writer.SetMana(m, 81, 71);

            AssertSame(All(world));
            world.Ecs.Mirror.TryGetExtras(MOB, out MirrorExtras x).Should().BeTrue();
            (x.Hits, x.HitsMax, x.Stamina, x.StaminaMax, x.Mana, x.ManaMax).Should().Be(((ushort)64, (ushort)70, (ushort)85, (ushort)90, (ushort)71, (ushort)81));

            // a zero write on a record without Vitals adds nothing; on one with Vitals it writes the zeros
            Mobile stat2 = world.GetOrCreateMobile(0x0000_3910);
            world.Writer.SetMana(stat2, 0, 0);
            world.Ecs.Mirror.TryGetEntity(0x0000_3910, out var e).Should().BeTrue();
            e.HasComponent<Vitals>().Should().BeFalse();
            world.Writer.SetMana(m, 0, 0);
            world.Ecs.Mirror.TryGetExtras(MOB, out x);
            (x.Mana, x.ManaMax, x.Stamina).Should().Be(((ushort)0, (ushort)0, (ushort)85));
            AssertSame(All(world));
        }

        [Fact]
        public void The_Death_Rekey_Carries_The_Pools_And_The_Handle_Follows_The_New_Serial()
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB);
            m.TryScalarEntity(out _).Should().BeTrue();

            uint dead = world.Writer.RekeyDeadMobile(m);

            m.Serial.Should().Be(dead);
            Check(world).Should().BeEmpty();
            AssertSame(All(world));
            m.TryScalarEntity(out var e).Should().BeTrue();
            e.GetComponent<ClassicUO.Ecs.Serial>().Value.Should().Be(dead);
            (EcsScalars.Mana(e), EcsScalars.Stamina(e), EcsScalars.Name(e)).Should().Be(((ushort)70, (ushort)85, "Moe the Brave"));
        }

        [Fact]
        public void The_Old_Path_Rekey_Carries_The_Pools()
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB);
            WorldWriterMobileTests.OldRekey(world, m);

            world.Ecs.Mirror.TryGetExtras(m.Serial, out MirrorExtras x).Should().BeTrue();
            (x.Mana, x.ManaMax, x.Stamina, x.StaminaMax).Should().Be(((ushort)70, (ushort)80, (ushort)85, (ushort)90));
            AssertSame(All(world));
        }

        [Fact]
        public void A_Removed_Entity_Reads_The_Defaults_And_Another_Removal_Revalidates_The_Rest()
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB), stat = world.Mobiles.Get(STAT);
            m.TryScalarEntity(out _).Should().BeTrue();
            stat.TryScalarEntity(out _).Should().BeTrue();
            uint epoch = world.Ecs.Mirror.Epoch;

            world.Writer.RemoveMobile(stat);

            world.Ecs.Mirror.Epoch.Should().NotBe(epoch);
            stat.IsDestroyed.Should().BeTrue();
            stat.TryScalarEntity(out _).Should().BeFalse();
            stat.Name.Should().BeNull();
            stat.Mana.Should().Be(0);
            // the epoch moved: the handle is looked up again and still finds the mobile's own record
            m.TryScalarEntity(out var e).Should().BeTrue();
            m.EcsEntityEpoch.Should().Be(world.Ecs.Mirror.Epoch);
            EcsScalars.Name(e).Should().Be("Moe the Brave");
            AssertSame(All(world).Append(stat));
        }

        [Fact]
        public void Serial_Reuse_The_Old_Instance_Reads_The_Defaults_The_New_One_Its_Record()
        {
            World world = Build();
            Mobile old = world.Mobiles.Get(STAT);
            old.TryScalarEntity(out _).Should().BeTrue();
            world.Writer.RemoveMobile(old);

            Mobile again = world.GetOrCreateMobile(STAT);
            again.Should().NotBeSameAs(old);
            world.Writer.SetStatus(again, "Stella", 1, 2);
            world.Writer.SetMana(again, 9, 8);

            old.TryScalarEntity(out _).Should().BeFalse();
            (old.Name, old.Hits, old.Mana).Should().Be(((string)null, (ushort)0, (ushort)0));
            again.TryScalarEntity(out _).Should().BeTrue();
            (again.Name, again.Hits, again.Mana).Should().Be(("Stella", (ushort)1, (ushort)8));
            AssertSame(All(world).Append(old));

            // an item serial reused the same way
            Item c = world.Items.Get(C);
            world.Writer.RemoveWorldItem(c, out _);
            Item c2 = world.Writer.UpsertWorldItem(C, 0xEEF, 0, 1, 1003, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            c2.Should().NotBeSameAs(c);
            c.Price.Should().Be(0u);
            c2.Price.Should().Be(0u);
            AssertSame(All(world).Append(c));
        }

        [Fact]
        public void An_Instance_World_Does_Not_Hold_Never_Reads_Another_Instances_Record()
        {
            World world = Build();

            // a detached mobile under a held mobile's serial (a character-creation preview, a stale instance)
            var preview = new Mobile(world, MOB);
            preview.TryScalarEntity(out _).Should().BeFalse();
            preview.Name.Should().BeNull();
            preview.Mana.Should().Be(0);

            // the E5 draw cached a handle without the identity check: the scalar reads look it up again
            world.Ecs.Mirror.TryGetEntity(MOB, out var e).Should().BeTrue();
            preview.EcsEntity = e;
            preview.EcsEntitySerial = MOB;
            preview.EcsEntityEpoch = world.Ecs.Mirror.Epoch;
            preview.EcsEntityHeld = false;
            preview.TryScalarEntity(out _).Should().BeFalse();
            preview.Name.Should().BeNull();

            // a serial without a record (not mirrored), then the record arrives: a miss is not cached
            Mobile held = world.Mobiles.Get(MOB);
            AssertSame(new Entity[] { preview, held });
        }

        [Fact]
        public void A_Miss_Is_Not_Cached_So_A_Record_Created_Later_Is_Found()
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB);
            uint epoch = world.Ecs.Mirror.Epoch;

            // the record goes without an epoch-visible change to this mobile's handle: removed and re-created under the serial
            world.Ecs.Mirror.Remove(MOB);
            m.TryScalarEntity(out _).Should().BeFalse();
            m.Name.Should().BeNull();

            world.Ecs.Sync(m);
            world.Ecs.Mirror.SetName(MOB, "Moe the Brave");
            world.Ecs.Mirror.Epoch.Should().NotBe(epoch);
            m.TryScalarEntity(out var e).Should().BeTrue();
            EcsScalars.Name(e).Should().Be("Moe the Brave");
        }

        [Fact]
        public void A_Destroyed_But_Held_Entity_Reads_The_Defaults()
        {
            World world = Build();
            Item c = world.Items.Get(C);
            c.TryScalarEntity(out _).Should().BeTrue();

            c.Destroy();

            c.IsDestroyed.Should().BeTrue();
            c.TryScalarEntity(out _).Should().BeFalse();
            c.Price.Should().Be(0u);
        }

        private static void SetDestroyedFlag(GameObject o) =>
            typeof(GameObject).GetProperty(nameof(GameObject.IsDestroyed)).SetMethod.Invoke(o, new object[] { true });

        // Destroy() also removes the record, so the destroyed check alone is never exercised there. Here the flag is set
        // while World still holds the instance and the mirror still has its record: the getters must not read it.
        [Fact]
        public void A_Destroyed_Instance_Whose_Record_Is_Still_In_The_Mirror_Reads_The_Defaults()
        {
            foreach (bool heldHandle in new[] { true, false })
            {
                World world = Build();
                Mobile m = world.Mobiles.Get(MOB);
                Item c = world.Items.Get(C);
                m.TryScalarEntity(out _).Should().BeTrue();
                c.TryScalarEntity(out _).Should().BeTrue();

                SetDestroyedFlag(m);
                SetDestroyedFlag(c);
                world.Mobiles.Get(MOB).Should().BeSameAs(m);
                world.Ecs.Mirror.TryGetEntity(MOB, out _).Should().BeTrue();
                world.Ecs.Mirror.TryGetEntity(C, out _).Should().BeTrue();

                if (!heldHandle)
                {
                    // no valid handle: the lookup path must refuse the destroyed instance too
                    m.EcsEntityHeld = false;
                    c.EcsEntityHeld = false;
                }

                m.IsDestroyed.Should().BeTrue();
                m.TryScalarEntity(out _).Should().BeFalse($"destroyed mobile, held handle: {heldHandle}");
                c.TryScalarEntity(out _).Should().BeFalse($"destroyed item, held handle: {heldHandle}");
                (m.Mana, m.Name, c.Price).Should().Be(((ushort)0, (string)null, 0u));
            }
        }

        // The record is changed by a write that bypasses the funnel; the getter of that field follows it and no other changes.
        public static IEnumerable<object[]> ConvertedFields() => Fields.Select(f => new object[] { f.Name });

        [Theory]
        [MemberData(nameof(ConvertedFields))]
        public void Each_Getter_Reads_Its_Own_Record_Field(string field)
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB);
            Item c = world.Items.Get(C);
            Entity target = field == "Price" ? c : m;
            object record = field switch
            {
                "Name" => "Moe the Brave",
                "Hits" => (ushort)55,
                "HitsMax" => (ushort)60,
                "Stamina" => (ushort)85,
                "StaminaMax" => (ushort)90,
                "Mana" => (ushort)70,
                "ManaMax" => (ushort)80,
                "Price" => 120u,
                _ => throw new ArgumentException(field)
            };
            Getter(target, field).Should().Be(record, "before the write");

            object written = field switch
            {
                "Name" => "written name",
                "Price" => 7u,
                _ => (ushort)11
            };

            WorldMirror mirror = world.Ecs.Mirror;

            switch (field)
            {
                case "Name": mirror.SetName(MOB, (string)written); break;
                case "Hits": mirror.SetVitals(MOB, 11, 60); break;
                case "HitsMax": mirror.SetVitals(MOB, 55, 11); break;
                case "Stamina": mirror.SetStamina(MOB, 90, 11); break;
                case "StaminaMax": mirror.SetStamina(MOB, 11, 85); break;
                case "Mana": mirror.SetMana(MOB, 80, 11); break;
                case "ManaMax": mirror.SetMana(MOB, 11, 70); break;
                case "Price": mirror.SetPrice(C, 7u); break;
            }

            target.TryScalarEntity(out _).Should().BeTrue();
            Getter(target, field).Should().Be(written, $"{field} getter");

            // the other converted fields of the same entity are untouched by this write
            foreach (var (name, _) in Fields)
            {
                if (name == field || (name == "Price") != (target is Item))
                {
                    continue;
                }

                Getter(target, name).Should().NotBe(written);
            }
        }

        // P2 (Codex): a death-rekeyed mobile and a corpse item share a serial and the mirror keeps one record per serial. The
        // item's creation replaced the mobile's record; the mobile must not read (or write) the item's record.
        [Fact]
        public void A_Rekeyed_Mobile_And_An_Item_Sharing_A_Serial_Never_Read_Or_Write_Each_Others_Record()
        {
            World world = WorldWriterMobileTests.BuildDead();
            const uint DEAD = WorldWriterMobileTests.DEAD;
            Mobile m = world.Mobiles.Get(DEAD);
            Item corpse = world.Items.Get(DEAD);
            world.Ecs.Mirror.TryGetEntity(DEAD, false, out _).Should().BeTrue("the item's record is under the serial");
            world.Ecs.Mirror.TryGetEntity(DEAD, true, out _).Should().BeFalse();

            world.Writer.SetStatus(m, "Moe", 30, 40);
            world.Writer.SetMana(m, 20, 10);
            world.Writer.SetStamina(m, 22, 11);

            // the mobile has no record of its kind, so its commits are dropped and it reads the defaults; the item's record got
            // none of it
            m.TryScalarEntity(out _).Should().BeFalse();
            (m.Name, m.Hits, m.HitsMax, m.Mana, m.ManaMax, m.Stamina, m.StaminaMax)
                .Should().Be(((string)null, (ushort)0, (ushort)0, (ushort)0, (ushort)0, (ushort)0, (ushort)0));
            world.Ecs.Mirror.TryGetExtras(DEAD, out MirrorExtras x).Should().BeTrue();
            (x.Name, x.Hits, x.Mana, x.Stamina).Should().Be(((string)null, (ushort)0, (ushort)0, (ushort)0));
            corpse.TryScalarEntity(out _).Should().BeTrue();
            (corpse.Name, corpse.Hits).Should().Be(((string)null, (ushort)0));

            // the item's own writes do not reach the mobile
            world.Writer.SetStatus(corpse, "a corpse", 1, 2);
            (m.Name, m.Hits).Should().Be(((string)null, (ushort)0));
            corpse.Name.Should().Be("a corpse");

            // the mobile's record replacing the item's: ColdScalarsTests.A_Kind_Replacement_Starts_Without_Cold_Values_...
        }

        [Fact]
        public void The_Mana_Pool_Of_A_Record_Without_Vitals_Is_Added_With_Its_Value()
        {
            var mirror = new WorldMirror();
            mirror.UpsertMobile(0x1234, 0x190, 0, 1, 1, 0, 0, 0, false);
            mirror.SetMana(0x1234, 20, 19).Should().BeTrue();
            mirror.TryGetExtras(0x1234, out MirrorExtras x).Should().BeTrue();
            (x.Mana, x.ManaMax).Should().Be(((ushort)19, (ushort)20));
        }

        // a commit for an instance World does not hold (a detached preview under a held serial) must not write the record
        [Fact]
        public void A_Commit_From_An_Instance_World_Does_Not_Hold_Leaves_The_Record_Alone()
        {
            World world = Build();
            var preview = new Mobile(world, MOB);

            world.Ecs.CommitMana(preview, 7, 6);
            world.Ecs.CommitStamina(preview, 7, 6);
            world.Ecs.CommitName(preview, "intruder");
            world.Ecs.CommitVitals(preview, 1, 2);

            world.Ecs.Mirror.TryGetExtras(MOB, out MirrorExtras x).Should().BeTrue();
            (x.Name, x.Hits, x.HitsMax, x.Mana, x.ManaMax, x.Stamina, x.StaminaMax).Should().Be(("Moe the Brave", (ushort)55, (ushort)60, (ushort)70, (ushort)80, (ushort)85, (ushort)90));
        }

        [Fact]
        public void A_Wrong_Cached_Handle_Under_The_Same_Serial_And_Epoch_Is_Not_Trusted_Unless_Held_Was_Set()
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB);
            world.Ecs.Mirror.TryGetEntity(VENDOR, out var vendor).Should().BeTrue();

            m.EcsEntity = vendor;
            m.EcsEntitySerial = MOB;
            m.EcsEntityEpoch = world.Ecs.Mirror.Epoch;
            m.EcsEntityHeld = false;

            m.TryScalarEntity(out var e).Should().BeTrue();
            EcsScalars.Name(e).Should().Be("Moe the Brave");
            m.EcsEntityHeld.Should().BeTrue();

            // a held handle cached for another serial (the serial changed without a deletion) is not trusted either
            m.EcsEntity = vendor;
            m.EcsEntitySerial = VENDOR;
            m.EcsEntityHeld = true;
            m.TryScalarEntity(out e).Should().BeTrue();
            EcsScalars.Name(e).Should().Be("Moe the Brave");

            // nor one cached at an older epoch
            m.EcsEntity = vendor;
            m.EcsEntitySerial = MOB;
            m.EcsEntityEpoch = world.Ecs.Mirror.Epoch - 1;
            m.TryScalarEntity(out e).Should().BeTrue();
            EcsScalars.Name(e).Should().Be("Moe the Brave");

            // nor one on a destroyed instance whose record is still there
            m.EcsEntityEpoch = world.Ecs.Mirror.Epoch;
            m.EcsEntity = vendor;
            m.Destroy();
            if (world.Ecs.Mirror.TryGetEntity(MOB, out _))
            {
                m.TryScalarEntity(out _).Should().BeFalse();
            }
        }

        [Fact]
        public void The_Mirror_Pool_Writers_Ignore_A_Serial_Without_A_Record_And_Carry_Keeps_Them()
        {
            var mirror = new WorldMirror();
            mirror.SetMana(0x1234, 1, 1).Should().BeFalse();
            mirror.SetStamina(0x1234, 1, 1).Should().BeFalse();

            mirror.UpsertMobile(0x1234, 0x190, 0, 1, 1, 0, 0, 0, false);
            mirror.SetStamina(0x1234, 10, 9).Should().BeTrue();
            mirror.SetVitals(0x1234, 5, 6).Should().BeTrue();
            mirror.SetMana(0x1234, 20, 19).Should().BeTrue();
            mirror.TryGetExtras(0x1234, out MirrorExtras x).Should().BeTrue();
            (x.Hits, x.HitsMax, x.Stamina, x.StaminaMax, x.Mana, x.ManaMax).Should().Be(((ushort)5, (ushort)6, (ushort)9, (ushort)10, (ushort)19, (ushort)20));

            mirror.UpsertMobile(0x1235, 0x190, 0, 1, 1, 0, 0, 0, false);
            mirror.CarryMobileExtras(in x, 0x1235);
            mirror.TryGetExtras(0x1235, out MirrorExtras y).Should().BeTrue();
            y.Should().Be(x with { ChildStamp = y.ChildStamp, ItemLayer = y.ItemLayer });
        }
    }
}
