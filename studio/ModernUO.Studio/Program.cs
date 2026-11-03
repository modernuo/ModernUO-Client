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
            }

            var settings = StudioSettings.Load();

            using var game = new StudioGame(settings, pack, screenshot) { Focus = focus, Select = select };
            game.Run();

            Log.Stop();
        }
    }
}
