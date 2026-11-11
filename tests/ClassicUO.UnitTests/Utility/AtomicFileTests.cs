using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace ClassicUO.UnitTests.Utility
{
    // Several client instances share profile, settings and cache files: writes replace the target in one rename,
    // readers and the writer wait for each other, and per-process names keep logs apart.
    public sealed class AtomicFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuo-atomic-" + Guid.NewGuid().ToString("N"));

        public AtomicFileTests() => Directory.CreateDirectory(_dir);

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

        // dedicated threads: a pool starved by the other test classes would delay the reader that the writer waits for
        private static Task Bg(Action a) => Task.Factory.StartNew(a, TaskCreationOptions.LongRunning);

        private string P(string name) => Path.Combine(_dir, name);

        [Fact]
        public void WriteAllText_creates_the_directory_and_leaves_no_temp_file()
        {
            string path = P("a/b/profile.json");

            AtomicFile.WriteAllText(path, "{\"x\":1}");

            File.ReadAllText(path).Should().Be("{\"x\":1}");
            Directory.GetFiles(Path.GetDirectoryName(path)).Should().ContainSingle();
        }

        [Fact]
        public void WriteAllText_has_no_byte_order_mark_and_replaces_existing_content()
        {
            string path = P("settings.json");
            File.WriteAllText(path, new string('x', 5000));

            AtomicFile.WriteAllText(path, "{}");

            File.ReadAllBytes(path).Should().Equal((byte)'{', (byte)'}');
        }

        [Fact]
        public void Writes_are_invisible_until_the_stream_is_disposed()
        {
            string path = P("macros.xml");
            AtomicFile.WriteAllText(path, "old");

            using (var stream = AtomicFile.OpenWrite(path))
            {
                stream.Write(new byte[] { (byte)'n', (byte)'e', (byte)'w' });
                stream.Flush();

                File.ReadAllText(path).Should().Be("old");
                stream.Commit();
            }

            File.ReadAllText(path).Should().Be("new");
        }

        [Fact]
        public void A_stream_disposed_without_Commit_leaves_the_target_untouched_and_removes_the_temp()
        {
            string path = P("skillsgroups.xml");
            AtomicFile.WriteAllText(path, "valid");

            using (var stream = AtomicFile.OpenWrite(path))
            {
                stream.Write(new byte[] { (byte)'x' });
            }

            File.ReadAllText(path).Should().Be("valid");
            Directory.GetFiles(_dir).Should().ContainSingle();
        }

        [Fact]
        public void An_exception_while_writing_does_not_publish_the_prefix()
        {
            string path = P("macros.xml");
            AtomicFile.WriteAllText(path, "<macros><m/></macros>");

            Action act = () =>
            {
                var atomic = AtomicFile.OpenWrite(path);

                using (var xml = new XmlTextWriter(atomic, System.Text.Encoding.UTF8))
                {
                    xml.WriteStartDocument(true);
                    xml.WriteStartElement("macros");
                    throw new InvalidOperationException("serialization failed");
                }
            };

            act.Should().Throw<InvalidOperationException>();
            File.ReadAllText(path).Should().Be("<macros><m/></macros>");
            Directory.GetFiles(_dir).Should().ContainSingle();
        }

        [Fact]
        public void A_missing_target_stays_missing_when_the_write_is_not_committed()
        {
            string path = P("new.txt");

            using (var stream = AtomicFile.OpenWrite(path))
            {
                stream.Write(new byte[] { 1 });
            }

            File.Exists(path).Should().BeFalse();
            Directory.GetFiles(_dir).Should().BeEmpty();
        }

        [Fact]
        public void A_reader_still_holding_the_old_file_keeps_a_consistent_view_while_the_replace_waits()
        {
            string path = P("gumps.xml");
            AtomicFile.WriteAllText(path, new string('a', 20000));

            var reader = AtomicFile.OpenRead(path);
            var sr = new StreamReader(reader);
            string seen = null;
            var read = Bg(() =>
            {
                Thread.Sleep(100);
                seen = sr.ReadToEnd();
                sr.Dispose();
            });

            AtomicFile.WriteAllText(path, new string('b', 20000));
            read.Wait();

            seen.Should().Be(new string('a', 20000));
            AtomicFile.ReadAllText(path).Should().Be(new string('b', 20000));
        }

        [Fact]
        public void A_reader_that_blocks_the_rename_is_waited_out()
        {
            string path = P("globalprofile.json");
            AtomicFile.WriteAllText(path, "one");

            // File.ReadAllText-style open: FileShare.Read, so on Windows the rename fails until it is closed
            var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Bg(() =>
            {
                Thread.Sleep(150);
                blocker.Dispose();
            });

            AtomicFile.WriteAllText(path, "two");
            release.Wait();

            File.ReadAllText(path).Should().Be("two");
            Directory.GetFiles(_dir).Should().ContainSingle();
        }

        [Fact]
        public void A_failed_replace_throws_and_removes_its_temp_file()
        {
            string path = P("target");
            Directory.CreateDirectory(path);

            Action act = () => AtomicFile.WriteAllText(path, "x");

            act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
            Directory.GetFiles(_dir).Should().BeEmpty();
        }

        [Fact]
        public void Concurrent_writers_and_a_reader_only_ever_see_whole_files()
        {
            string path = P("lastcharacter.json");
            AtomicFile.WriteAllText(path, new string('0', 30000));
            using var stop = new CancellationTokenSource();
            int torn = 0, reads = 0;

            var reader = Bg(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    string text = AtomicFile.ReadAllText(path);
                    Interlocked.Increment(ref reads);

                    if (text.Length != 30000 || text.Any(c => c != text[0]))
                    {
                        Interlocked.Increment(ref torn);
                    }

                    // an instance reads a profile now and then; a hot loop would starve the writers' rename retries
                    Thread.Sleep(1);
                }
            });

            var writers = Enumerable.Range(1, 4).Select(w => Bg(() =>
            {
                for (int i = 0; i < 40; i++)
                {
                    AtomicFile.WriteAllText(path, new string((char)('a' + w), 30000));
                }
            })).ToArray();

            Task.WaitAll(writers);
            stop.Cancel();
            reader.Wait();

            torn.Should().Be(0);
            reads.Should().BeGreaterThan(0);
            AtomicFile.ReadAllText(path).Should().MatchRegex("^[b-e]{30000}$");

            Directory.GetFiles(_dir).Should().ContainSingle();
        }

        [Fact]
        public void XmlTextWriter_over_OpenWrite_commits_on_dispose_and_LoadXml_reads_it()
        {
            string path = P("infobar.xml");

            var atomic = AtomicFile.OpenWrite(path);

            using (var xml = new XmlTextWriter(atomic, System.Text.Encoding.UTF8))
            {
                xml.WriteStartDocument(true);
                xml.WriteStartElement("infos");
                xml.WriteEndElement();
                xml.WriteEndDocument();
                xml.Flush();
                atomic.Commit();
            }

            var doc = new XmlDocument();
            AtomicFile.LoadXml(doc, path);

            doc.DocumentElement.Name.Should().Be("infos");
            Directory.GetFiles(_dir).Should().ContainSingle();
        }

        [Fact]
        public void CreateText_commits_on_dispose()
        {
            string path = P("chair.txt");

            using (var w = AtomicFile.CreateText(path, out var atomic))
            {
                w.WriteLine("1,2");
                w.Flush();
                atomic.Commit();
            }

            AtomicFile.ReadAllText(path).Should().Be("1,2" + Environment.NewLine);
        }

        [Fact]
        public void OpenRead_shares_with_writers_and_deleters()
        {
            string path = P("shared.txt");
            File.WriteAllText(path, "x");

            using var reader = AtomicFile.OpenRead(path);
            using var second = AtomicFile.OpenRead(path);

            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                writer.WriteByte((byte)'y');
            }

            File.Delete(path);

            File.Exists(path).Should().BeFalse();
        }

        [Fact]
        public void Pack_style_read_only_handles_coexist_and_refuse_writers()
        {
            // PackAssets opens pages with File.OpenHandle (read, FileShare.Read): any number of instances can read one pack
            string path = P("page0.bin");
            File.WriteAllBytes(path, new byte[64]);

            using SafeFileHandle a = File.OpenHandle(path);
            using SafeFileHandle b = File.OpenHandle(path);

            if (OperatingSystem.IsWindows())
            {
                Action write = () => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose();
                write.Should().Throw<IOException>();
            }

            RandomAccess.GetLength(a).Should().Be(64);
            RandomAccess.GetLength(b).Should().Be(64);
        }

        [Fact]
        public void LogFile_names_carry_the_process_id_so_instances_started_together_do_not_share_a_file()
        {
            using var log = new LogFile(_dir, "packets.log");

            Path.GetFileName(log.ToString()).Should().EndWith($"_{Environment.ProcessId}_packets.log");

            log.Write("hello");
        }
    }
}
