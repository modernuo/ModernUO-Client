using System.Text;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>The cliloc string tables (text/cliloc.bin): search by number or text. Items are table indexes.</summary>
    internal sealed class ClilocBrowser : AssetBrowser
    {
        private static readonly string[] _englishModes = { "Language", "English" };

        public ClilocBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Cliloc###cliloc";
        public override string MenuName => "Cliloc";
        public override string Icon => Icons.Languages;

        private ClassicUO.Assets.ClilocLoader Clilocs => Context.Assets.Files.Clilocs;

        // with English as the language the English strings are the language table and EnglishTable stays empty
        private bool HasBoth => Clilocs.LanguageTable != null && Clilocs.EnglishTable != null && !ReferenceEquals(Clilocs.LanguageTable, Clilocs.EnglishTable);

        private Uo.Pack.ClilocTable Table => HasBoth && Mode == 1 ? Clilocs.EnglishTable : Clilocs.LanguageTable ?? Clilocs.EnglishTable;

        protected override string Kind => $"Cliloc ({Clilocs.Language})";
        protected override string[] Modes => Context.Assets != null && HasBoth ? _englishModes : null;
        protected override bool HasGrid => false;
        protected override string SearchHint => "number (500000) or text";
        protected override float DefaultInspectorWidth => 420f;
        protected override Parts InspectorParts => Parts.Properties | Parts.Actions;

        protected override int SlotCount => Context.Assets == null || Table == null ? 0 : Table.Count;

        protected override int IdOf(int index) => Table.IdAt(index);

        protected override string FormatId(int index) => Table.IdAt(index).ToString();

        protected override string CopyTextOf(int index) => Table.IdAt(index).ToString();

        /// <summary>Selects by cliloc number, not by table index.</summary>
        public override void SelectById(int number)
        {
            int index = Table?.IndexOf(number) ?? -1;

            if (index >= 0)
            {
                SelectItem(index);
            }
        }

        protected override string NameOf(int index) => Encoding.UTF8.GetString(Table.Utf8At(index));

        protected override void DrawProperties(int index)
        {
            string text = NameOf(index);

            if (Inspector.BeginProperties())
            {
                Inspector.Property("Number", Table.IdAt(index).ToString());
                Inspector.Property("Length", $"{text.Length} characters");
                Inspector.Property("Arguments", text.Contains('~') ? "yes (~1_NAME~ style)" : "none");
                Inspector.EndProperties();
            }
        }

        protected override void DrawActions(int index)
        {
            if (Ui.Button($"{Icons.Copy}  Copy number"))
            {
                ImGui.SetClipboardText(Table.IdAt(index).ToString());
            }

            ImGui.SameLine();

            if (Ui.Button($"{Icons.Clipboard}  Copy text"))
            {
                ImGui.SetClipboardText(NameOf(index));
            }
        }
    }
}
