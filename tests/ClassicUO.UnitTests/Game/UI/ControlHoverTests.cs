using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // Hover runs every frame: InvokeMouseOver/Enter/Exit allocate their MouseEventArgs only when something listens, and
    // UIManager.SortControlsByInfo moves an Over gump by relinking its node. Listeners get the same arguments as before.
    [Collection(nameof(FunnelCheckCollection))] // UIManager.Gumps is process-wide
    public sealed class ControlHoverTests
    {
        private static Gump G(World world) => new Gump(world, 0, 0);

        [Fact]
        public void Listeners_Get_The_Position_Relative_To_The_Control()
        {
            var world = new World();
            Gump g = G(world);
            g.X = 10;
            g.Y = 20;
            var seen = new List<(string, int, int)>();
            g.MouseOver += (s, e) => seen.Add(("over", e.X, e.Y));
            g.MouseEnter += (s, e) => seen.Add(("enter", e.X, e.Y));
            g.MouseExit += (s, e) => seen.Add(("exit", e.X, e.Y));

            g.InvokeMouseOver(new Point(15, 27));
            g.InvokeMouseEnter(new Point(11, 21));
            g.InvokeMouseExit(new Point(0, 0));

            seen.Should().Equal(("over", 5, 7), ("enter", 1, 1), ("exit", -10, -20));
        }

        [Fact]
        public void Without_Listeners_Hover_Allocates_Nothing()
        {
            var world = new World();
            Gump g = G(world);
            var p = new Point(3, 4);

            for (int i = 0; i < 100; i++)
            {
                g.InvokeMouseOver(p);
                g.InvokeMouseEnter(p);
                g.InvokeMouseExit(p);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
            {
                g.InvokeMouseOver(p);
                g.InvokeMouseEnter(p);
                g.InvokeMouseExit(p);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        }

        [Fact]
        public void An_Over_Gump_Moves_To_The_Front_Reusing_Its_Node()
        {
            var world = new World();
            Gump a = G(world), over = G(world), b = G(world);
            over.LayerOrder = UILayer.Over;
            var saved = UIManager.Gumps.ToList();
            UIManager.Gumps.Clear();

            try
            {
                UIManager.Add(a, false);
                UIManager.Add(b, false);
                UIManager.Add(over, false);
                var node = UIManager.Gumps.Last;
                node.Value.Should().BeSameAs(over);

                typeof(UIManager).GetMethod("SortControlsByInfo", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

                UIManager.Gumps.ToArray().Should().Equal(over, a, b);
                UIManager.Gumps.First.Should().BeSameAs(node, "the node is relinked, not replaced");
            }
            finally
            {
                UIManager.Gumps.Clear();

                foreach (Gump g in saved)
                {
                    UIManager.Gumps.AddLast(g);
                }
            }
        }
    }
}
