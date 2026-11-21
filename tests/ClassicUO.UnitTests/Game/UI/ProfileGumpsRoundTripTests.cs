using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // G0 (gump system): Profile.SaveGumps then Profile.ReadGumps gives back the open gumps: type, position, serial and the
    // type's own attributes. G9 replaces ReadGumps' type switches with a registry; this round trip must stay equal. It uses
    // the saved types that build without the game's assets (most others load art in their constructor or Restore).
    [Collection(nameof(FunnelCheckCollection))] // UIManager.Gumps and ProfileManager.ProfilePath are process-wide
    public sealed class ProfileGumpsRoundTripTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuo-gumps-" + Guid.NewGuid().ToString("N"));
        private readonly string _savedProfilePath = ProfileManager.ProfilePath;
        private readonly Point? _savedNameHandler = NameOverHeadHandlerGump.LastPosition;

        public ProfileGumpsRoundTripTests()
        {
            Directory.CreateDirectory(_dir);
            SetProfilePath(_dir); // SkillsGroupManager saves and loads skillsgroups.xml there
        }

        public void Dispose()
        {
            SetProfilePath(_savedProfilePath);
            NameOverHeadHandlerGump.LastPosition = _savedNameHandler;

            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        private static void SetProfilePath(string path) =>
            typeof(ProfileManager).GetProperty(nameof(ProfileManager.ProfilePath))!.SetValue(null, path);

        private static void SaveGumps(Profile profile, World world, string dir) =>
            typeof(Profile).GetMethod("SaveGumps", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(profile, new object[] { world, dir });

        private static string Describe(Gump g) =>
            $"{g.GumpType} {g.X},{g.Y} serial {g.LocalSerial} " + g switch
            {
                DebugGump d => $"min {d.IsMinimized}",
                NetworkStatsGump n => $"min {n.IsMinimized}",
                _ => ""
            };

        [Fact]
        public void Saved_Gumps_Read_Back_Equal()
        {
            using var scope = new UiTestScope();
            var world = new World();
            var profile = new Profile();

            UIManager.Add(new DebugGump(world, 120, 130) { IsMinimized = true });
            UIManager.Add(new NetworkStatsGump(world, 300, 40));
            UIManager.Add(new InfoBarGump(world) { X = 7, Y = 9 });
            UIManager.Add(new Gump(world, 0x1234, 0x99) { X = 1, Y = 2 }); // GumpType.None: not saved

            var open = UIManager.Gumps.Where(g => g.CanBeSaved).Select(Describe).ToList();
            open.Should().HaveCount(3);

            SaveGumps(profile, world, _dir);
            File.Exists(Path.Combine(_dir, "gumps.xml")).Should().BeTrue();

            var read = profile.ReadGumps(world, _dir);

            read.Select(Describe).Should().BeEquivalentTo(open, "every saved gump comes back with its type, position and state");
            read.Should().OnlyContain(g => !g.IsDisposed);
        }

        [Fact]
        public void The_Name_Handler_Position_Is_Read_Without_A_Gump()
        {
            File.WriteAllText(Path.Combine(_dir, "gumps.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?><gumps>"
                + $"<gump type=\"{(int)GumpType.NameOverHeadHandler}\" x=\"33\" y=\"44\" serial=\"0\" /></gumps>");

            new Profile().ReadGumps(new World(), _dir).Should().BeEmpty();
            NameOverHeadHandlerGump.LastPosition.Should().Be(new Point(33, 44));
        }
    }
}
