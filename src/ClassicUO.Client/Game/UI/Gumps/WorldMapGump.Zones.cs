// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.IO;
using ClassicUO.Network.Encryption;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static ClassicUO.Game.UI.Gumps.WorldMapGump;
using SpriteFont = ClassicUO.Renderer.SpriteFont;

namespace ClassicUO.Game.UI.Gumps
{
    [JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(ZonesFile), GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(ZonesFileZoneData), GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(List<ZonesFileZoneData>), GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(List<int>), GenerationMode = JsonSourceGenerationMode.Metadata)]
    sealed partial class ZonesJsonContext : JsonSerializerContext { }

    internal partial class WorldMapGump
    {
        internal class ZonesFileZoneData
        {
            public string Label { get; set; }

            public string Color { get; set; }

            public List<List<int>> Polygon { get; set; }
        }

        internal class ZonesFile
        {
            public int MapIndex { get; set; }
            public List<ZonesFileZoneData> Zones { get; set; }
        }

        private class Zone
        {
            public string Label;
            public Color Color;
            public Rectangle BoundingRectangle;
            public List<Point> Vertices;

            public Zone(ZonesFileZoneData data)
            {
                Label = data.Label;
                Color = _colorMap[data.Color];

                Vertices = new List<Point>();

                int xmin = int.MaxValue;
                int xmax = int.MinValue;
                int ymin = int.MaxValue;
                int ymax = int.MinValue;

                foreach (List<int> rawPoint in data.Polygon)
                {
                    Point p = new Point(rawPoint[0], rawPoint[1]);

                    if (p.X < xmin) xmin = p.X;
                    if (p.X > xmax) xmax = p.X;
                    if (p.Y < ymin) ymin = p.Y;
                    if (p.Y > ymax) ymax = p.Y;

                    Vertices.Add(p);
                }

                BoundingRectangle = new Rectangle(xmin, ymin, xmax - xmin, ymax - ymin);
            }
        }

        private class ZoneSet
        {
            public int MapIndex;
            public List<Zone> Zones = new List<Zone>();
            public bool Hidden = false;
            public string NiceFileName;

            public ZoneSet(ZonesFile zf, string filename, bool hidden)
            {
                MapIndex = zf.MapIndex;
                foreach (ZonesFileZoneData data in zf.Zones)
                {
                    Zones.Add(new Zone(data));
                }

                Hidden = hidden;
                NiceFileName = MakeNiceFileName(filename);
            }

            public static string MakeNiceFileName(string filename)
            {
                // Yes, we invoke the same method twice, because our filenames have two layers of extension
                // we want to strip off (.zones.json)
                return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filename));
            }
        }

        private class ZoneSets
        {
            public Dictionary<string, ZoneSet> ZoneSetDict { get; } = new Dictionary<string, ZoneSet>();

            public void AddZoneSetByFileName(World world, string filename, bool hidden)
            {
                try
                {
                    var zf = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(filename), ZonesJsonContext.Default.ZonesFile);
                    ZoneSetDict[filename] = new ZoneSet(zf, filename, hidden);
                    GameActions.Print(world, string.Format(ResGumps.MapZoneFileLoaded, ZoneSetDict[filename].NiceFileName), 0x3A /* yellow green */);
                }
                catch (Exception ee)
                {
                    Log.Error($"{ee}");
                }
            }

            public void Clear()
            {
                ZoneSetDict.Clear();
            }
        }

        private void LoadZones()
        {
            Log.Trace("LoadZones()...");

            _zoneSets.Clear();

            foreach (String filename in Directory.GetFiles(_mapFilesPath, "*.zones.json"))
            {
                bool shouldHide = !string.IsNullOrEmpty
                (
                    _hiddenZoneFiles.FirstOrDefault(x => x.Contains(filename))
                );

                _zoneSets.AddZoneSetByFileName(World, filename, shouldHide);
            }
        }
    }
}
