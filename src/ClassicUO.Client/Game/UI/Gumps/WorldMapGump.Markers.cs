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
            if (!World.InGame)
            {
                return;
            }

            _mapMarkersLoaded = false;

            GameActions.Print(World, ResGumps.LoadingWorldMapMarkers, 0x2A);

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

            LoadMarkerIcons();

            List<string> mapFiles = new List<string> { UserMarkersFilePath };
            mapFiles.AddRange(Directory.GetFiles(_mapFilesPath, "*.map")
                                .Union(Directory.GetFiles(_mapFilesPath, "*.csv"))
                                .Union(Directory.GetFiles(_mapFilesPath, "*.xml")));

            _markerFiles.Clear();

            foreach (string mapFile in mapFiles)
            {
                if (!File.Exists(mapFile))
                {
                    continue;
                }

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

                string extension = Path.GetExtension(mapFile).ToLower();

                if (extension == ".usr")
                {
                    markerFile.Markers = LoadUserMarkers() ?? new List<WMapMarker>();
                    markerFile.IsEditable = true;
                }
                else
                {
                    // a file that cannot be read keeps the markers read before the error; a bad line is skipped
                    try
                    {
                        using FileStream stream = File.Open(mapFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                        if (extension == ".xml") // Ultima Mapper
                        {
                            ReadXmlMarkers(stream, mapFile, markerFile.Markers);
                        }
                        else
                        {
                            ReadLineMarkers(stream, mapFile, extension == ".map" ? ParseUoamMarker : ParseCsvMarker, markerFile.Markers);
                        }
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is XmlException)
                    {
                        Log.Error($"world map markers: could not read {mapFile}: {e.Message}");
                    }
                }

                if (markerFile.Markers.Count > 0)
                {
                    GameActions.Print(World, $"..{Path.GetFileName(mapFile)} ({markerFile.Markers.Count})", 0x2B);
                }
                _markerFiles.Add(markerFile);
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

        /// <summary>
        /// Loads the icon files of MapIcons that are not loaded yet. Icons stay for the session: MarkersManagerGump and
        /// UserMarkersGump keep references to them, so a reopen or "Reload markers" must not dispose them (every open used
        /// to); a reload adds new files, and a changed icon file needs a restart. The folder bounds the set.
        /// </summary>
        private static void LoadMarkerIcons()
        {
            Directory.CreateDirectory(_mapIconsPath);

            foreach (string icon in Directory.GetFiles(_mapIconsPath, "*.cur").Union(Directory.GetFiles(_mapIconsPath, "*.ico")))
            {
                LoadMarkerIcon(icon, true);
            }

            foreach (string icon in Directory.GetFiles(_mapIconsPath, "*.png").Union(Directory.GetFiles(_mapIconsPath, "*.jpg")))
            {
                LoadMarkerIcon(icon, false);
            }
        }

        private static void LoadMarkerIcon(string path, bool cursorFormat)
        {
            string key = Path.GetFileNameWithoutExtension(path).ToLower();

            // also skips the second of two files with one name (a.cur, a.png): it used to be decoded and then leaked when
            // Add threw on the duplicate key
            if (_markerIcons.ContainsKey(key))
            {
                return;
            }

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Seek(0, SeekOrigin.Begin);

                Texture2D texture;

                if (cursorFormat)
                {
                    texture = CurLoader.CreateTextureFromICO_Cur(ms);
                }
                else
                {
                    texture = Texture2D.FromStream(Client.Game.GraphicsDevice, ms);
                    ClassicUO.Renderer.TextureUploads.Changed();
                }

                _markerIcons.Add(key, texture);
            }
            catch (Exception ee)
            {
                Log.Error($"world map icon {path}: {ee}");
            }
        }

        /// <summary>Ultima Mapper XML: Marker elements with X, Y, Name, Facet and Icon. A marker with a bad attribute is
        /// skipped and logged.</summary>
        internal static void ReadXmlMarkers(Stream stream, string fileName, List<WMapMarker> markers)
        {
            using XmlTextReader reader = new XmlTextReader(stream);

            while (reader.Read())
            {
                if (!reader.Name.Equals("Marker"))
                {
                    continue;
                }

                try
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

                    string icon = reader.GetAttribute("Icon")?.ToLower();

                    if (icon != null && _markerIcons.TryGetValue(icon, out Texture2D value))
                    {
                        marker.MarkerIcon = value;

                        marker.MarkerIconName = icon;
                    }

                    markers.Add(marker);
                }
                catch (Exception e) when (IsBadMarkerLine(e))
                {
                    Log.Warn($"world map markers: {fileName} line {reader.LineNumber}: {e.Message}");
                }
            }
        }

        /// <summary>A line-based marker file: each line parsed by <paramref name="parse"/> (null: not a marker); a line
        /// that does not parse is skipped and logged. Blank lines are skipped (a blank line in a CSV file used to end
        /// LoadMarkers altogether, so no marker of any file was drawn).</summary>
        internal static void ReadLineMarkers(Stream stream, string fileName, Func<string, WMapMarker> parse, List<WMapMarker> markers)
        {
            using StreamReader reader = new StreamReader(stream);
            int lineNumber = 0;

            while (reader.ReadLine() is { } line)
            {
                lineNumber++;

                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                try
                {
                    WMapMarker marker = parse(line);

                    if (marker != null)
                    {
                        markers.Add(marker);
                    }
                }
                catch (Exception e) when (IsBadMarkerLine(e))
                {
                    Log.Warn($"world map markers: {fileName} line {lineNumber}: {e.Message}");
                }
            }
        }

        private static bool IsBadMarkerLine(Exception e) =>
            e is FormatException || e is OverflowException || e is ArgumentException || e is IndexOutOfRangeException || e is NullReferenceException;

        /// <summary>UOAM .map line: "+icon name: x y facet marker name"; the first line ("3") and other lines are not markers.</summary>
        internal static WMapMarker ParseUoamMarker(string line)
        {
            if (line.Equals("3") || !(line.Substring(0, 1).Equals("+") || line.Substring(0, 1).Equals("-")))
            {
                return null;
            }

            string icon = line.Substring(1, line.IndexOf(':') - 1);

            line = line.Substring(line.IndexOf(':') + 2);

            string[] splits = line.Split(' ');

            if (splits.Length <= 1)
            {
                return null;
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

            return marker;
        }

        /// <summary>CSV line: x,y,mapindex,name of marker,iconname,color[,zoom].</summary>
        internal static WMapMarker ParseCsvMarker(string line)
        {
            string[] splits = line.Split(',');

            if (splits.Length <= 1)
            {
                return null;
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

            return marker;
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

            // a comma would shift the CSV fields of the saved line
            if (string.IsNullOrWhiteSpace(markerName) || markerName.Contains(','))
            {
                GameActions.Print(World, ResGumps.InvalidMarkerName, 0x2A);

                return;
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

                        try
                        {
                            tempList.Add(ParseMarker(splits));
                        }
                        catch (Exception e) when (IsBadMarkerLine(e))
                        {
                            Log.Warn($"world map markers: {UserMarkersFilePath}: {e.Message}");
                        }
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
