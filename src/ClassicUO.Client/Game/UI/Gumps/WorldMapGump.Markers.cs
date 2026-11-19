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
    internal partial class WorldMapGump
    {
        internal class WMapMarker
        {
            public string Name { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int MapId { get; set; }
            public Color Color { get; set; }
            public Texture2D MarkerIcon { get; set; }
            public string MarkerIconName { get; set; }
            public int ZoomIndex { get; set; }
            public string ColorName { get; set; }
        }

        internal class WMapMarkerFile
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public List<WMapMarker> Markers { get; set; }
            public bool Hidden { get; set; }
            public bool IsEditable { get; set; }
        }

        private void LoadMarkers()
        {
            //return Task.Run(() =>
            {
                if (World.InGame)
                {
                    _mapMarkersLoaded = false;

                    GameActions.Print(World, ResGumps.LoadingWorldMapMarkers, 0x2A);

                    foreach (Texture2D t in _markerIcons.Values)
                    {
                        if (t != null && !t.IsDisposed)
                        {
                            t.Dispose();
                        }
                    }

                    if (!File.Exists(UserMarkersFilePath))
                    {
                        try
                        {
                            // CreateNew: another instance creating it first must not be truncated
                            using (new FileStream(UserMarkersFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                        }
                        catch (IOException)
                        {
                        }
                    }

                    _markerIcons.Clear();

                    if (!Directory.Exists(_mapIconsPath))
                    {
                        Directory.CreateDirectory(_mapIconsPath);
                    }

                    foreach (string icon in Directory.GetFiles(_mapIconsPath, "*.cur").Union(Directory.GetFiles(_mapIconsPath, "*.ico")))
                    {
                        FileStream fs = new FileStream(icon, FileMode.Open, FileAccess.Read);
                        MemoryStream ms = new MemoryStream();
                        fs.CopyTo(ms);
                        ms.Seek(0, SeekOrigin.Begin);

                        try
                        {
                            Texture2D texture = CurLoader.CreateTextureFromICO_Cur(ms);

                            _markerIcons.Add(Path.GetFileNameWithoutExtension(icon).ToLower(), texture);
                        }
                        catch (Exception ee)
                        {
                            Log.Error($"{ee}");
                        }
                        finally
                        {
                            ms.Dispose();
                            fs.Dispose();
                        }
                    }

                    foreach (string icon in Directory.GetFiles(_mapIconsPath, "*.png").Union(Directory.GetFiles(_mapIconsPath, "*.jpg")))
                    {
                        FileStream fs = new FileStream(icon, FileMode.Open, FileAccess.Read);
                        MemoryStream ms = new MemoryStream();
                        fs.CopyTo(ms);
                        ms.Seek(0, SeekOrigin.Begin);

                        try
                        {
                            Texture2D texture = Texture2D.FromStream(Client.Game.GraphicsDevice, ms);

                            _markerIcons.Add(Path.GetFileNameWithoutExtension(icon).ToLower(), texture);
                        }
                        catch (Exception ee)
                        {
                            Log.Error($"{ee}");
                        }
                        finally
                        {
                            ms.Dispose();
                            fs.Dispose();
                        }
                    }

                    List<string> mapFiles = new List<string> { UserMarkersFilePath };
                    mapFiles.AddRange(Directory.GetFiles(_mapFilesPath, "*.map")
                                        .Union(Directory.GetFiles(_mapFilesPath, "*.csv"))
                                        .Union(Directory.GetFiles(_mapFilesPath, "*.xml")));

                    _markerFiles.Clear();

                    foreach (string mapFile in mapFiles)
                    {
                        if (File.Exists(mapFile))
                        {
                            WMapMarkerFile markerFile = new WMapMarkerFile
                            {
                                Hidden = false,
                                Name = Path.GetFileNameWithoutExtension(mapFile),
                                FullPath = mapFile,
                                Markers = new List<WMapMarker>(),
                                IsEditable = false,
                            };

                            string hiddenFile = _hiddenMarkerFiles.FirstOrDefault(x => x.Contains(markerFile.Name));

                            if (!string.IsNullOrEmpty(hiddenFile))
                            {
                                markerFile.Hidden = true;
                            }

                            if (mapFile != null && Path.GetExtension(mapFile).ToLower().Equals(".xml")) // Ultima Mapper
                            {
                                using (XmlTextReader reader = new XmlTextReader(File.Open(mapFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                                {
                                    while (reader.Read())
                                    {
                                        if (reader.Name.Equals("Marker"))
                                        {
                                            WMapMarker marker = new WMapMarker
                                            {
                                                X = int.Parse(reader.GetAttribute("X")),
                                                Y = int.Parse(reader.GetAttribute("Y")),
                                                Name = reader.GetAttribute("Name"),
                                                MapId = int.Parse(reader.GetAttribute("Facet")),
                                                Color = Color.White,
                                                ZoomIndex = 3
                                            };

                                            if (_markerIcons.TryGetValue(reader.GetAttribute("Icon").ToLower(), out Texture2D value))
                                            {
                                                marker.MarkerIcon = value;

                                                marker.MarkerIconName = reader.GetAttribute("Icon").ToLower();
                                            }

                                            markerFile.Markers.Add(marker);
                                        }
                                    }

                                }
                            }
                            else if (mapFile != null && Path.GetExtension(mapFile).ToLower().Equals(".map")) //UOAM
                            {
                                using (StreamReader reader = new StreamReader(File.Open(mapFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                                {
                                    while (!reader.EndOfStream)
                                    {
                                        string line = reader.ReadLine();

                                        // ignore empty lines, and if UOAM, ignore the first line that always has a 3
                                        if (string.IsNullOrEmpty(line) || line.Equals("3"))
                                        {
                                            continue;
                                        }

                                        // Check for UOAM file
                                        if (line.Substring(0, 1).Equals("+") || line.Substring(0, 1).Equals("-"))
                                        {
                                            string icon = line.Substring(1, line.IndexOf(':') - 1);

                                            line = line.Substring(line.IndexOf(':') + 2);

                                            string[] splits = line.Split(' ');

                                            if (splits.Length <= 1)
                                            {
                                                continue;
                                            }

                                            WMapMarker marker = new WMapMarker
                                            {
                                                X = int.Parse(splits[0]),
                                                Y = int.Parse(splits[1]),
                                                MapId = int.Parse(splits[2]),
                                                Name = string.Join(" ", splits, 3, splits.Length - 3),
                                                Color = Color.White,
                                                ZoomIndex = 3
                                            };

                                            string[] iconSplits = icon.Split(' ');

                                            marker.MarkerIconName = iconSplits[0].ToLower();

                                            if (_markerIcons.TryGetValue(iconSplits[0].ToLower(), out Texture2D value))
                                            {
                                                marker.MarkerIcon = value;
                                            }

                                            markerFile.Markers.Add(marker);
                                        }
                                    }
                                }
                            }
                            else if (mapFile != null && Path.GetExtension(mapFile).ToLower().Equals(".usr"))
                            {
                                markerFile.Markers = LoadUserMarkers() ?? new List<WMapMarker>();
                                markerFile.IsEditable = true;
                            }
                            else if (mapFile != null) //CSV x,y,mapindex,name of marker,iconname,color,zoom
                            {
                                using (StreamReader reader = new StreamReader(File.Open(mapFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                                {
                                    while (!reader.EndOfStream)
                                    {
                                        string line = reader.ReadLine();

                                        if (string.IsNullOrEmpty(line))
                                        {
                                            return;
                                        }

                                        string[] splits = line.Split(',');

                                        if (splits.Length <= 1)
                                        {
                                            continue;
                                        }

                                        WMapMarker marker = new WMapMarker
                                        {
                                            X = int.Parse(splits[0]),
                                            Y = int.Parse(splits[1]),
                                            MapId = int.Parse(splits[2]),
                                            Name = splits[3],
                                            MarkerIconName = splits[4].ToLower(),
                                            Color = GetColor(splits[5]),
                                            ZoomIndex = splits.Length == 7 ? int.Parse(splits[6]) : 3
                                        };

                                        if (_markerIcons.TryGetValue(splits[4].ToLower(), out Texture2D value))
                                        {
                                            marker.MarkerIcon = value;
                                        }

                                        markerFile.Markers.Add(marker);
                                    }
                                }
                            }

                            if (markerFile.Markers.Count > 0)
                            {
                                GameActions.Print(World, $"..{Path.GetFileName(mapFile)} ({markerFile.Markers.Count})", 0x2B);
                            }
                            _markerFiles.Add(markerFile);
                        }
                    }

                    BuildContextMenu();

                    int count = 0;

                    foreach (WMapMarkerFile file in _markerFiles)
                    {
                        count += file.Markers.Count;
                    }

                    _mapMarkersLoaded = true;

                    GameActions.Print(World, string.Format(ResGumps.WorldMapMarkersLoaded0, count), 0x2A);
                }
            }

            //);
        }

        private void AddMarkerOnPlayer()
        {
            if (!World.InGame)
            {
                return;
            }

            var entryDialog = new EntryDialog(World, 250, 150, ResGumps.EnterMarkerName, SaveMakerOnPlayer)
            {
                CanCloseWithRightClick = true
            };

            UIManager.Add(entryDialog);
        }

        private void SaveMakerOnPlayer(string markerName)
        {
            if (!World.InGame)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(markerName))
            {
                GameActions.Print(World, ResGumps.InvalidMarkerName, 0x2A);
            }

            var markerColor = "blue";
            var markerIcon = "";
            var markerZoomLevel = 3;

            var markerCsv = $"{World.Player.X},{World.Player.Y},{_map.Index},{markerName},{markerIcon},{markerColor},{markerZoomLevel}";

            try
            {
                AtomicFile.AppendAllText(UserMarkersFilePath, markerCsv + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Error($"could not save the marker: {e.Message}");

                // a marker that would vanish on reload is not shown either
                return;
            }

            var mapMarker = new WMapMarker
            {
                X = World.Player.X,
                Y = World.Player.Y,
                Color = GetColor(markerColor),
                ColorName = markerColor,
                MapId = _map.Index,
                MarkerIconName = markerIcon,
                Name = markerName,
                ZoomIndex = markerZoomLevel
            };

            if (!string.IsNullOrWhiteSpace(mapMarker.MarkerIconName) && _markerIcons.TryGetValue(mapMarker.MarkerIconName, out Texture2D markerIconTexture))
            {
                mapMarker.MarkerIcon = markerIconTexture;
            }

            var mapMarkerFile = _markerFiles.FirstOrDefault(x => x.FullPath == UserMarkersFilePath);

            mapMarkerFile?.Markers.Add(mapMarker);
        }

        /// <summary>
        /// Reload User Markers File after Changes
        /// </summary>
        internal static void ReloadUserMarkers()
        {
            var userFile = _markerFiles.FirstOrDefault(f => f.Name == USER_MARKERS_FILE);

            if (userFile == null)
            {
                return;
            }

            // a failed read keeps the markers already on the map
            userFile.Markers = LoadUserMarkers() ?? userFile.Markers;
        }

        /// <summary>
        /// Load User Markers to List of Markers
        /// </summary>
        /// <returns>List of loaded Markers; null if the file cannot be read (another instance replacing it for too long)</returns>
        internal static List<WMapMarker> LoadUserMarkers()
        {
            List<WMapMarker> tempList = new List<WMapMarker>();

            try
            {
                using (StreamReader reader = new StreamReader(AtomicFile.OpenRead(UserMarkersFilePath)))
                {
                    while (!reader.EndOfStream)
                    {
                        string line = reader.ReadLine();

                        if (string.IsNullOrEmpty(line))
                        {
                            continue;
                        }

                        string[] splits = line.Split(',');

                        if (splits.Length <= 1)
                        {
                            continue;
                        }
                        tempList.Add(ParseMarker(splits));
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Error($"could not read the user markers: {e.Message}");

                return null;
            }

            return tempList;
        }

        /// <summary>
        /// Parser String to Marker
        /// </summary>
        /// <param name="splits">Array of string contain information about Marker</param>
        /// <returns>Marker</returns>
        internal static WMapMarker ParseMarker(string[] splits)
        {
            WMapMarker marker = new WMapMarker
            {
                X = int.Parse(Truncate(splits[0], 4)),
                Y = int.Parse(Truncate(splits[1], 4)),
                MapId = int.Parse(splits[2]),
                Name = Truncate(splits[3], 25),
                MarkerIconName = splits[4].ToLower(),
                Color = GetColor(Truncate(splits[5], 10)),
                ColorName = Truncate(splits[5], 10),
                ZoomIndex = splits.Length == 7 ? int.Parse(splits[6]) : 3
            };

            if (_markerIcons.TryGetValue(splits[4].ToLower(), out Texture2D value))
            {
                marker.MarkerIcon = value;
            }

            return marker;
        }

        /// <summary>
        /// Truncate string to max length
        /// </summary>
        /// <param name="s">String</param>
        /// <param name="maxLen">Max Length</param>
        /// <returns>Truncated String</returns>
        private static string Truncate(string s, int maxLen)
        {
            return s.Length > maxLen ? s.Remove(maxLen) : s;
        }

        /// <summary>
        /// Map Color name to Color in XNA
        /// </summary>
        private static readonly Dictionary<string, Color> _colorMap = new Dictionary<string, Color>
        {
            { "red", Color.Red },
            { "green", Color.Green },
            { "blue", Color.Blue },
            { "purple", Color.Purple },
            { "black", Color.Black },
            { "yellow", Color.Yellow },
            { "white", Color.White },
            { "none", Color.Transparent },
        };

        /// <summary>
        /// Get Color for Texture by name
        /// </summary>
        /// <param name="name">Color name</param>
        /// <returns>Color in XNA (RGBA)</returns>
        public static Color GetColor(string name)
        {
            return _colorMap.TryGetValue(name, out var color) ? color : Color.White;
        }
    }
}
