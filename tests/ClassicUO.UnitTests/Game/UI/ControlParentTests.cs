using ClassicUO.Game;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // perf-9: Control.Parent skips its child-list scan for a control its caller already took out of the list
    // (OutOfParentList, ContainerItemControls.Begin). Every other re-parent keeps the old semantics: the control leaves the
    // old parent's list and is appended to the new one's, once.
    public sealed class ControlParentTests
    {
        private static Gump G(World world) => new Gump(world, 0, 0);

        [Fact]
        public void Moving_A_Control_Removes_It_From_The_Old_Parent()
        {
            var world = new World();
            Gump p1 = G(world), p2 = G(world), c = G(world);
            p1.Add(c);

            p2.Add(c);

            p1.Children.Should().NotContain(c);
            p2.Children.Should().Equal(c);
            c.Parent.Should().BeSameAs(p2);
        }

        [Fact]
        public void Re_Adding_To_The_Same_Parent_Moves_The_Control_To_The_End()
        {
            var world = new World();
            Gump p = G(world), a = G(world), b = G(world);
            p.Add(a);
            p.Add(b);

            p.Add(a);

            p.Children.Should().Equal(b, a);
        }

        [Fact]
        public void A_Control_Taken_Out_Of_The_List_Is_Re_Added_Once_And_Then_Moves_Normally()
        {
            var world = new World();
            Gump p = G(world), q = G(world), a = G(world), b = G(world);
            p.Add(a);
            p.Add(b);
            p.Children.Remove(a);
            a.OutOfParentList();
            a.Parent.Should().BeSameAs(p, "the parent link is kept while it is out of the list");

            p.Add(a);

            p.Children.Should().Equal(b, a);

            q.Add(a);

            p.Children.Should().Equal(b);
            q.Children.Should().Equal(a);
        }

        [Fact]
        public void Put_Back_Directly_The_Next_Re_Parent_Removes_It()
        {
            var world = new World();
            Gump p = G(world), q = G(world), a = G(world);
            p.Add(a);
            p.Children.Remove(a);
            a.OutOfParentList();
            p.Children.Add(a);
            a.InParentList();

            q.Add(a);

            p.Children.Should().BeEmpty();
            q.Children.Should().Equal(a);
        }

        [Fact]
        public void Insert_Honours_A_Control_Taken_Out_Of_The_List()
        {
            var world = new World();
            Gump p = G(world), a = G(world), b = G(world);
            p.Add(a);
            p.Add(b);
            p.Children.Remove(b);
            b.OutOfParentList();

            p.Insert(0, b);
            p.Children.Should().Equal(b, a);

            p.Remove(b);
            p.Children.Should().Equal(a);
            b.Parent.Should().BeNull();
        }
    }
}
