using System.IO;
using ClassicUO.Configuration;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests
{
    // ReduceFPSWhenInactive defaults on. The default only applies to a profile.json without the key: profiles are written with every
    // property, so a profile saved before this default keeps its stored false (see the report: a one-time migration is not done).
    public class InactiveFpsProfileTests
    {
        [Fact]
        public void A_New_Profile_Reduces_FPS_When_Inactive()
        {
            new Profile().ReduceFPSWhenInactive.Should().BeTrue();
        }

        [Theory]
        [InlineData("{ }", true)]
        [InlineData("{ \"reduce_f_p_s_when_inactive\": false }", false)]
        [InlineData("{ \"reduce_f_p_s_when_inactive\": true }", true)]
        public void Stored_Value_Wins_Over_The_Default(string json, bool expected)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-inactive-{System.Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllText(path, json);

                ConfigurationResolver.Load<Profile>(path, ProfileJsonContext.DefaultToUse.Profile).ReduceFPSWhenInactive.Should().Be(expected);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
