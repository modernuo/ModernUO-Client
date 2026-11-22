using System.Reflection;
using FluentAssertions;
using StbTextEditSharp;
using Xunit;

namespace ClassicUO.UnitTests.Utility
{
    // gump-system G5: the undo buffers are made on the first edit (a text box nobody types in carries none). Typing,
    // deleting, undo and redo give the texts stb_textedit gives with eager buffers.
    public class UndoStateTests
    {
        private sealed class Box : ITextEditHandler
        {
            public string Text { get; set; } = string.Empty;
            public int Length => Text.Length;
            public TextEditRow LayoutRow(int startIndex) => new TextEditRow { num_chars = Text.Length - startIndex, x1 = Text.Length };
            public float GetWidth(int index) => 1;
        }

        private static bool HasBuffers(UndoState s) =>
            typeof(UndoState).GetField("_undoChar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s) != null
            || typeof(UndoState).GetField("_undoRec", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s) != null;

        [Fact]
        public void No_Buffers_Until_The_First_Edit()
        {
            var box = new Box();
            var edit = new TextEdit(box) { SingleLine = true };

            HasBuffers(edit.UndoState).Should().BeFalse();
            edit.Undo();
            edit.Redo();
            HasBuffers(edit.UndoState).Should().BeFalse("undo and redo with nothing recorded touch no buffer");

            edit.InputChar('a');
            HasBuffers(edit.UndoState).Should().BeTrue();
        }

        [Fact]
        public void Type_Delete_Undo_Redo()
        {
            var box = new Box();
            var edit = new TextEdit(box) { SingleLine = true };

            foreach (char c in "hello")
            {
                edit.InputChar(c);
            }

            box.Text.Should().Be("hello");
            edit.Key(ControlKeys.BackSpace);
            box.Text.Should().Be("hell");

            edit.Undo();
            box.Text.Should().Be("hello");
            edit.Undo();
            box.Text.Should().Be("hell");
            edit.Undo();
            edit.Undo();
            edit.Undo();
            edit.Undo();
            box.Text.Should().Be(string.Empty);

            edit.Redo();
            edit.Redo();
            box.Text.Should().Be("he");

            edit.InputChar('y');
            box.Text.Should().Be("hey");
            edit.Redo();
            box.Text.Should().Be("hey", "typing flushed the redo records");
        }

        [Fact]
        public void More_Records_Than_The_Buffer_Holds()
        {
            var box = new Box();
            var edit = new TextEdit(box) { SingleLine = true };

            for (int i = 0; i < 250; i++)
            {
                edit.InputChar((char)('a' + i % 26));
            }

            box.Text.Length.Should().Be(250);

            for (int i = 0; i < 250; i++)
            {
                edit.Undo();
            }

            // 99 records: the oldest edits were discarded
            box.Text.Length.Should().Be(250 - 99);
        }
    }
}
