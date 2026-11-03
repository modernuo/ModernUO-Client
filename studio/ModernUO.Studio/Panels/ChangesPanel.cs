using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>
    /// The change set (studio/UX.md, "Editing model"): every map edit since the pack was opened, newest first, each
    /// linked to its tile; undo and redo; and saving the result as a new pack.
    /// </summary>
    internal sealed class ChangesPanel : Panel
    {
        private bool _openSave;
        private string _target = "";
        private Task<PackSaver.Result> _saving;
        private volatile string _progress = "";
        private string _error;
        private PackSaver.Result _saved;

        public ChangesPanel(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Changes###changes";
        public override string MenuName => "Changes";
        public override string Icon => Icons.History;
        public override string MenuGroup => "Map";
        protected override Vector2 DefaultSize => new(420, 400);

        public override void OnPackChanged()
        {
            _saved = null;
            _error = null;
        }

        /// <summary>Opens the save dialog (File > Save as New Pack, Ctrl+Shift+S).</summary>
        public void RequestSave()
        {
            if (Context.Assets == null || Context.Assets.Edits.Dirty == 0)
            {
                return;
            }

            Focus();
            _openSave = true;
        }

        protected override void DrawContent()
        {
            var edits = Context.Assets.Edits;

            if (Ui.IconButton("undo", Icons.Undo, edits.CanUndo ? $"Undo {edits.NextUndo.Label} (Ctrl+Z)" : "Nothing to undo", enabled: edits.CanUndo))
            {
                edits.Undo();
            }

            ImGui.SameLine(0f, 2f);

            if (Ui.IconButton("redo", Icons.Redo, edits.CanRedo ? $"Redo {edits.NextRedo.Label} (Ctrl+Y)" : "Nothing to redo", enabled: edits.CanRedo))
            {
                edits.Redo();
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(edits.Dirty > 0 ? Theme.Gold : Theme.TextMuted, edits.Dirty == 0 ? "No unsaved changes" : $"{edits.Dirty} unsaved change{(edits.Dirty == 1 ? "" : "s")}");

            ImGui.SameLine();
            float saveWidth = ImGui.CalcTextSize($"{Icons.Save}  Save as new pack").X + 24f;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - saveWidth));

            if (edits.Dirty == 0)
            {
                ImGui.BeginDisabled();
            }

            if (Ui.Button($"{Icons.Save}  Save as new pack", ButtonKind.Primary, "Ctrl+Shift+S"))
            {
                RequestSave();
            }

            if (edits.Dirty == 0)
            {
                ImGui.EndDisabled();
            }

            if (_saved != null)
            {
                DrawSaved();
            }

            if (Ui.BeginCard("history", Vector2.Zero, padded: false, sunken: true))
            {
                if (edits.History.Count == 0)
                {
                    EmptyState(Icons.History, "No changes yet", "Edit the map: pick a tool on the map's toolbar (V A R L F P X).");
                }

                var draw = ImGui.GetWindowDrawList();

                for (int i = edits.History.Count - 1; i >= 0; i--)
                {
                    var c = edits.History[i];
                    ImGui.PushID(i);
                    Vector2 min = ImGui.GetCursorScreenPos();
                    var size = new Vector2(ImGui.GetContentRegionAvail().X, 44f);

                    if (ImGui.InvisibleButton("change", size) && c.X >= 0)
                    {
                        Context.ShowOnMap(c.Facet, c.X, c.Y);
                    }

                    bool hovered = ImGui.IsItemHovered();
                    float t = Ui.Animate(ImGui.GetItemID(), hovered ? 1f : 0f);
                    draw.AddRectFilled(min, min + size, Theme.U32(Theme.Mix(Theme.Base, Theme.Surface, t)), Theme.Rounding);
                    draw.AddText(min + new Vector2(12f, 7f), Theme.U32(i == edits.History.Count - 1 ? Theme.Gold : Theme.TextMuted), Icons.ChevronRight);
                    draw.AddText(min + new Vector2(32f, 5f), Theme.U32(Theme.Text), c.Label);
                    draw.AddText(min + new Vector2(32f, 24f), Theme.U32(Theme.TextMuted),
                        $"{c.Tiles} tile{(c.Tiles == 1 ? "" : "s")}  ·  {MapData.FacetName(c.Facet)} {c.X}, {c.Y}  ·  {c.Time:HH:mm:ss}");

                    if (hovered)
                    {
                        Ui.Tooltip("Show on the map");
                    }

                    ImGui.PopID();
                }
            }

            Ui.EndCard();

            DrawSaveDialog(edits);
        }

        private void DrawSaved()
        {
            ImGui.Dummy(new Vector2(0f, 2f));
            Ui.Chip($"{Icons.CircleCheck}  Saved to {_saved.Root}", Theme.Green);
            ImGui.SameLine();

            if (Ui.Button($"{Icons.FolderOpen}  Open it", ButtonKind.Secondary))
            {
                string root = _saved.Root;
                _saved = null;
                Context.OpenPack(root);
            }
        }

        private void DrawSaveDialog(MapEdits edits)
        {
            if (_openSave)
            {
                _openSave = false;
                _error = null;
                // next to the source pack, named after it and the time: a new folder never collides with a published pack
                string root = Context.Assets.Root.TrimEnd('\\', '/');
                _target = Path.Combine(Path.GetDirectoryName(root) ?? root, $"{Path.GetFileName(root)}-edit-{DateTime.Now:yyyyMMdd-HHmm}");
                ImGui.OpenPopup("Save as new pack");
            }

            ImGui.SetNextWindowSize(new Vector2(560f, 0f), ImGuiCond.Appearing);

            bool open = true;

            if (!ImGui.BeginPopupModal("Save as new pack", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            {
                return;
            }

            var regions = edits.EditedRegions().ToList();
            Ui.SectionLabel("Save as new pack");
            ImGui.PushTextWrapPos(520f);
            Ui.Muted("The open pack is never changed. The new pack links every unchanged file to it (or copies them), "
                + "rewrites the edited map regions and updates the manifest.");
            ImGui.PopTextWrapPos();
            ImGui.Dummy(new Vector2(0f, 4f));

            Ui.Chip($"{edits.Dirty} change{(edits.Dirty == 1 ? "" : "s")}", Theme.Gold);
            ImGui.SameLine();
            Ui.Chip($"{regions.Count} region file{(regions.Count == 1 ? "" : "s")} rewritten", Theme.Blue);

            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.SectionLabel("New pack folder");
            ImGui.SetNextItemWidth(520f);
            bool busy = _saving is { IsCompleted: false };

            if (busy)
            {
                ImGui.BeginDisabled();
            }

            ImGui.InputText("##target", ref _target, 1024);

            if (busy)
            {
                ImGui.EndDisabled();
            }

            if (_error != null)
            {
                ImGui.PushTextWrapPos(520f);
                ImGui.TextColored(Theme.Red, $"{Icons.Alert}  {_error}");
                ImGui.PopTextWrapPos();
            }

            if (busy)
            {
                ImGui.TextColored(Theme.Gold, $"{Icons.Loader}  {_progress}");
            }

            ImGui.Dummy(new Vector2(0f, 6f));

            if (_saving is { IsCompleted: true })
            {
                if (_saving.IsCompletedSuccessfully)
                {
                    _saved = _saving.Result;
                    edits.MarkSaved();
                    StudioLog.Info($"Saved {_saved.Root}: {_saved.Regions} region(s) rewritten, {_saved.Linked:N0} file(s) linked, {_saved.Copied:N0} copied");
                    ImGui.CloseCurrentPopup();
                }
                else
                {
                    _error = _saving.Exception?.GetBaseException().Message ?? "the save failed";
                    StudioLog.Error($"Save failed: {_error}");
                }

                _saving = null;
            }

            if (!busy && Ui.Button($"{Icons.Save}  Save", ButtonKind.Primary))
            {
                var assets = Context.Assets;
                string target = _target.Trim().Trim('"');
                _error = null;
                _progress = "Starting";
                _saving = Task.Run(() => PackSaver.Save(assets, target, p => _progress = p));
            }

            ImGui.SameLine();

            if (!busy && Ui.Button("Cancel", ButtonKind.Ghost))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }
}
