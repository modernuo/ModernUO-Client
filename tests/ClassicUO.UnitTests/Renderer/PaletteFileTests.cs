using System;
using System.IO;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // anim/palettes.bin is untrusted input that sizes a GPU buffer (Codex audit P2s on poc/mem-reduce): the counts, the
    // payload extent and the palette ids are validated before anything is allocated (AnimPaletteFile.ReadLayout,
    // Renderer.PaletteUploadSize).
    public sealed class PaletteFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuo-palettes-" + Guid.NewGuid().ToString("N"));

        public PaletteFileTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        // header, the two counts as written (whatever they are), the frame table, then paletteBytes of payload
        private string Write(int paletteCount, int frameCount, uint[] frames, int paletteBytes)
        {
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bin");
            using var fs = File.Create(path);
            StructIo.WriteHeader(fs, AnimConst.MagicPalettes);
            StructIo.Write(fs, paletteCount);
            StructIo.Write(fs, frameCount);
            StructIo.WriteSpan<uint>(fs, frames);
            fs.Write(new byte[paletteBytes]);
            return path;
        }

        private const int PaletteBytes = AnimPaletteFile.Entries * 2;

        [Fact]
        public void A_valid_file_reads_with_its_layout()
        {
            var path = Write(2, 3, [0, uint.MaxValue, 1], 2 * PaletteBytes);
            var fp = AnimPaletteFile.ReadFramePalette(path, out int count, out long offset);
            fp.Should().Equal(0u, uint.MaxValue, 1u);
            count.Should().Be(2);
            var l = AnimPaletteFile.ReadLayout(path);
            l.PaletteOffset.Should().Be(offset);
            l.PaletteBytes.Should().Be(2 * PaletteBytes);
            offset.Should().Be(new FileInfo(path).Length - 2 * PaletteBytes);
            var (_, pal) = AnimPaletteFile.Read(path);
            pal.Length.Should().Be(2 * AnimPaletteFile.Entries);
        }

        [Fact]
        public void A_count_that_wraps_to_a_small_buffer_is_rejected()
        {
            // the audit's example: 8,388,609 palettes are 4,294,967,808 bytes, which narrowed to uint/int is 512
            var path = Write(8_388_609, 0, [], PaletteBytes);
            FluentActions.Invoking(() => AnimPaletteFile.ReadLayout(path)).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => AnimPaletteFile.ReadFramePalette(path, out _, out _)).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => Uo.Render.Renderer.PaletteUploadSize(8_388_609, 24, long.MaxValue))
                .Should().Throw<InvalidDataException>("over the explicit byte limit even if the file were that long");
        }

        [Fact]
        public void Negative_counts_are_rejected()
        {
            FluentActions.Invoking(() => AnimPaletteFile.ReadLayout(Write(-1, 0, [], 0))).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => AnimPaletteFile.ReadLayout(Write(1, -1, [], PaletteBytes))).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => Uo.Render.Renderer.PaletteUploadSize(-1, 24, 1000)).Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void A_frame_table_longer_than_the_file_is_rejected()
        {
            // claims 1,000 frames but holds 2
            var path = Write(1, 1000, [0, 0], PaletteBytes);
            FluentActions.Invoking(() => AnimPaletteFile.ReadLayout(path)).Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void A_truncated_payload_is_rejected_before_any_upload()
        {
            // the header says 4 palettes; the file holds 3 and a half: previously ReadExactly threw after the GPU buffer
            // and the mapped transfer buffer were created
            var path = Write(4, 1, [3], 3 * PaletteBytes + PaletteBytes / 2);
            FluentActions.Invoking(() => AnimPaletteFile.ReadLayout(path)).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => AnimPaletteFile.Read(path)).Should().Throw<InvalidDataException>();

            // a file that shrank after the pack was loaded: the renderer's own check against the open file's length
            long offset = 16 + 8 + 4;
            FluentActions.Invoking(() => Uo.Render.Renderer.PaletteUploadSize(4, offset, offset + 3 * PaletteBytes))
                .Should().Throw<InvalidDataException>();
            Uo.Render.Renderer.PaletteUploadSize(4, offset, offset + 4 * PaletteBytes).Should().Be((4L * PaletteBytes, (uint)(4 * PaletteBytes)));
            Uo.Render.Renderer.PaletteUploadSize(0, offset, offset).Should().Be((0L, 4u), "an empty table still gets a 4-byte buffer");
        }

        [Fact]
        public void Palette_ids_past_the_count_are_rejected()
        {
            var path = Write(2, 3, [0, 2, 1], 2 * PaletteBytes);
            FluentActions.Invoking(() => AnimPaletteFile.ReadFramePalette(path, out _, out _)).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => AnimPaletteFile.ValidateFramePalette([0, uint.MaxValue, 1], 2)).Should().NotThrow();
            FluentActions.Invoking(() => AnimPaletteFile.ValidateFramePalette([5], 2)).Should().Throw<InvalidDataException>();
        }
    }
}
