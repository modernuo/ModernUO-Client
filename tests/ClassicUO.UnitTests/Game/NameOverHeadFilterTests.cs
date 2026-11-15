using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.UnitTests.Ecs;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>
    /// perf-15: <see cref="NameOverHeadManager.IsAllowed(Entity, bool)"/> takes the player's race from its caller (read once
    /// per render-list pass) instead of reading it per item. The filter itself is unchanged: corpses and mobiles by their
    /// type flags, other items by the Items flag and whether their displayed graphic is drawn for that race.
    /// </summary>
    public sealed class NameOverHeadFilterTests
    {
        private const ushort PLAIN = 0x1000;
        private const ushort NO_DRAW = 0x1001;
        private const ushort GARGOYLE_ONLY = 0x1002;
        private const ushort CORPSE = 0x2006;
        private const ushort GOLD = 0x0EED;

        private static bool WithStatics(System.Func<bool> test)
        {
            var data = new StaticTiles[0x10000];
            data[NO_DRAW].PackFlags = TilePackFlags.NoDraw;
            data[GARGOYLE_ONLY].PackFlags = TilePackFlags.NoDrawUnlessGargoyle;
            data[CORPSE].PackFlags = TilePackFlags.NoDraw;
            data[GOLD + 2].PackFlags = TilePackFlags.NoDraw;
            var saved = GameObject.TestStaticData;
            GameObject.TestStaticData = data;

            try
            {
                return test();
            }
            finally
            {
                GameObject.TestStaticData = saved;
            }
        }

        private static Item NewItem(ushort graphic, ushort amount = 1)
        {
            Item item = Item.Create(new World(), 0x4000_0100);
            item.TestSetGraphic(graphic);
            item.TestSetAmount(amount);

            return item;
        }

        private static bool Allowed(Entity e, NameOverheadTypeAllowed allowed, bool gargoyle) =>
            WithStatics(() => NameOverHeadManager.IsAllowed(e, allowed, gargoyle));

        [Fact]
        public void Null_Is_Not_Allowed()
        {
            Allowed(null, NameOverheadTypeAllowed.All, false).Should().BeFalse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Drawn_Item_Follows_The_Items_Flag(bool gargoyle)
        {
            Item item = NewItem(PLAIN);
            Allowed(item, NameOverheadTypeAllowed.Items, gargoyle).Should().BeTrue();
            Allowed(item, NameOverheadTypeAllowed.AllMobiles | NameOverheadTypeAllowed.Corpses, gargoyle).Should().BeFalse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Hidden_Item_Is_Never_Allowed(bool gargoyle)
        {
            Allowed(NewItem(NO_DRAW), NameOverheadTypeAllowed.All, gargoyle).Should().BeFalse();
        }

        [Fact]
        public void A_Gargoyle_Only_Item_Follows_The_Race_Passed_In()
        {
            Item item = NewItem(GARGOYLE_ONLY);
            Allowed(item, NameOverheadTypeAllowed.All, false).Should().BeFalse();
            Allowed(item, NameOverheadTypeAllowed.All, true).Should().BeTrue();
        }

        [Fact]
        public void The_Displayed_Graphic_Is_Tested_Not_The_Graphic()
        {
            Allowed(NewItem(GOLD, 3), NameOverheadTypeAllowed.Items, false).Should().BeTrue();
            Allowed(NewItem(GOLD, 10), NameOverheadTypeAllowed.Items, false).Should().BeFalse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Corpse_Follows_The_Corpses_Flag_Even_If_Its_Graphic_Is_Hidden(bool gargoyle)
        {
            Item corpse = NewItem(CORPSE);
            Allowed(corpse, NameOverheadTypeAllowed.Corpses, gargoyle).Should().BeTrue();
            Allowed(corpse, NameOverheadTypeAllowed.Items, gargoyle).Should().BeFalse();
        }

        [Theory]
        [InlineData((byte)NotorietyFlag.Innocent, (int)NameOverheadTypeAllowed.Innocent)]
        [InlineData((byte)NotorietyFlag.Ally, (int)NameOverheadTypeAllowed.Ally)]
        [InlineData((byte)NotorietyFlag.Gray, (int)NameOverheadTypeAllowed.Gray)]
        [InlineData((byte)NotorietyFlag.Criminal, (int)NameOverheadTypeAllowed.Criminal)]
        [InlineData((byte)NotorietyFlag.Enemy, (int)NameOverheadTypeAllowed.Enemy)]
        [InlineData((byte)NotorietyFlag.Murderer, (int)NameOverheadTypeAllowed.Murderer)]
        [InlineData((byte)NotorietyFlag.Invulnerable, (int)NameOverheadTypeAllowed.Invulnerable)]
        public void A_Mobile_Follows_Its_Notoriety_Flag(byte notoriety, int flagBits)
        {
            var flag = (NameOverheadTypeAllowed)flagBits;
            var mobile = new Mobile(new World(), 0x100) { NotorietyFlag = (NotorietyFlag)notoriety };

            foreach (bool gargoyle in new[] { false, true })
            {
                Allowed(mobile, flag, gargoyle).Should().BeTrue();
                Allowed(mobile, NameOverheadTypeAllowed.All & ~flag, gargoyle).Should().BeFalse();
            }
        }

        [Fact]
        public void A_Mobile_Of_Unknown_Notoriety_Is_Always_Allowed()
        {
            var mobile = new Mobile(new World(), 0x100) { NotorietyFlag = NotorietyFlag.Unknown };
            Allowed(mobile, NameOverheadTypeAllowed.None, false).Should().BeTrue();
        }
    }
}
