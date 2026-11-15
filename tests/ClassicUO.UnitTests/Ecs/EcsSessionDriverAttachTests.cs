using System.IO;
using System.Linq;
using System.Reflection;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // perf-9: -ecsdriver plays with the ECS checks off. The driver used to load behind WorldEcs.Update's -ecscheck guard, so a
    // FrameStats counter job (-ecscheck 0) logged in and ran to its timeout without one driver line. The driver is CUO_DEVTOOLS
    // only (reached by reflection; the release variant passes vacuously).
    [Collection(nameof(FunnelCheckCollection))]
    public class EcsSessionDriverAttachTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(120)]
        public void Driver_Loads_Whatever_The_EcsCheck_Interval(int interval)
        {
            PropertyInfo driverProp = typeof(WorldEcs).GetProperty("Driver");

            if (driverProp == null)
            {
                return;
            }

            string script = Path.GetTempFileName();
            File.WriteAllLines(script, new[] { "wait 1" });
            int savedInterval = CUOEnviroment.EcsCheckFrames;
            FieldInfo scriptField = typeof(CUOEnviroment).GetField("EcsDriverScript");
            object savedScript = scriptField.GetValue(null);

            try
            {
                CUOEnviroment.EcsCheckFrames = interval;
                scriptField.SetValue(null, script);
                var world = new World();

                world.Ecs.Update();

                driverProp.GetValue(world.Ecs).Should().NotBeNull("the -ecsdriver script loads with -ecscheck {0}", interval);
                scriptField.GetValue(null).Should().BeNull("the script is taken once");
            }
            finally
            {
                CUOEnviroment.EcsCheckFrames = savedInterval;
                scriptField.SetValue(null, savedScript);

                foreach (Gump g in UIManager.Gumps.ToList())
                {
                    if (g.GetType().Name == "DriverGump")
                    {
                        g.Dispose();
                    }
                }

                File.Delete(script);
            }
        }
    }
}
