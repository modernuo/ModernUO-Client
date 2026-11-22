using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // G0 (gump system): the snapshot helper sees every queued command, in queue order, with its values.
    public sealed class GumpCommandSnapshotTests
    {
        private sealed class Painter : Control, IGumpDeferredDraw
        {
            public RenderedText Text;
            public ushort Hue;

            public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
            {
                renderLists.AddGumpWithAtlas(GumpCommand.Outline(null, x, y, Width, Height, ShaderHueTranslator.GetHueVector(Hue), layerDepthRef));
                renderLists.AddGumpNoAtlas(GumpCommand.Deferred(this, x, y, layerDepthRef, 7));
                renderLists.AddGumpNoAtlas(Text, x + 1, y + 2, layerDepthRef, Alpha);

                return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
            }

            public bool DrawDeferred(UltimaBatcher2D batcher, int x, int y, float layerDepth, int arg) => true;
        }

        private static RenderedText Text(string s)
        {
            var text = (RenderedText)RuntimeHelpers.GetUninitializedObject(typeof(RenderedText));
            typeof(RenderedText).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(text, s);

            return text;
        }

        private static Gump Build(int childX, string text, ushort hue = 0)
        {
            var root = new Gump(null, 0, 0) { X = 100, Y = 50 };
            root.Add(new Painter { X = childX, Y = 5, Width = 30, Height = 20, Text = Text(text), Hue = hue });

            return root;
        }

        [Fact]
        public void Lists_Every_Command_With_Absolute_Positions()
        {
            var lines = GumpCommandSnapshot.Take(Build(10, "hello"));

            lines.Should().HaveCount(3);
            lines[0].Should().StartWith("atlas Outline at 110,55 30x20");
            lines[1].Should().StartWith("text Deferred at 110,55").And.Contain("arg 7").And.EndWith("Painter d0");
            lines[2].Should().StartWith("text Text at 111,57").And.Contain("'hello'");
        }

        [Fact]
        public void Equal_Trees_Give_Equal_Snapshots_And_Any_Drawn_Change_Shows()
        {
            var a = GumpCommandSnapshot.Take(Build(10, "hello"));

            GumpCommandSnapshot.Take(Build(10, "hello")).Should().Equal(a, "names come from first appearance, not identity");
            GumpCommandSnapshot.Take(Build(11, "hello")).Should().NotEqual(a);
            GumpCommandSnapshot.Take(Build(10, "hullo")).Should().NotEqual(a);
            GumpCommandSnapshot.Take(Build(10, "hello", 0x21)).Should().NotEqual(a);
        }
    }
}
