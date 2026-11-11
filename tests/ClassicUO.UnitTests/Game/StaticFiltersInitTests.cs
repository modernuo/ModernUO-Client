using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Game.Data;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    // First-run init of cave.txt, vegetation.txt and tree.txt: several instances starting together must end with the same
    // complete files, whatever the interleaving.
    public sealed class StaticFiltersInitTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuo-static-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        // tree tiles from 0x0D70 up count as impassable: some go to tree.txt, the passable rest become vegetation
        private static bool Impassable(ushort g) => g >= 0x0D70 && g <= 0x0D72;

        private static string[] Lines(string path) => File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();

        private void AssertComplete()
        {
            string[] veg = Lines(Path.Combine(_dir, "vegetation.txt"));

            veg.Should().Contain("3221", "0x0C95 is a passable tree tile, so it is vegetation");
            veg.Should().NotContain("3440", "0x0D70 is impassable");
            Lines(Path.Combine(_dir, "tree.txt")).Should().Contain("3440=1");
            File.Exists(Path.Combine(_dir, "cave.txt")).Should().BeTrue();
            Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
        }

        [Fact]
        public void A_fresh_directory_gets_vegetation_with_the_tree_entries_and_a_tree_file()
        {
            StaticFilters.EnsureDefaultFiles(_dir, Impassable);

            AssertComplete();
        }

        [Fact]
        public void A_vegetation_file_without_tree_entries_gets_them_added_once_judged_by_content()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "vegetation.txt"), "3397\r\n0x0D46\r\n");

            StaticFilters.EnsureDefaultFiles(_dir, Impassable);

            string[] veg = Lines(Path.Combine(_dir, "vegetation.txt"));

            veg.Should().StartWith("3397", "0x0D46");
            veg.Should().Contain("3221");
            veg.Where(l => l == "3397").Should().ContainSingle();
            AssertComplete();
        }

        [Fact]
        public void A_tree_file_already_present_leaves_both_files_alone()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "vegetation.txt"), "1\r\n");
            File.WriteAllText(Path.Combine(_dir, "tree.txt"), "2=1\r\n");

            StaticFilters.EnsureDefaultFiles(_dir, Impassable);

            File.ReadAllText(Path.Combine(_dir, "vegetation.txt")).Should().Be("1\r\n");
            File.ReadAllText(Path.Combine(_dir, "tree.txt")).Should().Be("2=1\r\n");
        }

        [Fact]
        public void Parallel_repairs_of_an_incomplete_vegetation_file_add_each_entry_once()
        {
            var wanted = Enumerable.Range(3000, 40).Select(i => (ushort)i).ToList();

            for (int round = 0; round < 10; round++)
            {
                string file = Path.Combine(_dir, "veg" + round + ".txt");
                Directory.CreateDirectory(_dir);
                File.WriteAllText(file, "3000\r\n3001\r\n");
                var start = new ManualResetEventSlim();

                Task[] instances = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    StaticFilters.AppendMissingVegetation(file, wanted);
                }, TaskCreationOptions.LongRunning)).ToArray();

                start.Set();
                Task.WaitAll(instances);

                string[] lines = Lines(file);

                lines.Should().OnlyHaveUniqueItems();
                lines.Should().HaveCount(40);
            }
        }

        [Fact]
        public void Instances_started_together_converge_on_complete_files()
        {
            // the original data lists a few graphics in both base and tree vegetation, so compare with one instance's files
            string baseDir = Path.Combine(_dir, "base");
            StaticFilters.EnsureDefaultFiles(baseDir, Impassable);
            string[] expectedVeg = Lines(Path.Combine(baseDir, "vegetation.txt"));
            string[] expectedTree = Lines(Path.Combine(baseDir, "tree.txt"));

            for (int round = 0; round < 5; round++)
            {
                string dir = Path.Combine(_dir, "r" + round);
                var start = new ManualResetEventSlim();

                Task[] instances = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    StaticFilters.EnsureDefaultFiles(dir, Impassable);
                }, TaskCreationOptions.LongRunning)).ToArray();

                start.Set();
                Task.WaitAll(instances);

                Lines(Path.Combine(dir, "vegetation.txt")).Should().Equal(expectedVeg);
                Lines(Path.Combine(dir, "tree.txt")).Should().Equal(expectedTree);
                Directory.GetFiles(dir, "*.tmp").Should().BeEmpty();
            }
        }
    }
}
