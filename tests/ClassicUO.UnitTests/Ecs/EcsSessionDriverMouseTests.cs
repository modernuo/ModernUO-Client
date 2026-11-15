using System;
using System.Reflection;
using ClassicUO.Input;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T2 audit round 2: the session driver owns the scripted cursor (`mouse x y`, Input.Mouse.Override). It must not outlive
    // the script: released at EOF, when a line throws, and on detach (driver gump disposed, world out of the game); an
    // override the driver did not set (DevBench) is left alone. The driver is CUO_DEVTOOLS only (the tests are compiled
    // without the define), so it is reached by reflection and the tests pass vacuously in the release variant.
    [Collection(nameof(FunnelCheckCollection))]
    public class EcsSessionDriverMouseTests
    {
        private static readonly Type DriverType = typeof(Mouse).Assembly.GetType("ClassicUO.Game.EcsSessionDriver");

        private sealed class Driver
        {
            private readonly object _d;

            public Driver(params string[] lines)
            {
                _d = DriverType.GetMethod("FromLines", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { lines });
            }

            public bool Step(Func<string, bool> execute)
            {
                try
                {
                    return (bool)DriverType.GetMethod("Step", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_d, new object[] { execute });
                }
                catch (TargetInvocationException e)
                {
                    throw e.InnerException!;
                }
            }

            public void Release() => DriverType.GetMethod("ReleaseMouse", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_d, null);

            public bool Owns => (bool)DriverType.GetProperty("OwnsMouse", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_d);
        }

        private static bool Yield(string line) => true;

        // the dev variant (CUO_DEVTOOLS) must have the driver; the release variant has none
        private static bool Dev()
        {
#if CUO_DEVTOOLS
            const bool dev = true;
#else
            const bool dev = false;
#endif
            (DriverType != null).Should().Be(dev, "the driver exists exactly in the dev variant");

            return DriverType != null;
        }

        [Fact]
        public void Mouse_Override_Is_Released_At_The_End_Of_The_Script()
        {
            if (!Dev()) return;

            Mouse.Override = null;
            var d = new Driver("mouse 600 300", "wait 1");

            d.Step(Yield).Should().BeTrue();
            Mouse.Override.Should().Be(new Point(600, 300));
            d.Owns.Should().BeTrue();

            d.Step(Yield).Should().BeTrue(); // wait: the script is still running, the cursor stays
            Mouse.Override.Should().Be(new Point(600, 300));

            d.Step(Yield).Should().BeFalse(); // EOF
            Mouse.Override.Should().BeNull();
            d.Owns.Should().BeFalse();
        }

        [Fact]
        public void Mouse_Off_Clears_And_A_Later_End_Leaves_Others_Alone()
        {
            if (!Dev()) return;

            Mouse.Override = null;
            var d = new Driver("mouse 10 20", "mouse off");
            d.Step(Yield);
            d.Step(Yield);
            Mouse.Override.Should().BeNull();
            d.Owns.Should().BeFalse();

            Mouse.Override = new Point(1, 2); // someone else's (DevBench)
            d.Step(Yield).Should().BeFalse();
            Mouse.Override.Should().Be(new Point(1, 2));
            Mouse.Override = null;
        }

        [Fact]
        public void Mouse_Override_Is_Released_When_A_Line_Throws()
        {
            if (!Dev()) return;

            Mouse.Override = null;
            var d = new Driver("mouse 5 6", "boom");
            d.Step(Yield);
            Mouse.Override.Should().Be(new Point(5, 6));

            Action act = () => d.Step(_ => throw new InvalidOperationException("boom"));
            act.Should().Throw<InvalidOperationException>();
            Mouse.Override.Should().BeNull();
        }

        [Fact]
        public void Mouse_Override_Is_Released_On_Detach_And_Only_If_Owned()
        {
            if (!Dev()) return;

            Mouse.Override = null;
            var d = new Driver("mouse 7 8", "wait 1");
            d.Step(Yield);
            d.Release(); // driver gump disposed / world out of the game
            Mouse.Override.Should().BeNull();

            var other = new Driver("wait 1");
            Mouse.Override = new Point(3, 4);
            other.Release();
            Mouse.Override.Should().Be(new Point(3, 4));
            Mouse.Override = null;
        }
    }
}
