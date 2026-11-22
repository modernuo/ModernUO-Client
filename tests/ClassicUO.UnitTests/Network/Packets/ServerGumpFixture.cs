using System.Text;

namespace ClassicUO.UnitTests.Network.Packets
{
    /// <summary>A ModernUO-style server gump: 50 layout entries (a list of text lines, buttons, html, entries) and 20 text lines.</summary>
    internal static class ServerGumpFixture
    {
        public static (string Layout, string[] Lines) Build()
        {
            var sb = new StringBuilder();
            sb.Append("{ page 0 }{ resizepic 0 0 9200 400 520 }{ gumppictiled 10 10 380 500 2624 }{ checkertrans 10 10 380 500 }");
            sb.Append("{ xmfhtmlgumpcolor 20 15 360 20 1015000 0 0 32767 }");

            for (int i = 0; i < 15; i++)
            {
                sb.Append("{ text 20 ").Append(40 + i * 20).Append(" 1152 ").Append(i + 1).Append(" }");
            }

            for (int i = 0; i < 10; i++)
            {
                sb.Append("{ button 300 ").Append(40 + i * 20).Append(" 4005 4007 1 0 ").Append(i + 1).Append(" }");
            }

            sb.Append("{ htmlgump 20 340 360 40 16 1 1 }{ htmlgump 20 385 360 20 17 0 0 }");
            sb.Append("{ textentry 20 410 200 20 0 1 18 }{ checkbox 20 435 210 211 0 5 }{ group 0 }");
            sb.Append("{ radio 120 435 208 209 1 6 }{ radio 160 435 208 209 0 7 }{ tooltip 1011036 }");
            sb.Append("{ xmfhtmltok 20 460 200 20 0 0 32767 1060658 @Weight\t5@ }{ page 1 }");

            for (int i = 0; i < 8; i++)
            {
                sb.Append("{ gumppic ").Append(20 + i * 45).Append(" 40 5100 }");
            }

            sb.Append("{ croppedtext 10 490 100 20 37 19 }{ tilepichue 300 20 3821 0 }");

            var lines = new string[20];
            lines[0] = "Title";

            for (int i = 1; i < 20; i++)
            {
                lines[i] = "Line number " + i + " of the list";
            }

            return (sb.ToString(), lines);
        }

        public static void MakeResident(GameSeam seam)
        {
            foreach (ushort g in new ushort[] { 2624, 4005, 4007, 5100, 208, 209, 210, 211 })
            {
                seam.Resident(g, 30, 22);
            }

            for (ushort i = 0; i < 9; i++)
            {
                seam.Resident((ushort)(9200 + i), 16, 16);
            }
        }
    }
}
