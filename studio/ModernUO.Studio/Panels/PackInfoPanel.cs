using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>The open pack's manifest, its files and how each section loaded.</summary>
    internal sealed class PackInfoPanel : Panel
    {
        private bool _showFiles;

        public PackInfoPanel(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Pack Info###packinfo";
        public override string MenuName => "Pack Info";
        public override string Icon => Icons.Info;
        public override string MenuGroup => "Tools";
        protected override Vector2 DefaultSize => new(720, 560);

        protected override void DrawContent()
        {
            var assets = Context.Assets;
            var m = assets.Manifest;
            int failed = assets.Results.Count(r => r.Error != null);

            Ui.SectionLabel("Pack");
            Ui.Title(Path.GetFileName(assets.Root.TrimEnd('\\', '/')));
            Ui.MonoText(assets.Root, Theme.TextSecondary);
            ImGui.Dummy(new Vector2(0f, 2f));

            Ui.Chip($"format {m.Version}", Theme.TextSecondary);
            ImGui.SameLine();
            Ui.Chip($"client {m.SourceClientVersion}", Theme.Gold);
            ImGui.SameLine();
            Ui.Chip($"converted {m.Created:yyyy-MM-dd}", Theme.TextSecondary);

            if (m.Bodies != null)
            {
                ImGui.SameLine();
                Ui.Chip($"bodies {m.Bodies.Preset}{(m.Bodies.ForceOldAnimations ? ", old" : "")}", Theme.Purple);
            }

            ImGui.SameLine();
            Ui.Chip(failed == 0 ? $"{Icons.CircleCheck}  all {assets.Results.Count} sections loaded" : $"{Icons.Alert}  {failed} failed",
                failed == 0 ? Theme.Green : Theme.Red);

            Inspector.Section("Sections");

            if (Ui.BeginCard("sections", Vector2.Zero, autoHeight: true))
            {
                if (ImGui.BeginTable("sections", 3, ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Section", ImGuiTableColumnFlags.WidthFixed, 160f);
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 90f);
                    ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch);

                    foreach (var r in assets.Results)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextColored(r.Error == null ? Theme.Green : Theme.Red, r.Error == null ? Icons.Check : Icons.Alert);
                        ImGui.SameLine();
                        ImGui.TextUnformatted(r.Name);
                        ImGui.TableNextColumn();
                        Ui.MonoText($"{r.Milliseconds,6:0.0} ms", Theme.TextMuted);
                        ImGui.TableNextColumn();
                        ImGui.TextColored(r.Error == null ? Theme.TextSecondary : Theme.Red, r.Error ?? "loaded");
                    }

                    ImGui.EndTable();
                }
            }

            Ui.EndCard();

            Inspector.Section("Facets");

            if (Ui.BeginCard("facets", Vector2.Zero, autoHeight: true))
            {
                foreach (var facet in m.Facets)
                {
                    ImGui.PushFont(Theme.Strong, 0f);
                    ImGui.TextUnformatted($"{facet.Index}  {MapData.FacetName(facet.Index)}");
                    ImGui.PopFont();
                    ImGui.SameLine(200f);
                    ImGui.TextColored(Theme.TextSecondary, $"{facet.Width} x {facet.Height} tiles");
                    ImGui.SameLine(360f);
                    ImGui.TextColored(Theme.TextMuted, $"{facet.BlocksX} x {facet.BlocksY} blocks, {facet.RegionBlocks} per region");
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20f);

                    if (Ui.IconButton($"map{facet.Index}", Icons.Map, $"Open {MapData.FacetName(facet.Index)} on the map"))
                    {
                        var info = MapData.Info(assets, facet.Index);
                        Context.ShowOnMap(facet.Index, info.Width / 2, info.Height / 2);
                    }
                }
            }

            Ui.EndCard();

            long total = m.Files.Sum(f => f.Size);
            Inspector.Section($"Files  ·  {m.Files.Count:N0}  ·  {total / (1024.0 * 1024.0):0.0} MB");
            Ui.Toggle("Show the file list", ref _showFiles);

            if (_showFiles && Ui.BeginCard("files", new Vector2(0f, 320f), padded: false, sunken: true))
            {
                if (ImGui.BeginTable("files", 3, ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Path");
                    ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 90f);
                    ImGui.TableSetupColumn("SHA-256");
                    ImGui.TableHeadersRow();

                    ImGuiListClipper clipper = new();
                    clipper.Begin(m.Files.Count);

                    while (clipper.Step())
                    {
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            var f = m.Files[i];
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            Ui.MonoText(f.Path);
                            ImGui.TableNextColumn();
                            ImGui.TextColored(Theme.TextSecondary, $"{f.Size / 1024.0:N0} KB");
                            ImGui.TableNextColumn();
                            Ui.MonoText(f.Sha256, Theme.TextMuted);
                        }
                    }

                    clipper.End();
                    ImGui.EndTable();
                }

                Ui.EndCard();
            }
            else if (_showFiles)
            {
                Ui.EndCard();
            }
        }
    }
}
