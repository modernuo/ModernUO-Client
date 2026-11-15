using System;
using System.IO;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    // A failed load (unreadable or unparsable file) must never lead to a save that destroys the user's file: defaults stay in
    // memory, and the first later save keeps a .bak copy of what was on disk.
    public sealed class XmlLoadFailureTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuo-xmlfail-" + Guid.NewGuid().ToString("N"));

        public XmlLoadFailureTests() => Directory.CreateDirectory(_dir);

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

        private const string Custom = "<skillsgroups><group name=\"Mine\"><skillids><skill id=\"3\" /></skillids></group></skillsgroups>";

        [Fact]
        public void A_skills_file_that_fails_to_parse_is_not_overwritten_by_the_load()
        {
            string path = Path.Combine(_dir, "skillsgroups.xml");
            File.WriteAllText(path, "<skillsgroups><group name=");

            var manager = new SkillsGroupManager(null);
            manager.Load(path);

            File.ReadAllText(path).Should().Be("<skillsgroups><group name=");
            manager.Groups.Should().NotBeEmpty("the defaults are kept in memory");
            Directory.GetFiles(_dir).Should().ContainSingle();
        }

        [Fact]
        public void A_skills_file_that_cannot_be_opened_is_not_overwritten_by_the_load()
        {
            if (!OperatingSystem.IsWindows())
            {
                return; // an exclusive lock only blocks readers on Windows
            }

            string path = Path.Combine(_dir, "skillsgroups.xml");
            File.WriteAllText(path, Custom);

            var manager = new SkillsGroupManager(null);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                manager.Load(path);
            }

            File.ReadAllText(path).Should().Be(Custom);
            manager.Groups.Should().NotBeEmpty();
        }

        [Fact]
        public void The_first_save_after_a_failed_load_keeps_a_backup_of_the_file()
        {
            string path = Path.Combine(_dir, "skillsgroups.xml");
            File.WriteAllText(path, "not xml at all");

            var manager = new SkillsGroupManager(null);
            manager.Load(path);
            manager.Save(path);

            File.ReadAllText(path + ".bak").Should().Be("not xml at all");
            File.ReadAllText(path).Should().Contain("<skillsgroups");

            File.WriteAllText(path, "<skillsgroups />");
            manager.Save(path);

            File.ReadAllText(path + ".bak").Should().Be("not xml at all", "an existing backup is kept");
        }

        [Fact]
        public void A_save_that_cannot_back_up_the_failed_file_leaves_it_alone_and_retries_the_backup_next_time()
        {
            if (!OperatingSystem.IsWindows())
            {
                return; // an exclusive lock only blocks readers on Windows
            }

            string path = Path.Combine(_dir, "skillsgroups.xml");
            File.WriteAllText(path, Custom);

            var manager = new SkillsGroupManager(null);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                manager.Load(path);
                manager.Save(path);
            }

            File.ReadAllText(path).Should().Be(Custom, "the save was skipped, the file was not preserved");
            File.Exists(path + ".bak").Should().BeFalse();

            manager.Save(path);

            File.ReadAllText(path + ".bak").Should().Be(Custom, "the lock is gone: the next save backs the file up first");
            File.ReadAllText(path).Should().NotBe(Custom);
        }

        [Fact]
        public void A_good_load_followed_by_a_save_makes_no_backup()
        {
            string path = Path.Combine(_dir, "skillsgroups.xml");
            File.WriteAllText(path, Custom);

            var manager = new SkillsGroupManager(null);
            manager.Load(path);
            manager.Groups.Should().ContainSingle().Which.Name.Should().Be("Mine");
            manager.Save(path);

            File.Exists(path + ".bak").Should().BeFalse();
        }

        [Fact]
        public void A_missing_skills_file_still_gets_the_defaults_written()
        {
            string path = Path.Combine(_dir, "skillsgroups.xml");

            new SkillsGroupManager(null).Load(path);

            File.Exists(path).Should().BeTrue();
        }

        [Fact]
        public void An_infobar_file_that_fails_to_parse_is_backed_up_before_the_next_save()
        {
            string path = Path.Combine(_dir, "infobar.xml");
            File.WriteAllText(path, "<infos><info");

            var manager = new InfoBarManager(null);
            manager.Load(path);

            File.ReadAllText(path).Should().Be("<infos><info");

            manager.Save(path);

            File.ReadAllText(path + ".bak").Should().Be("<infos><info");
        }
    }
}
