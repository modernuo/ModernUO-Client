using System;
using System.IO;
using ClassicUO.Configuration;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // ui_border_bake (G11a): the switch, and where the B8 results say the UI target changed. The composite itself needs a
    // device; CUO_UI_COMPOSITE_CHECK compares it pixel for pixel in a running client.
    public class UiCompositeSwitchTests
    {
        private static RenderGeometry Geometry() => RenderGeometry.Compute(0, 0, 1920, 1080, 1f, new Rectangle(20, 30, 1200, 800));

        [Fact]
        public void Default_Is_Off()
        {
            new Settings().UiBorderBake.Should().BeFalse();
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void Key_Loads_In_Both_States(string value, bool expected)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-uiborderbake-{Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllText(path, $"{{\n  \"username\": \"tester\",\n  \"ui_border_bake\": {value}\n}}\n");

                ConfigurationResolver.Load(path, SettingsJsonContext.RealDefault.Settings).UiBorderBake.Should().Be(expected);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Override_Decides_The_Switch(bool on)
        {
            UiComposite.OverrideForTests = on;

            try
            {
                UiComposite.Enabled.Should().Be(on);
            }
            finally
            {
                UiComposite.OverrideForTests = null;
            }
        }

        // The tracking runs in both switch states (a switch turned on mid-session bakes only what changed since), so the
        // outcomes below hold in both.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void KeptUi_BakesNothing(bool on)
        {
            WithSwitch(on, () =>
            {
                (UiBorderBake bake, RecordedPass pass, RenderGeometry g) = Baked(new Rectangle(100, 100, 32, 32));
                Rectangle? cursor = new Rectangle(100, 100, 32, 32);

                UiComposite.NoteUiChange(bake, g, RecordingResult.Same, RecordingResult.Same, pass, ref cursor);

                bake.Next(g, 1, this, this, out _).Should().Be(BorderBakeKind.None);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void CursorOnlyChange_BakesTheOldAndNewCursorRects(bool on)
        {
            WithSwitch(on, () =>
            {
                (UiBorderBake bake, RecordedPass pass, RenderGeometry g) = Baked(new Rectangle(300, 200, 32, 32));
                Rectangle? cursor = new Rectangle(100, 100, 32, 32);

                UiComposite.NoteUiChange(bake, g, RecordingResult.Same, RecordingResult.Changed, pass, ref cursor);

                bake.Next(g, 1, this, this, out Rectangle r).Should().Be(BorderBakeKind.Partial);
                r.Contains(new Rectangle(100, 100, 32, 32)).Should().BeTrue();
                r.Contains(new Rectangle(300, 200, 32, 32)).Should().BeTrue();
                cursor.Should().Be(new Rectangle(300, 200, 32, 32));
            });
        }

        [Theory]
        [InlineData(RecordingResult.Changed, RecordingResult.Changed)]
        [InlineData(RecordingResult.Aborted, RecordingResult.Changed)]
        [InlineData(RecordingResult.Same, RecordingResult.Aborted)]
        public void GumpChange_OrAnAbortedPass_BakesEverything(RecordingResult gumps, RecordingResult final)
        {
            (UiBorderBake bake, RecordedPass pass, RenderGeometry g) = Baked(new Rectangle(300, 200, 32, 32));
            Rectangle? cursor = new Rectangle(100, 100, 32, 32);

            UiComposite.NoteUiChange(bake, g, gumps, final, pass, ref cursor);

            bake.Next(g, 1, this, this, out _).Should().Be(BorderBakeKind.Full);
        }

        [Fact]
        public void CursorChange_WithoutTheLastCursorRect_BakesEverything()
        {
            (UiBorderBake bake, RecordedPass pass, RenderGeometry g) = Baked(new Rectangle(300, 200, 32, 32));
            Rectangle? cursor = null;

            UiComposite.NoteUiChange(bake, g, RecordingResult.Same, RecordingResult.Changed, pass, ref cursor);

            bake.Next(g, 1, this, this, out _).Should().Be(BorderBakeKind.Full);
            cursor.Should().Be(new Rectangle(300, 200, 32, 32));
        }

        private static void WithSwitch(bool on, Action body)
        {
            UiComposite.OverrideForTests = on;

            try
            {
                UiComposite.Enabled.Should().Be(on);
                body();
            }
            finally
            {
                UiComposite.OverrideForTests = null;
            }
        }

        // a border baked once, and a final pass: segment 0 the cache copy, segment 1 the cursor at cursorNow
        private (UiBorderBake, RecordedPass, RenderGeometry) Baked(Rectangle cursorNow)
        {
            RenderGeometry g = Geometry();
            var bake = new UiBorderBake();
            bake.Next(g, 1, this, this, out _);

            var pass = new RecordedPass();
            Quad(pass, 0, new Rectangle(0, 0, 1920, 1080));
            Quad(pass, 1, cursorNow);
            pass.Segments.Add(Segment(0));
            pass.Segments.Add(Segment(1));
            pass.SpriteCount = 2;
            pass.Valid = true;

            return (bake, pass, g);
        }

        private static RecordedSegment Segment(int start) => new()
        {
            Start = start,
            Count = 1,
            Transform = Matrix.Identity,
            Viewport = new Microsoft.Xna.Framework.Graphics.Viewport(0, 0, 1920, 1080),
            Brightlight = float.NaN,
            CircleRadius = float.NaN
        };

        private static void Quad(RecordedPass pass, int i, Rectangle r)
        {
            pass.Vertices[i].Position0 = new Vector3(r.Left, r.Top, 0);
            pass.Vertices[i].Position1 = new Vector3(r.Right, r.Top, 0);
            pass.Vertices[i].Position2 = new Vector3(r.Left, r.Bottom, 0);
            pass.Vertices[i].Position3 = new Vector3(r.Right, r.Bottom, 0);
        }
    }
}
