using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 S11: the class-A members of the world objects are record-only (a get-only property that reads the mirror record, no
    // backing field) except the funnel-written caches below. A record-only member cannot be written past WorldWriter: there
    // is no setter to call. These tests pin that split; WriteSiteBudgetTests counts the writes of the members that still
    // have one (its AFields must name exactly the cache members).
    public class ClassAMemberTests
    {
        private static readonly Type[] WorldTypes = { typeof(GameObject), typeof(Entity), typeof(Item), typeof(Mobile), typeof(PlayerMobile) };

        // the record-only class-A members (ecs-state-census.md, section 2; T4, T9 cold-caches, S5-S9)
        internal static readonly string[] RecordOnly =
        {
            "Graphic", "Hue", "Flags", "Direction", "Amount", "Container", "Layer", "Name", "Hits", "HitsMax", "Stamina",
            "StaminaMax", "Mana", "ManaMax", "Price", "LightID", "IsDamageable", "UsedLayer", "DisplayedGraphic", "Race",
            "SpeedMode", "IsFemale", "IsRenamable", "InWarMode", "Offset", "OffsetX", "OffsetY", "OffsetZ",
            "AnimationFrameCount", "AnimationFromServer"
        };

        // the class-A members that keep a setter: the position cache (owner default, S10), the serial (the object's key),
        // the Mobile's notoriety and pushed animation inputs, the Mobile's step-sound state, an item's corpse/boat animation
        // fields and IsMulti. Only WorldWriter's operations write them (FunnelCheck, WriteSiteBudgetTests).
        internal static readonly string[] Settable =
        {
            "X", "Y", "Z", "Serial", "IsMulti", "IsRunning", "NotorietyFlag", "IsDead", "AnimIndex", "ExecuteAnimation",
            "LastAnimationChangeTime", "LastStepTime", "LastStepSoundTime", "StepSoundOffset"
        };

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static IEnumerable<(Type Type, PropertyInfo Property)> Properties() =>
            WorldTypes.SelectMany(t => t.GetProperties(Instance).Select(p => (t, p)));

        [Fact]
        public void Record_Only_Members_Have_No_Setter()
        {
            var settable = Properties()
                .Where(x => RecordOnly.Contains(x.Property.Name) && x.Property.SetMethod != null)
                .Select(x => $"{x.Type.Name}.{x.Property.Name}")
                .ToList();

            settable.Should().BeEmpty("a record-only member is written by WorldWriter's commits only, never through a setter");
        }

        [Fact]
        public void Record_Only_Members_Have_No_Backing_Field()
        {
            var names = new HashSet<string>(RecordOnly.SelectMany(n => new[] { n, "_" + char.ToLowerInvariant(n[0]) + n.Substring(1) }), StringComparer.Ordinal);
            names.Add("_lightID");

            // PlayerMobile's GetTotalAmount local `_amount` is a local, not a field: only instance fields are looked at
            var fields = WorldTypes
                .SelectMany(t => t.GetFields(Instance).Select(f => (t, f)))
                .Where(x => names.Contains(x.f.Name))
                .Select(x => $"{x.t.Name}.{x.f.Name}")
                .ToList();

            fields.Should().BeEmpty("the record is the only copy of a record-only member");
        }

        [Fact]
        public void Only_The_Cache_Members_Have_A_Setter()
        {
            var classA = new HashSet<string>(RecordOnly.Concat(Settable), StringComparer.Ordinal);
            var withSetter = Properties()
                .Where(x => classA.Contains(x.Property.Name) && x.Property.SetMethod != null)
                .Select(x => x.Property.Name)
                .Distinct()
                .ToList();

            withSetter.Should().BeEquivalentTo(Settable, "a cache member that lost its setter is record-only: move it to RecordOnly and out of WriteSiteScanner.AFields");
        }

        [Fact]
        public void The_Write_Budget_Scans_Exactly_The_Cache_Members()
        {
            WriteSiteScanner.AFields.Where(f => !f.StartsWith("_", StringComparison.Ordinal))
                .Should().BeEquivalentTo(Settable);
            WriteSiteScanner.AFields.Should().NotContain(RecordOnly);
        }
    }
}
