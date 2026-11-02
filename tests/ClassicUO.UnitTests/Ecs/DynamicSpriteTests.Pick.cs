using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T7b part 2b: picking from the extract's records (SpritePickTable). The pick state recorded from a select must be the
    // state the view's draw leaves on the GameObject (the side effects picking read before): the mobile's FrameInfo and
    // mirror bit (golden: the transcribed MobileView.Draw), the art entry's draw offset, a corpse's mirror bit. The table
    // serves only the last committed draw's objects; anything else falls back to the side effects. In the client,
    // CUO_PICK_RECORDS_CHECK compares every record-served pick with the side effect and pickscan's pick_records mode compares
    // the selection.
    public partial class DynamicSpriteTests
    {
        private static Mobile PickMobile(World world, uint serial = 0x100) => new Mobile(world, serial);

        private static void RecordMobileDraw(Mobile mobile, MobileSpriteExtract extract, EcsEntity e)
        {
            // what LegacySpriteSink.DrawMobile / GpuWorldRenderer.TryDrawMobileEcs do after the select
            bool mirror = (e.GetComponent<AnimationState>().Flags & AnimStateFlags.Mirror) != 0;
            SpritePickTable.RecordMobile(mobile, extract.FrameInfo, mirror);
            mobile.FrameInfo = extract.FrameInfo;
            mobile.ViewFlipped = mirror;
        }

        public static TheoryData<string, ushort> PickMobileCases => new TheoryData<string, ushort>
        {
            { "plain", 0xFFFF }, { "hued", 0xFFFF }, { "flipped", 0xFFFF }, { "mounted", 0xFFFF }, { "sitting", 0xFFFF },
            { "equipped", 0xFFFF }, { "translucent", 0xFFFF },
            // partially decoded: a layer whose atlas frame has no texture yet is not in FrameInfo (view and select)
            { "mounted", HORSE }, { "equipped", ROBE }, { "equipped", LANTERN }
        };

        [Theory]
        [MemberData(nameof(PickMobileCases))]
        public void Pick_Mobile_From_Records_Is_The_Views_FrameInfo_And_Mirror(string name, ushort pending)
        {
            SpritePickTable.Reset();
            (AnimationsLoader anims, _) = AnimFixture(5);
            var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>()) { PendingBody = pending };
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);
            Mobile mobile = PickMobile(new World());

            SpritePickTable.BeginDraw();
            extract.BeginSelect(atlas);
            extract.Select(e, 600, 400, 812.25f);
            RecordMobileDraw(mobile, extract, e);
            SpritePickTable.Commit();

            var side = new ViewSide();
            ViewMobile(atlas, e, 600, 400, 812.25f, extract.Selected.ToArray(), side);

            Assert.True(SpritePickTable.TryMobile(mobile, out Rectangle frame, out bool flipped), name);
            Assert.Equal(side.FrameInfo, frame);
            Assert.Equal(mobile.FrameInfo, frame);
            Assert.Equal(mobile.ViewFlipped, flipped);
            Assert.Equal(name == "flipped", flipped);
            Assert.NotEqual(Rectangle.Empty, frame);
        }

        [Fact]
        public void Pick_Mobile_Whose_Every_Layer_Is_Decoding_Records_An_Empty_Frame()
        {
            // the view leaves FrameInfo empty (nothing to pick); so do the records
            SpritePickTable.Reset();
            (AnimationsLoader anims, _) = AnimFixture(5);
            var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>()) { PendingBody = HUMAN };
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, "plain");
            var extract = new MobileSpriteExtract(anims);
            Mobile mobile = PickMobile(new World());

            SpritePickTable.BeginDraw();
            extract.BeginSelect(atlas);
            extract.Select(e, 600, 400, 812.25f);
            RecordMobileDraw(mobile, extract, e);
            SpritePickTable.Commit();

            Assert.True(SpritePickTable.TryMobile(mobile, out Rectangle frame, out _));
            Assert.Equal(Rectangle.Empty, frame);
        }

        public static TheoryData<string> PickItemCases => new TheoryData<string> { "plain", "hued", "stack", "coins", "multi", "light", "fading", "preview" };

        [Theory]
        [MemberData(nameof(PickItemCases))]
        public void Pick_Item_From_Records_Is_The_Art_Entry_Offset(string name)
        {
            SpritePickTable.Reset();
            (ArtLoader arts, _) = ArtFixture();
            var extract = new ItemSpriteExtract(arts, null);
            ItemSpriteInput input = ItemCase(name);
            Item item = Item.Create(new World(), 0x40000100);

            SpritePickTable.BeginDraw();
            Assert.True(extract.Select(in input, false, 700, 300, 455.5f));
            SpritePickTable.RecordItem(item, extract);
            SpritePickTable.Commit();

            ushort graphic = (ushort)extract.Selected[0].Id;
            ref ArtEntry entry = ref arts.GetEntry(graphic + 0x4000);

            Assert.True(extract.HasArtPick);
            Assert.Equal(graphic, extract.ArtPickGraphic);
            Assert.True(SpritePickTable.TryItemArt(item, graphic, in entry, out int w, out int h), name);
            Assert.Equal(entry.Width, w);
            Assert.Equal(entry.Height, h);

            // a graphic the item did not draw last (its animation moved on): the art entry's
            Assert.False(SpritePickTable.TryItemArt(item, (ushort)(graphic + 1), in entry, out _, out _));
            // the record is an item's, not a mobile's or a corpse's
            Assert.False(SpritePickTable.TryMobile(item, out _, out _));
            Assert.False(SpritePickTable.TryCorpse(item, out _));
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("missing-stack")]
        public void Pick_Item_Of_Missing_Art_Reads_The_Art_Entry(string name)
        {
            // the view's art entry write for missing art is discarded (a scratch entry), so the select records nothing and
            // picking keeps reading the entry as before
            SpritePickTable.Reset();
            (ArtLoader arts, _) = ArtFixture();
            var extract = new ItemSpriteExtract(arts, null);
            ItemSpriteInput input = ItemCase(name);
            Item item = Item.Create(new World(), 0x40000100);

            SpritePickTable.BeginDraw();
            extract.Select(in input, false, 700, 300, 455.5f);
            SpritePickTable.RecordItem(item, extract);
            SpritePickTable.Commit();

            Assert.False(extract.HasArtPick);
            Assert.False(SpritePickTable.TryItemArt(item, MISSING_ART, in arts.GetEntry(MISSING_ART + 0x4000), out _, out _));
        }

        [Fact]
        public void Pick_Hidden_Or_Undrawn_Item_Is_Not_Recorded()
        {
            SpritePickTable.Reset();
            (ArtLoader arts, _) = ArtFixture();
            var extract = new ItemSpriteExtract(arts, null);
            ItemSpriteInput input = ItemCase("plain");
            input.AllowedToDraw = false;
            Item item = Item.Create(new World(), 0x40000100);

            SpritePickTable.BeginDraw();
            Assert.False(extract.Select(in input, false, 700, 300, 455.5f));
            SpritePickTable.RecordItem(item, extract);
            SpritePickTable.Commit();

            Assert.False(SpritePickTable.TryItemArt(item, 0x0E75, in arts.GetEntry(0x0E75 + 0x4000), out _, out _));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Pick_Corpse_From_Records_Is_Its_Mirror_Bit(bool mirror)
        {
            SpritePickTable.Reset();
            Item corpse = Item.Create(new World(), 0x40000200);

            SpritePickTable.BeginDraw();
            SpritePickTable.RecordItem(corpse, true, mirror, false, 0, 0, 0);
            SpritePickTable.Commit();

            Assert.True(SpritePickTable.TryCorpse(corpse, out bool flipped));
            Assert.Equal(mirror, flipped);
            Assert.False(SpritePickTable.TryItemArt(corpse, 0x2006, default(ArtEntry), out _, out _));
        }

        // ---- the table's lifetime ----

        [Fact]
        public void Pick_Records_Serve_Only_The_Last_Committed_Draw()
        {
            SpritePickTable.Reset();
            var world = new World();
            Mobile a = PickMobile(world, 0x101), b = PickMobile(world, 0x102);
            var ra = new Rectangle(10, 20, 30, 40);

            // nothing committed yet
            Assert.False(SpritePickTable.TryMobile(a, out _, out _));

            SpritePickTable.BeginDraw();
            SpritePickTable.RecordMobile(a, ra, true);
            // the frame's fill reads the last committed draw, not the one being written
            Assert.False(SpritePickTable.TryMobile(a, out _, out _));
            SpritePickTable.Commit();

            Assert.True(SpritePickTable.TryMobile(a, out Rectangle got, out bool flip));
            Assert.Equal(ra, got);
            Assert.True(flip);
            Assert.False(SpritePickTable.TryMobile(b, out _, out _));

            // the next draw doesn't draw a: it falls back
            SpritePickTable.BeginDraw();
            SpritePickTable.RecordMobile(b, ra, false);
            Assert.True(SpritePickTable.TryMobile(a, out _, out _));
            SpritePickTable.Commit();

            Assert.False(SpritePickTable.TryMobile(a, out _, out _));
            Assert.True(SpritePickTable.TryMobile(b, out _, out flip));
            Assert.False(flip);
        }

        [Fact]
        public void Pick_Records_Of_An_Uncommitted_Draw_Are_Dropped()
        {
            // the Gpu item check's probe records nothing; a draw started again without a commit starts empty
            SpritePickTable.Reset();
            Mobile a = PickMobile(new World());

            SpritePickTable.BeginDraw();
            SpritePickTable.RecordMobile(a, new Rectangle(1, 2, 3, 4), false);
            SpritePickTable.BeginDraw();
            SpritePickTable.Commit();

            Assert.False(SpritePickTable.TryMobile(a, out _, out _));

            // outside a draw nothing is recorded
            SpritePickTable.RecordMobile(a, new Rectangle(1, 2, 3, 4), false);
            Assert.False(SpritePickTable.TryMobile(a, out _, out _));
        }

        [Fact]
        public void Pick_Records_An_Object_Drawn_Twice_Keeps_The_Last()
        {
            SpritePickTable.Reset();
            Mobile a = PickMobile(new World());

            SpritePickTable.BeginDraw();
            SpritePickTable.RecordMobile(a, new Rectangle(1, 2, 3, 4), false);
            SpritePickTable.RecordMobile(a, new Rectangle(5, 6, 7, 8), true);
            SpritePickTable.Commit();

            Assert.True(SpritePickTable.TryMobile(a, out Rectangle got, out bool flip));
            Assert.Equal(new Rectangle(5, 6, 7, 8), got);
            Assert.True(flip);
        }

        [Fact]
        public void Pick_Records_Of_A_Reused_Object_Are_Not_Served()
        {
            SpritePickTable.Reset();
            Item item = Item.Create(new World(), 0x40000100);

            SpritePickTable.BeginDraw();
            SpritePickTable.RecordItem(item, false, false, true, 0x0E75, 0, 16);
            SpritePickTable.Commit();
            Assert.True(SpritePickTable.TryItemArt(item, 0x0E75, default(ArtEntry), out _, out _));

            // MapObjectRecycler reuse (Incarnation bumped, stamp reset)
            item.ResetForReuse();
            Assert.False(SpritePickTable.TryItemArt(item, 0x0E75, default(ArtEntry), out _, out _));
        }

        [Fact]
        public void Pick_Records_Grow_Past_The_Initial_Buffer_And_The_Override_Turns_Them_Off()
        {
            SpritePickTable.Reset();
            var world = new World();
            var mobiles = new List<Mobile>();

            for (int round = 0; round < 3; round++)
            {
                SpritePickTable.BeginDraw();

                for (int i = 0; i < 700; i++)
                {
                    if (round == 0)
                    {
                        mobiles.Add(PickMobile(world, (uint)(0x1000 + i)));
                    }

                    SpritePickTable.RecordMobile(mobiles[i], new Rectangle(i, round, 3, 4), (i & 1) != 0);
                }

                SpritePickTable.Commit();

                for (int i = 0; i < mobiles.Count; i++)
                {
                    Assert.True(SpritePickTable.TryMobile(mobiles[i], out Rectangle got, out bool flip));
                    Assert.Equal(new Rectangle(i, round, 3, 4), got);
                    Assert.Equal((i & 1) != 0, flip);
                }
            }

            SpritePickTable.ReadOverride = false;
            Assert.False(SpritePickTable.TryMobile(mobiles[0], out _, out _));
            SpritePickTable.ReadOverride = true;
            Assert.True(SpritePickTable.TryMobile(mobiles[0], out _, out _));
            SpritePickTable.Reset();
        }
    }
}
