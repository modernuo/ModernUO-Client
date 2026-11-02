using System;
using System.Collections.Generic;
using ClassicUO.Game.UI.Controls;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // perf-10: Control.Update removes each run of disposed children with one RemoveRange instead of a RemoveAt per child.
    // The result must be what the per-child loop gave: the same survivors in the same order, the same children updated,
    // also when a child's Update disposes or adds siblings.
    public sealed class ControlUpdateTests
    {
        private sealed class Probe : Control
        {
            public readonly string Name;
            public Action OnUpdate;
            public List<string> Log;

            public Probe(string name, List<string> log)
            {
                Name = name;
                Log = log;
            }

            public override void Update()
            {
                Log.Add(Name);
                OnUpdate?.Invoke();
                base.Update();
            }

            public override string ToString() => Name;
        }

        // the loop before perf-10, as the reference
        private static void OldUpdate(Control parent)
        {
            for (int i = 0; i < parent.Children.Count; i++)
            {
                Control c = parent.Children[i];

                if (c.IsDisposed)
                {
                    parent.Children.RemoveAt(i--);

                    continue;
                }

                c.Update();
            }
        }

        private static (Probe Root, List<string> Log) Build(string pattern, Action<Probe, List<string>> wire = null)
        {
            var log = new List<string>();
            var root = new Probe("root", log);

            for (int i = 0; i < pattern.Length; i++)
            {
                var c = new Probe(((char)('a' + i)).ToString(), log);
                root.Add(c);

                if (pattern[i] == 'x')
                {
                    c.Dispose();
                }
            }

            wire?.Invoke(root, log);

            return (root, log);
        }

        private static void Same(string pattern, Action<Probe, List<string>> wire = null)
        {
            (Probe a, List<string> logA) = Build(pattern, wire);
            (Probe b, List<string> logB) = Build(pattern, wire);

            OldUpdate(a);
            b.Update();
            logB.RemoveAt(0); // the root's own entry

            b.Children.ConvertAll(c => ((Probe)c).Name).Should().Equal(a.Children.ConvertAll(c => ((Probe)c).Name), pattern);
            logB.Should().Equal(logA, pattern);
        }

        [Theory]
        [InlineData("")]
        [InlineData("x")]
        [InlineData("xxxx")]
        [InlineData("oooo")]
        [InlineData("xxoxxxoox")]
        [InlineData("oxoxox")]
        [InlineData("ooxx")]
        public void Disposed_Runs_Leave_What_The_Per_Child_Loop_Left(string pattern) => Same(pattern);

        [Fact]
        public void A_Child_That_Disposes_Later_Siblings_And_Adds_One_Gives_The_Same_List()
        {
            Same("oxoooxo", (root, log) =>
            {
                var first = (Probe)root.Children[0];
                first.OnUpdate = () =>
                {
                    root.Children[3].Dispose();
                    root.Children[4].Dispose();
                    root.Add(new Probe("late", log));
                };
            });
        }

        [Fact]
        public void A_Child_That_Removes_An_Earlier_Sibling_Gives_The_Same_List()
        {
            Same("oxoxo", (root, log) =>
            {
                var third = (Probe)root.Children[2];
                third.OnUpdate = () => root.Remove(root.Children[0]);
            });
        }

        [Fact]
        public void Every_Child_Disposed_Empties_The_List()
        {
            (Probe root, _) = Build(new string('x', 500));

            root.Update();

            root.Children.Should().BeEmpty();
        }
    }
}
