using System.Collections.Generic;
using System.Globalization;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;

namespace ClassicUO.UnitTests.Game.UI
{
    // G0 (gump system): the command list a gump's draw walk queues, one line per command, for snapshot tests. Two walks
    // with equal snapshots queue equal draws (the batcher draws only from these values). Textures, texts and deferred targets
    // are named by first appearance (t0, t1, ...), so a snapshot compares structure and values, not object identity; a
    // text also shows its string and hue. G6 (per-gump recordings) compares a replay with a fresh walk this way.
    internal static class GumpCommandSnapshot
    {
        public static List<string> Take(Control root, float layerDepth = 0f)
        {
            var lists = new RenderLists();
            root.AddToRenderLists(lists, root.X, root.Y, ref layerDepth);

            return Describe(lists);
        }

        public static List<string> Describe(RenderLists lists)
        {
            var names = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            var lines = new List<string>(lists.GumpSpritesCount + lists.GumpTextsCount);

            for (int i = 0; i < lists.GumpSpritesCount; i++)
            {
                lines.Add("atlas " + Describe(lists.PeekGumpSprite(i), names));
            }

            for (int i = 0; i < lists.GumpTextsCount; i++)
            {
                lines.Add("text " + Describe(lists.PeekGumpText(i), names));
            }

            return lines;
        }

        private static string Name(object o, Dictionary<object, string> names, string prefix)
        {
            if (o == null)
            {
                return "null";
            }

            if (!names.TryGetValue(o, out string n))
            {
                names[o] = n = prefix + names.Count.ToString(CultureInfo.InvariantCulture);
            }

            return n;
        }

        private static string Describe(in GumpCommand c, Dictionary<object, string> names)
        {
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

            string common = $"{c.Kind} at {c.X},{c.Y} {c.Width}x{c.Height} src {c.Source.X},{c.Source.Y},{c.Source.Width},{c.Source.Height} "
                + $"hue {F(c.HueVector.X)},{F(c.HueVector.Y)},{F(c.HueVector.Z)} depth {F(c.LayerDepth)} alpha {F(c.Alpha)} h {c.Hue} arg {c.Arg}";

            switch (c.Kind)
            {
                case GumpCommandKind.Text:
                    return $"{common} {Name(c.Text, names, "r")} '{c.Text?.Text}' texthue {c.Text?.Hue}";

                case GumpCommandKind.Deferred:
                    return $"{common} {c.Target?.GetType().Name} {Name(c.Target, names, "d")}";

                case GumpCommandKind.Callback:
                    return $"{common} {Name(c.Callback, names, "f")}";

                default:
                    return $"{common} {Name(c.Texture, names, "t")}";
            }
        }
    }
}
