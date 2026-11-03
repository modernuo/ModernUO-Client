using System.Globalization;
using ClassicUO.Utility.Logging;

namespace ModernUO.Studio
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Log.Start(LogTypes.All);

            string pack = null, screenshot = null, focus = null, select = null;
            var layout = StudioLayout.Browse;
            int frames = 90;
            string goTo = null, selfTest = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "-pack" or "--pack" && i + 1 < args.Length)
                {
                    pack = args[++i];
                }
                else if (args[i] is "-screenshot" or "--screenshot" && i + 1 < args.Length)
                {
                    screenshot = args[++i];
                }
                else if (args[i] is "-focus" or "--focus" && i + 1 < args.Length)
                {
                    focus = args[++i];
                }
                else if (args[i] is "-select" or "--select" && i + 1 < args.Length)
                {
                    select = args[++i];
                }
                else if (args[i] is "-frames" or "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out int n))
                {
                    frames = Math.Max(2, n);
                    i++;
                }
                else if (args[i] is "-selftest" or "--selftest" && i + 1 < args.Length)
                {
                    selfTest = args[++i];
                }
                else if (args[i] is "-goto" or "--goto" && i + 1 < args.Length)
                {
                    goTo = args[++i];
                }
                else if (args[i] is "-layout" or "--layout" && i + 1 < args.Length)
                {
                    layout = string.Equals(args[++i], "map", StringComparison.OrdinalIgnoreCase) ? StudioLayout.Map : StudioLayout.Browse;
                }
            }

            var settings = StudioSettings.Load();

            using var game = new StudioGame(settings, pack, screenshot) { Focus = focus, Select = select, Layout = layout, ScreenshotFrames = frames, GoTo = goTo, SelfTestDir = selfTest };
            game.Run();

            Log.Stop();
        }
    }
}
