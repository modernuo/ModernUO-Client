using System.IO;
using ClassicUO.Configuration;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests
{
    // "Always use full size game window" keeps the windowed viewport (position and size) in the profile while it is on, so
    // turning it off restores what the player had instead of 600x480 at 20,20. An old profile without the keys gets that default.
    public class GameWindowWindowedProfileTests
    {
        [Fact]
        public void A_New_Profile_Has_The_Old_Defaults()
        {
            var p = new Profile();
            p.GameWindowWindowedPosition.Should().Be(new Point(20, 20));
            p.GameWindowWindowedSize.Should().Be(new Point(600, 480));
        }

        [Fact]
        public void The_Windowed_Viewport_Survives_A_Save_And_Load()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-windowed-{System.Guid.NewGuid():N}.json");

            try
            {
                var p = new Profile { GameWindowWindowedPosition = new Point(130, 70), GameWindowWindowedSize = new Point(853, 705) };
                ConfigurationResolver.Save(p, path, ProfileJsonContext.DefaultToUse.Profile);

                Profile loaded = ConfigurationResolver.Load<Profile>(path, ProfileJsonContext.DefaultToUse.Profile);
                loaded.GameWindowWindowedPosition.Should().Be(new Point(130, 70));
                loaded.GameWindowWindowedSize.Should().Be(new Point(853, 705));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
