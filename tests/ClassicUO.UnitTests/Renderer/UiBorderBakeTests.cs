using System;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // ui_border_bake (G11a): which part of the baked border is stale, and the rects the bake and the viewport UI blend use.
    public class UiBorderBakeTests
    {
        private static readonly object Target = new(), OtherTarget = new(), Background = new();

        private static RenderGeometry Geometry(int w = 1920, int h = 1080, float scale = 1f, int vx = 20, int vy = 30, int vw = 1200, int vh = 800)
            => RenderGeometry.Compute(0, 0, w, h, scale, new Rectangle(vx, vy, vw, vh));

        [Fact]
        public void FirstBake_IsFull_ThenNothing_UntilSomethingChanges()
        {
            var bake = new UiBorderBake();
            RenderGeometry g = Geometry();

            bake.Next(g, 1, Target, Background, out Rectangle r).Should().Be(BorderBakeKind.Full);
            r.Should().Be(new Rectangle(0, 0, 1920, 1080));
            bake.Next(g, 1, Target, Background, out _).Should().Be(BorderBakeKind.None);
            bake.FullBakes.Should().Be(1);
            bake.PartialBakes.Should().Be(0);
        }

        [Fact]
        public void DirtyUi_BakesOnlyThatRegion_Once()
        {
            var bake = new UiBorderBake();
            RenderGeometry g = Geometry();
            bake.Next(g, 1, Target, Background, out _);

            bake.AddDirtyUi(new Rectangle(100, 100, 32, 32), g);
            bake.AddDirtyUi(new Rectangle(140, 100, 32, 32), g);

            bake.Next(g, 1, Target, Background, out Rectangle r).Should().Be(BorderBakeKind.Partial);
            r.Contains(new Rectangle(100, 100, 72, 32)).Should().BeTrue();
            r.Width.Should().BeLessThan(80);
            bake.Next(g, 1, Target, Background, out _).Should().Be(BorderBakeKind.None);
        }

        [Fact]
        public void DirtyUi_BeforeTheFirstBake_IsIgnored_TheFirstBakeIsFull()
        {
            var bake = new UiBorderBake();
            RenderGeometry g = Geometry();
            bake.AddDirtyUi(new Rectangle(1, 1, 5, 5), g);

            bake.Next(g, 1, Target, Background, out _).Should().Be(BorderBakeKind.Full);
        }

        [Fact]
        public void InvalidateAll_GeometryVersionTargetOrBackground_ForceAFullBake()
        {
            var bake = new UiBorderBake();
            RenderGeometry g = Geometry();
            bake.Next(g, 1, Target, Background, out _);

            bake.InvalidateAll();
            bake.Next(g, 1, Target, Background, out _).Should().Be(BorderBakeKind.Full);

            RenderGeometry moved = Geometry(vx: 21);
            bake.Next(moved, 1, Target, Background, out _).Should().Be(BorderBakeKind.Full);
            bake.Next(moved, 2, Target, Background, out _).Should().Be(BorderBakeKind.Full);
            bake.Next(moved, 2, OtherTarget, Background, out _).Should().Be(BorderBakeKind.Full);
            bake.Next(moved, 2, OtherTarget, new object(), out _).Should().Be(BorderBakeKind.Full);
            bake.Next(Geometry(1280, 720), 2, OtherTarget, Background, out Rectangle r).Should().Be(BorderBakeKind.Full);
            r.Should().Be(new Rectangle(0, 0, 1280, 720));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        [InlineData(1.5f)]
        [InlineData(2f)]
        public void UiToBackBuffer_CoversTheScaledRect_AndItsFilterNeighbours(float scale)
        {
            RenderGeometry g = Geometry(1917, 1079, scale);
            var ui = new Rectangle(37, 41, 20, 13);

            Rectangle bb = UiBorderBake.UiToBackBuffer(ui, g);

            // the UI pixels left of, right of, above and below the rect, mapped to the back buffer, lie inside
            bb.Left.Should().BeLessThanOrEqualTo((int)Math.Floor((ui.Left - 1) * g.ScaleX));
            bb.Top.Should().BeLessThanOrEqualTo((int)Math.Floor((ui.Top - 1) * g.ScaleY));
            bb.Right.Should().BeGreaterThanOrEqualTo((int)Math.Ceiling((ui.Right + 1) * g.ScaleX));
            bb.Bottom.Should().BeGreaterThanOrEqualTo((int)Math.Ceiling((ui.Bottom + 1) * g.ScaleY));
        }

        [Fact]
        public void UiToBackBuffer_IsClippedToTheBackBuffer()
        {
            RenderGeometry g = Geometry(800, 600);

            UiBorderBake.UiToBackBuffer(new Rectangle(-50, -50, 60, 60), g).Should().Be(new Rectangle(0, 0, 12, 12));
            UiBorderBake.UiToBackBuffer(new Rectangle(900, 10, 5, 5), g).Should().Be(Rectangle.Empty);
        }

        [Theory]
        [InlineData(20, 30, 1200, 800)]
        [InlineData(0, 0, 1920, 1080)]
        [InlineData(0, 0, 600, 480)]
        [InlineData(1500, 900, 600, 400)]
        [InlineData(-5, -5, 700, 500)]
        [InlineData(3000, 10, 100, 100)]
        public void BorderRects_CoverTheWindowOutsideTheViewport_ExactlyOnce(int vx, int vy, int vw, int vh)
        {
            var bounds = new Rectangle(0, 0, 1920, 1080);
            var world = new Rectangle(vx, vy, vw, vh);
            Span<Rectangle> rects = stackalloc Rectangle[4];
            int n = UiBorderBake.BorderRects(bounds, world, rects);

            int[] cover = new int[bounds.Width * bounds.Height];

            for (int i = 0; i < n; i++)
            {
                for (int y = rects[i].Top; y < rects[i].Bottom; y++)
                {
                    for (int x = rects[i].Left; x < rects[i].Right; x++)
                    {
                        cover[y * bounds.Width + x]++;
                    }
                }
            }

            for (int y = 0; y < bounds.Height; y += 7)
            {
                for (int x = 0; x < bounds.Width; x += 7)
                {
                    cover[y * bounds.Width + x].Should().Be(world.Contains(x, y) ? 0 : 1, $"pixel {x},{y}");
                }
            }

            // the viewport's edges, sampled densely
            for (int x = Math.Max(0, vx - 1); x <= Math.Min(bounds.Width - 1, vx + vw); x++)
            {
                foreach (int y in new[] { vy - 1, vy, vy + vh - 1, vy + vh })
                {
                    if (y >= 0 && y < bounds.Height)
                    {
                        cover[y * bounds.Width + x].Should().Be(world.Contains(x, y) ? 0 : 1, $"edge pixel {x},{y}");
                    }
                }
            }
        }

        [Fact]
        public void TryGetBounds_CoversTheSegmentsFromTheGivenOne()
        {
            var pass = new RecordedPass();
            Quad(pass, 0, 0, 0, 1280, 720);
            Quad(pass, 1, 100.5f, 200.25f, 32, 32);
            Quad(pass, 2, 140, 190, 10, 10);
            pass.Segments.Add(Segment(0, 1));
            pass.Segments.Add(Segment(1, 2));
            pass.SpriteCount = 3;
            pass.Valid = true;

            pass.TryGetBounds(1, out Rectangle b).Should().BeTrue();
            b.Should().Be(new Rectangle(100, 190, 50, 43));

            pass.TryGetBounds(2, out Rectangle none).Should().BeTrue();
            none.Width.Should().Be(0);
        }

        [Fact]
        public void TryGetBounds_RefusesTransformedSegments_AndInvalidPasses()
        {
            var pass = new RecordedPass();
            Quad(pass, 0, 10, 10, 5, 5);
            RecordedSegment seg = Segment(0, 1);
            seg.Transform = Matrix.CreateScale(2f);
            pass.Segments.Add(seg);
            pass.SpriteCount = 1;

            pass.TryGetBounds(0, out _).Should().BeFalse(); // not valid yet
            pass.Valid = true;
            pass.TryGetBounds(0, out _).Should().BeFalse(); // transformed
        }

        private static RecordedSegment Segment(int start, int count) => new()
        {
            Start = start,
            Count = count,
            Transform = Matrix.Identity,
            Viewport = new Microsoft.Xna.Framework.Graphics.Viewport(0, 0, 1280, 720),
            Brightlight = float.NaN,
            CircleRadius = float.NaN
        };

        private static void Quad(RecordedPass pass, int i, float x, float y, float w, float h)
        {
            pass.Vertices[i].Position0 = new Vector3(x, y, 0);
            pass.Vertices[i].Position1 = new Vector3(x + w, y, 0);
            pass.Vertices[i].Position2 = new Vector3(x, y + h, 0);
            pass.Vertices[i].Position3 = new Vector3(x + w, y + h, 0);
        }
    }
}
