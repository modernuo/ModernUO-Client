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
        private void BuildGump()
        {
            BuildContextMenu();
        }

        private void BuildOptionDictionary()
        {
            _options.Clear();

            _options["show_all_markers"] = new ContextMenuItemEntry(ResGumps.ShowAllMarkers, () => { _showMarkers = !_showMarkers; SaveSettings(); }, true, _showMarkers);
            _options["show_marker_names"] = new ContextMenuItemEntry(ResGumps.ShowMarkerNames, () => { _showMarkerNames = !_showMarkerNames; SaveSettings(); }, true, _showMarkerNames);
            _options["show_marker_icons"] = new ContextMenuItemEntry(ResGumps.ShowMarkerIcons, () => { _showMarkerIcons = !_showMarkerIcons; SaveSettings(); }, true, _showMarkerIcons);
            _options["flip_map"] = new ContextMenuItemEntry(ResGumps.FlipMap, () => { _flipMap = !_flipMap; SaveSettings(); }, true, _flipMap);

            _options["goto_location"] = new ContextMenuItemEntry
            (
                ResGumps.GotoLocation,
                () => UIManager.Add(new LocationGoGump(World, (x, y) => GoToMarker(x, y, true)))
            );

            _options["top_most"] = new ContextMenuItemEntry(ResGumps.TopMost, () => { TopMost = !TopMost; }, true, _isTopMost);

            _options["free_view"] = new ContextMenuItemEntry(ResGumps.FreeView, () => { FreeView = !FreeView; }, true, FreeView);

            for (int i = 0; i < MapLoader.MAPS_COUNT; i++)
            {
                var idx = i;

                _options[$"free_view_map_{idx}"] = new ContextMenuItemEntry
                (
                    string.Format(ResGumps.WorldMapChangeMap0, idx), () =>
                    {
                        FreeView = true;
                        ChangeMap(idx);
                    }
                );
            }

            _options["show_party_members"] = new ContextMenuItemEntry
            (
                ResGumps.ShowPartyMembers,
                () =>
                {
                    _showPartyMembers = !_showPartyMembers;

                    World.WMapManager.SetEnable(_showPartyMembers);
                    SaveSettings();
                },
                true,
                _showPartyMembers
            );

            _options["show_mobiles"] = new ContextMenuItemEntry(ResGumps.ShowMobiles, () => { _showMobiles = !_showMobiles; SaveSettings(); }, true, _showMobiles);

            _options["show_multis"] = new ContextMenuItemEntry(ResGumps.ShowHousesBoats, () => { _showMultis = !_showMultis; SaveSettings(); }, true, _showMultis);

            _options["show_your_name"] = new ContextMenuItemEntry(ResGumps.ShowYourName, () => { _showPlayerName = !_showPlayerName; SaveSettings(); }, true, _showPlayerName);

            _options["show_your_healthbar"] = new ContextMenuItemEntry(ResGumps.ShowYourHealthbar, () => { _showPlayerBar = !_showPlayerBar; SaveSettings(); }, true, _showPlayerBar);

            _options["show_party_name"] = new ContextMenuItemEntry(ResGumps.ShowGroupName, () => { _showGroupName = !_showGroupName; SaveSettings(); }, true, _showGroupName);

            _options["show_party_healthbar"] = new ContextMenuItemEntry(ResGumps.ShowGroupHealthbar, () => { _showGroupBar = !_showGroupBar; SaveSettings(); }, true, _showGroupBar);

            _options["show_coordinates"] = new ContextMenuItemEntry(ResGumps.ShowYourCoordinates, () => { _showCoordinates = !_showCoordinates; SaveSettings(); }, true, _showCoordinates);

            _options["show_sextant_coordinates"] = new ContextMenuItemEntry(ResGumps.ShowSextantCoordinates, () => { _showSextantCoordinates = !_showSextantCoordinates; }, true, _showSextantCoordinates);

            _options["show_mouse_coordinates"] = new ContextMenuItemEntry(ResGumps.ShowMouseCoordinates, () => { _showMouseCoordinates = !_showMouseCoordinates; }, true, _showMouseCoordinates);

            _options["allow_positional_target"] = new ContextMenuItemEntry(
                ResGumps.AllowPositionalTargeting, () => { _allowPositionalTarget = !_allowPositionalTarget; SaveSettings(); }, true, _allowPositionalTarget
            );

            _options["markers_manager"] = new ContextMenuItemEntry(ResGumps.MarkersManager,
                () =>
                {
                    var mm = new MarkersManagerGump(World);

                    UIManager.Add(mm);
                }
            );

            _options["add_marker_on_player"] = new ContextMenuItemEntry(ResGumps.AddMarkerOnPlayer, () => AddMarkerOnPlayer());
            _options["saveclose"] = new ContextMenuItemEntry(ResGumps.SaveClose, Dispose);

            _options["show_grid_if_zoomed"] = new ContextMenuItemEntry(ResGumps.GridIfZoomed, () => { _showGridIfZoomed = !_showGridIfZoomed; SaveSettings();  }, true, _showGridIfZoomed);

            _options["reset_map_cache"] = new ContextMenuItemEntry(ResGumps.ResetMapsCache, () =>
            {
                // no cache folder yet, or a PNG another instance has open (Windows): log and go on
                try
                {
                    foreach (string file in Directory.GetFiles(_mapsCachePath, "*.png"))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                        {
                            Log.Warn($"world map cache: could not delete {file}: {e.Message}");
                        }
                    }
                }
                catch (DirectoryNotFoundException)
                {
                }
            }, false);
        }

        public void GoToMarker(int x, int y, bool isManualType)
        {
            FreeView = true;

            _gotoMarker = new WMapMarker
            {
                Color = Color.Aquamarine,
                MapId = _map.Index,
                Name = isManualType ? $"Go to: {x}, {y}" : "",
                X = x,
                Y = y,
                ZoomIndex = 1
            };

            _center.X = x;
            _center.Y = y;
        }

        private void BuildContextMenuForZones(ContextMenuControl parent)
        {
            ContextMenuItemEntry zoneOptions = new ContextMenuItemEntry(ResGumps.MapZoneOptions);

            zoneOptions.Add(_options["show_grid_if_zoomed"]);
            zoneOptions.Add(new ContextMenuItemEntry(ResGumps.MapZoneReload, () => { LoadZones(); BuildContextMenu(); }));
            zoneOptions.Add(new ContextMenuItemEntry(""));

            if (_zoneSets.ZoneSetDict.Count < 1)
            {
                zoneOptions.Add(new ContextMenuItemEntry(ResGumps.MapZoneNone));
            }
            else
            {
                foreach (KeyValuePair<string, ZoneSet> entry in _zoneSets.ZoneSetDict)
                {
                    string filename = entry.Key;
                    ZoneSet zoneSet = entry.Value;

                    zoneOptions.Add
                    (
                        new ContextMenuItemEntry
                        (
                            String.Format(ResGumps.MapZoneFileName, zoneSet.NiceFileName),
                            () => {
                                zoneSet.Hidden = !zoneSet.Hidden;

                                if (!zoneSet.Hidden)
                                {
                                    string hiddenFile = _hiddenZoneFiles.FirstOrDefault(x => x.Equals(filename));

                                    if (!string.IsNullOrEmpty(hiddenFile))
                                    {
                                        _hiddenZoneFiles.Remove(hiddenFile);
                                    }
                                }
                                else
                                {
                                    _hiddenZoneFiles.Add(filename);
                                }
                            },
                            true,
                            !entry.Value.Hidden
                        )
                    );
                }
            }

            parent.Add(zoneOptions);
        }

        private void BuildContextMenu()
        {
            BuildOptionDictionary();

            ContextMenu?.Dispose();
            ContextMenu = new ContextMenuControl(this);

            ContextMenuItemEntry markerFontEntry = new ContextMenuItemEntry(ResGumps.FontStyle);
            markerFontEntry.Add(new ContextMenuItemEntry(string.Format(ResGumps.Style0, 1), () => { SetFont(1); }));
            markerFontEntry.Add(new ContextMenuItemEntry(string.Format(ResGumps.Style0, 2), () => { SetFont(2); }));
            markerFontEntry.Add(new ContextMenuItemEntry(string.Format(ResGumps.Style0, 3), () => { SetFont(3); }));
            markerFontEntry.Add(new ContextMenuItemEntry(string.Format(ResGumps.Style0, 4), () => { SetFont(4); }));
            markerFontEntry.Add(new ContextMenuItemEntry(string.Format(ResGumps.Style0, 5), () => { SetFont(5); }));
            markerFontEntry.Add(new ContextMenuItemEntry(string.Format(ResGumps.Style0, 6), () => { SetFont(6); }));

            ContextMenuItemEntry markersEntry = new ContextMenuItemEntry(ResGumps.MapMarkerOptions);
            markersEntry.Add(new ContextMenuItemEntry(ResGumps.ReloadMarkers, LoadMarkers));

            markersEntry.Add(markerFontEntry);

            markersEntry.Add(_options["show_all_markers"]);
            markersEntry.Add(new ContextMenuItemEntry(""));
            markersEntry.Add(_options["show_marker_names"]);
            markersEntry.Add(_options["show_marker_icons"]);
            markersEntry.Add(new ContextMenuItemEntry(""));

            if (_markerFiles.Count > 0)
            {
                foreach (WMapMarkerFile markerFile in _markerFiles)
                {
                    ContextMenuItemEntry entry = new ContextMenuItemEntry
                    (
                        string.Format(ResGumps.ShowHide0, markerFile.Name),
                        () =>
                        {
                            markerFile.Hidden = !markerFile.Hidden;

                            if (!markerFile.Hidden)
                            {
                                string hiddenFile = _hiddenMarkerFiles.SingleOrDefault(x => x.Equals(markerFile.Name));

                                if (!string.IsNullOrEmpty(hiddenFile))
                                {
                                    _hiddenMarkerFiles.Remove(hiddenFile);
                                }
                            }
                            else
                            {
                                _hiddenMarkerFiles.Add(markerFile.Name);
                            }
                        },
                        true,
                        !markerFile.Hidden
                    );

                    _options[$"show_marker_{markerFile.Name}"] = entry;
                    markersEntry.Add(entry);
                }
            }
            else
            {
                markersEntry.Add(new ContextMenuItemEntry(ResGumps.NoMapFiles));
            }


            ContextMenu.Add(markersEntry);

            BuildContextMenuForZones(ContextMenu);

            ContextMenuItemEntry namesHpBarEntry = new ContextMenuItemEntry(ResGumps.NamesHealthbars);
            namesHpBarEntry.Add(_options["show_your_name"]);
            namesHpBarEntry.Add(_options["show_your_healthbar"]);
            namesHpBarEntry.Add(_options["show_party_name"]);
            namesHpBarEntry.Add(_options["show_party_healthbar"]);

            ContextMenu.Add(namesHpBarEntry);

            ContextMenu.Add("", null);
            ContextMenu.Add(_options["goto_location"]);
            ContextMenu.Add(_options["flip_map"]);
            ContextMenu.Add(_options["top_most"]);

            ContextMenuItemEntry freeView = new ContextMenuItemEntry(ResGumps.FreeView);
            freeView.Add(_options["free_view"]);

            for (int i = 0; i < MapLoader.MAPS_COUNT; i++)
                freeView.Add(_options[$"free_view_map_{i}"]);

            ContextMenu.Add(freeView);

            ContextMenu.Add("", null);
            ContextMenu.Add(_options["show_party_members"]);
            ContextMenu.Add(_options["show_mobiles"]);
            ContextMenu.Add(_options["show_multis"]);
            ContextMenu.Add(_options["show_coordinates"]);
            ContextMenu.Add(_options["show_sextant_coordinates"]);
            ContextMenu.Add(_options["show_mouse_coordinates"]);
            ContextMenu.Add(_options["allow_positional_target"]);
            ContextMenu.Add("", null);
            ContextMenu.Add(_options["markers_manager"]);
            ContextMenu.Add(_options["add_marker_on_player"]);
            ContextMenu.Add("", null);
            ContextMenu.Add(_options["reset_map_cache"]);
            ContextMenu.Add(_options["saveclose"]);
        }

        private void SetFont(int fontIndex)
        {
            _markerFontIndex = fontIndex;

            switch (fontIndex)
            {
                case 1:
                    _markerFont = Fonts.Map1;

                    break;

                case 2:
                    _markerFont = Fonts.Map2;

                    break;

                case 3:
                    _markerFont = Fonts.Map3;

                    break;

                case 4:
                    _markerFont = Fonts.Map4;

                    break;

                case 5:
                    _markerFont = Fonts.Map5;

                    break;

                case 6:
                    _markerFont = Fonts.Map6;

                    break;

                default:
                    _markerFontIndex = 1;
                    _markerFont = Fonts.Map1;

                    break;
            }
        }

        public void SetOptionValue(string key, bool v)
        {
            if (_options.TryGetValue(key, out ContextMenuItemEntry entry) && entry != null)
            {
                entry.IsSelected = v;
            }
        }
    }
}
