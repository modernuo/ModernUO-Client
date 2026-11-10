using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Renderer;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    // With ShowMobilesHP off only the target mobiles can draw a health line, so HealthLinesManager visits those (at most
    // four) instead of every mobile. The checker (CheckOverride) runs the old every-mobile loop without drawing and compares
    // the mobiles drawn and their order; these scenes must give it no mismatch.
    public class HealthLinesManagerTests
    {
        private const uint PLAYER = 0x0000_0100;

        private sealed class Scene
        {
            public World World;
            public HealthLinesManager Manager;
            public Camera Camera;
            public readonly List<Mobile> Visited = new List<Mobile>();
            public readonly List<Mobile> Drawn = new List<Mobile>();
        }

        private static Scene Build(int mobiles)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            var s = new Scene { World = world, Manager = new HealthLinesManager(world), Camera = new Camera() };
            s.Camera.Bounds = new Rectangle(0, 0, 800, 600);
            world.Writer.SetHits(world.Player, 50, 40);
            world.Player.RealScreenPosition = new Point(400, 300);

            for (uint i = 1; i <= mobiles; i++)
            {
                Mobile m = world.GetOrCreateMobile(0x0000_1000 + i);
                world.Writer.SetHits(m, 100, (ushort)(i * 10 % 101));
                m.RealScreenPosition = new Point((int)(i * 60 % 820) - 10, (int)(i * 45 % 640) - 20);
            }

            s.Manager.CheckOverride = true;
            s.Manager.DryRun = true;
            s.Manager.VisitObserver = s.Visited.Add;
            s.Manager.DrawObserver = s.Drawn.Add;

            return s;
        }

        private static void Frame(Scene s, int mode, int showWhen, bool newTargetSystem, bool enabled)
        {
            s.Visited.Clear();
            s.Drawn.Clear();
            s.Manager.Configure(s.Camera, mode, showWhen, newTargetSystem, enabled);
            s.Manager.DrawMobiles(null, 0f);
        }

        private static void SetTargets(World w, uint lastTarget, uint lastAttack, uint selected, uint newTarget)
        {
            w.TargetManager.LastTargetInfo.Serial = lastTarget;
            w.TargetManager.LastAttack = lastAttack;
            w.TargetManager.SelectedTarget = selected;
            w.TargetManager.NewTargetSystemSerial = newTarget;
        }

        [Fact]
        public void Without_Targets_Nothing_Is_Visited_Or_Drawn()
        {
            Scene s = Build(10);
            SetTargets(s.World, 0, 0, 0, 0);
            Frame(s, 2, 0, false, false);

            s.Visited.Should().BeEmpty();
            s.Drawn.Should().BeEmpty();
            s.Manager.Checks.Should().Be(1);
            s.Manager.CheckMismatches.Should().Be(0);
        }

        [Fact]
        public void Targets_Draw_In_Mobiles_Order_Once_Each()
        {
            Scene s = Build(10);
            Mobile a = s.World.Mobiles.Get(0x1001), b = s.World.Mobiles.Get(0x1002), c = s.World.Mobiles.Get(0x1003);
            a.RealScreenPosition = b.RealScreenPosition = c.RealScreenPosition = new Point(300, 300);

            // named out of table order, one of them twice, plus the player
            SetTargets(s.World, c.Serial, a.Serial, c.Serial, PLAYER);
            Frame(s, 0, 0, false, false);

            List<Mobile> order = s.World.Mobiles.Values.Where(m => m == a || m == c || m == s.World.Player).ToList();
            s.Visited.Should().Equal(order);
            s.Drawn.Should().Equal(order);
            s.Manager.CheckMismatches.Should().Be(0);
        }

        // removed and re-added serials, destroyed-unswept mobiles, the player, mobiles leaving range (removed from Mobiles),
        // serials of no mobile, zero and full hits, off-screen positions, every showWhen and the new target system
        [Fact]
        public void Random_Scenes_Match_The_Every_Mobile_Loop()
        {
            Scene s = Build(16);
            var rng = new System.Random(1109);
            var serials = new List<uint> { 0, PLAYER, 0x0000_7777, 0x4000_0001 };

            for (uint i = 1; i <= 24; i++)
            {
                serials.Add(0x0000_1000 + i);
            }

            int drawn = 0, multi = 0;

            for (int step = 0; step < 3000; step++)
            {
                uint pick() => serials[rng.Next(serials.Count)];

                switch (rng.Next(8))
                {
                    case 0:
                    {
                        // leaves range (or never existed)
                        s.World.RemoveMobile(pick(), forceRemove: rng.Next(2) == 0);

                        break;
                    }
                    case 1:
                    {
                        uint serial = 0x0000_1000 + (uint)rng.Next(1, 25);
                        Mobile m = s.World.GetOrCreateMobile(serial);
                        m.RealScreenPosition = new Point(rng.Next(-60, 860), rng.Next(-60, 660));

                        break;
                    }
                    case 2:
                    {
                        Mobile m = s.World.Mobiles.Get(pick());

                        if (m != null && !m.IsDestroyed)
                        {
                            ushort max = (ushort)(rng.Next(3) == 0 ? 0 : 100);
                            s.World.Writer.SetHits(m, max, (ushort)(rng.Next(2) == 0 ? max : rng.Next(0, 101)));
                        }

                        break;
                    }
                    case 3:
                    {
                        Mobile m = s.World.Mobiles.Get(pick());

                        if (m != null)
                        {
                            m.RealScreenPosition = new Point(rng.Next(-60, 860), rng.Next(-60, 660));
                        }

                        break;
                    }
                    case 4:
                    {
                        // the old loop's sweep: destroyed mobiles leave the table
                        foreach (Mobile m in s.World.Mobiles.Values.Where(m => m.IsDestroyed && m != s.World.Player).ToList())
                        {
                            s.World.Mobiles.Remove(m.Serial);
                        }

                        break;
                    }
                    default:
                        SetTargets(s.World, pick(), pick(), pick(), pick());

                        break;
                }

                Frame(s, rng.Next(0, 3), rng.Next(0, 3), rng.Next(2) == 0, false);
                s.Visited.Count.Should().BeLessThanOrEqualTo(4);
                drawn += s.Drawn.Count;
                multi += s.Drawn.Count > 1 ? 1 : 0;
                s.Manager.CheckMismatches.Should().Be(0, $"step {step}");
            }

            drawn.Should().BeGreaterThan(100, "the scenes draw lines");
            multi.Should().BeGreaterThan(10, "some frames draw several, so the order is compared");
            s.Manager.Checks.Should().Be(3000);
        }

        // ShowMobilesHP on: every mobile is visited in Mobiles.Values order (mode 1: bars only, no hits text)
        [Fact]
        public void Enabled_Visits_Every_Mobile()
        {
            Scene s = Build(10);
            SetTargets(s.World, 0, 0, 0, 0);
            Frame(s, 1, 0, false, true);

            s.Visited.Should().Equal(s.World.Mobiles.Values.ToList());
            s.Drawn.Should().NotBeEmpty();
            s.Manager.Checks.Should().Be(0, "the checker compares the target walk only");
        }
    }
}
