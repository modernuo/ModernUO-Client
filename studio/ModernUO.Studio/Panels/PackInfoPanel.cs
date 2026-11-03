using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>The open pack's manifest, its files and how each section loaded.</summary>
    internal sealed class PackInfoPanel : Panel
    {
        public PackInfoPanel(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Pack Info###packinfo";
        public override string MenuName => "Pack Info";
        public override string MenuGroup => "Tools";
        protected override Vector2 DefaultSize => new(720, 560);

        protected override void DrawContent()
        {
            var assets = Context.Assets;
            var m = assets.Manifest;

            ImGui.TextUnformatted(assets.Root);
            ImGui.TextUnformatted($"Format {m.Version}, converted {m.Created:yyyy-MM-dd HH:mm} from client {m.SourceClientVersion}");
            ImGui.TextColored(Widgets.Dim, $"Loaders run as client {ClassicUO.Utility.ClientVersionHelper.ToVersionString(assets.Files.Version)}");

            if (m.Bodies != null)
            {
                ImGui.TextUnformatted($"Bodies: preset {m.Bodies.Preset}, policy {m.Bodies.Policy}{(m.Bodies.ForceOldAnimations ? ", old animations forced" : "")}");
            }

            if (ImGui.CollapsingHeader("Sections", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.BeginTable("sections", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Section");
                    ImGui.TableSetupColumn("Time");
                    ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();

                    foreach (var r in assets.Results)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(r.Name);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{r.Milliseconds:0.0} ms");
                        ImGui.TableNextColumn();

                        if (r.Error == null)
                        {
                            ImGui.TextColored(Widgets.Good, "ok");
                        }
                        else
                        {
                            ImGui.TextColored(Widgets.Bad, r.Error);
                        }
                    }

                    ImGui.EndTable();
                }
            }

            if (ImGui.CollapsingHeader("Facets"))
            {
                foreach (var facet in m.Facets)
                {
                    ImGui.BulletText($"Facet {facet.Index}: {facet.Width} x {facet.Height} tiles, {facet.BlocksX} x {facet.BlocksY} blocks, {facet.RegionBlocks} blocks per region");
                }
            }

            if (ImGui.CollapsingHeader($"Files ({m.Files.Count})"))
            {
                long total = m.Files.Sum(f => f.Size);
                ImGui.TextColored(Widgets.Dim, $"{total / (1024.0 * 1024.0):0.0} MB in total");

                if (ImGui.BeginTable("files", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                        new Vector2(0, 300f)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Path");
                    ImGui.TableSetupColumn("Size");
                    ImGui.TableSetupColumn("SHA-256");
                    ImGui.TableHeadersRow();

                    foreach (var f in m.Files)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(f.Path);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{f.Size / 1024.0:0} KB");
                        ImGui.TableNextColumn();
                        ImGui.TextColored(Widgets.Dim, f.Sha256);
                    }

                    ImGui.EndTable();
                }
            }
        }
    }
}
