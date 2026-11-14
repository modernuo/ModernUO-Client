// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using Uo.Pack;
using ClassicUO.Utility;
using ClassicUO.Configuration;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Resources;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    internal sealed class SkillsGroup
    {
        private readonly byte[] _list = new byte[60];

        public SkillsGroup()
        {
            for (int i = 0; i < _list.Length; i++)
            {
                _list[i] = 0xFF;
            }
        }

        public SkillsGroup Left { get; set; }
        public SkillsGroup Right { get; set; }
        public int Count;
        public bool IsMaximized;
        public string Name = ResGeneral.NoName;

        public byte GetSkill(int index)
        {
            if (index < 0 || index >= Count)
            {
                return 0xFF;
            }

            return _list[index];
        }

        public void Add(byte item)
        {
            if (!Contains(item))
            {
                _list[Count++] = item;
            }
        }

        public void Remove(byte item)
        {
            bool removed = false;

            for (int i = 0; i < Count; i++)
            {
                if (_list[i] == item)
                {
                    removed = true;

                    for (; i < Count - 1; i++)
                    {
                        _list[i] = _list[i + 1];
                    }

                    break;
                }
            }

            if (removed)
            {
                Count--;

                if (Count < 0)
                {
                    Count = 0;
                }

                _list[Count] = 0xFF;
            }
        }

        public bool Contains(byte item)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_list[i] == item)
                {
                    return true;
                }
            }

            return false;
        }

        // the catalog is looked up only when there is one (tests and an empty catalog have no Client.Game)
        public void Sort() => SortBy(SkillsGroupManager.SkillsCount, i => Client.Game.UO.FileManager.Skills.GetSortedIndex(i));

        /// <summary>
        /// Orders the group's skills by the catalog's sorted order. Skills the catalog does not list (it is shorter, or
        /// empty before a pack or server provides it) keep their ids, after the sorted ones in their old order, so a
        /// profile saved afterwards never loses or invents a skill id.
        /// </summary>
        internal unsafe void SortBy(int skillsCount, Func<int, int> sortedIndex)
        {
            byte* table = stackalloc byte[60];
            // SkipLocalsInit may leave stackalloc uninitialized
            Span<bool> used = stackalloc bool[60];
            used.Clear();
            int index = 0;

            for (int i = 0; i < skillsCount && index < Count; i++)
            {
                int skill = sortedIndex(i);

                for (int j = 0; j < Count; j++)
                {
                    if (!used[j] && skill == _list[j])
                    {
                        table[index++] = _list[j];
                        used[j] = true;

                        break;
                    }
                }
            }

            for (int j = 0; j < Count; j++)
            {
                if (!used[j])
                {
                    table[index++] = _list[j];
                }
            }

            for (int j = 0; j < Count; j++)
            {
                _list[j] = table[j];
            }
        }

        public void TransferTo(SkillsGroup group)
        {
            for (int i = 0; i < Count; i++)
            {
                group.Add(_list[i]);
            }

            group.Sort();
        }

        public void Save(XmlTextWriter xml)
        {
            xml.WriteStartElement("group");
            xml.WriteAttributeString("name", Name);
            xml.WriteStartElement("skillids");

            for (int i = 0; i < Count; i++)
            {
                byte idx = GetSkill(i);

                if (idx != 0xFF)
                {
                    xml.WriteStartElement("skill");
                    xml.WriteAttributeString("id", idx.ToString());
                    xml.WriteEndElement();
                }
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
        }
    }

    internal sealed class SkillsGroupManager
    {
        private readonly World _world;

        public SkillsGroupManager(World world) => _world = world;

        // zero without a running client (unit tests build the defaults headless)
        internal static int SkillsCount => Client.Game?.UO?.FileManager?.Skills?.SkillsCount ?? 0;


        public readonly List<SkillsGroup> Groups = new List<SkillsGroup>();


        public void Add(SkillsGroup g)
        {
            Groups.Add(g);
        }

        public bool Remove(SkillsGroup g)
        {
            if (Groups[0] == g)
            {
                var camera = Client.Game.Scene.Camera;

                MessageBoxGump messageBox = new MessageBoxGump(_world, 200, 125, ResGeneral.CannotDeleteThisGroup, null)
                {
                    X = camera.Bounds.X + camera.Bounds.Width / 2 - 100,
                    Y = camera.Bounds.Y + camera.Bounds.Height / 2 - 62
                };

                UIManager.Add(messageBox);

                return false;
            }

            Groups.Remove(g);
            g.TransferTo(Groups[0]);

            return true;
        }

        public void Load() => Load(Path.Combine(ProfileManager.ProfilePath, "skillsgroups.xml"));

        internal void Load(string path)
        {
            Groups.Clear();

            if (!File.Exists(path))
            {
                Log.Trace("No skillsgroups.xml file. Creating a default file.");

                MakeDefault(true, path);

                return;
            }

            XmlDocument doc = new XmlDocument();

            try
            {
                AtomicFile.LoadXml(doc, path);

                XmlElement root = doc["skillsgroups"];

                if (root != null)
                {
                    foreach (XmlElement xml in root.GetElementsByTagName("group"))
                    {
                        SkillsGroup g = new SkillsGroup();
                        g.Name = xml.GetAttribute("name");

                        XmlElement xmlIdsRoot = xml["skillids"];

                        if (xmlIdsRoot != null)
                        {
                            foreach (XmlElement xmlIds in xmlIdsRoot.GetElementsByTagName("skill"))
                            {
                                g.Add(byte.Parse(xmlIds.GetAttribute("id")));
                            }
                        }

                        g.Sort();
                        Add(g);
                    }
                }
            }
            catch (Exception ex)
            {
                // the user's file stays as it is: defaults live in memory only, and the next Save() keeps a .bak of the file first
                Log.Error(ex.ToString());

                _loadFailed = true;
                MakeDefault(false, path);
            }
        }


        private bool _loadFailed;

        public void Save() => Save(Path.Combine(ProfileManager.ProfilePath, "skillsgroups.xml"));

        internal void Save(string path)
        {
            if (_loadFailed)
            {
                if (!AtomicFile.KeepBackup(path))
                {
                    // the unreadable file is the user's only copy: keep it, try the backup again on the next save
                    return;
                }

                                _loadFailed = false;
            }

            using var atomic = AtomicFile.OpenWrite(path);
            using (XmlTextWriter xml = new XmlTextWriter(atomic, Encoding.UTF8)
            {
                Formatting = Formatting.Indented,
                IndentChar = '\t',
                Indentation = 1
            })
            {
                xml.WriteStartDocument(true);
                xml.WriteStartElement("skillsgroups");

                foreach (SkillsGroup k in Groups)
                {
                    k.Save(xml);
                }

                xml.WriteEndElement();
                xml.WriteEndDocument();
                xml.Flush();
                atomic.Commit();
            }
        }


        public void MakeDefault() => MakeDefault(true, Path.Combine(ProfileManager.ProfilePath, "skillsgroups.xml"));

        internal void MakeDefault(bool save, string path)
        {
            Groups.Clear();

            string table = Client.Game?.UO?.FileManager?.Pack?.PathOf(MiscConst.SkillGroupsFile);

            if (table == null || !LoadPackTable(table))
            {
                MakeDefaultMiscellaneous();
                MakeDefaultCombat();
                MakeDefaultTradeSkills();
                MakeDefaultMagic();
                MakeDefaultWilderness();
                MakeDefaultThieving();
                MakeDefaultBard();
            }

            foreach (SkillsGroup g in Groups)
            {
                g.Sort();
            }

            if (save)
            {
                Save(path);
            }
        }

        private void MakeDefaultMiscellaneous()
        {
            SkillsGroup g = new SkillsGroup();
            g.Name = ResGeneral.Miscellaneous;
            g.Add(4);
            g.Add(6);
            g.Add(10);
            g.Add(12);
            g.Add(19);
            g.Add(3);
            g.Add(36);

            Add(g);
        }

        private void MakeDefaultCombat()
        {
            int count = SkillsCount;

            SkillsGroup g = new SkillsGroup();
            g.Name = ResGeneral.Combat;
            g.Add(1);
            g.Add(31);
            g.Add(42);
            g.Add(17);
            g.Add(41);
            g.Add(5);
            g.Add(40);
            g.Add(27);

            if (count > 57)
            {
                g.Add(57);
            }

            g.Add(43);

            if (count > 50)
            {
                g.Add(50);
            }

            if (count > 51)
            {
                g.Add(51);
            }

            if (count > 52)
            {
                g.Add(52);
            }

            if (count > 53)
            {
                g.Add(53);
            }

            Add(g);
        }

        private void MakeDefaultTradeSkills()
        {
            SkillsGroup g = new SkillsGroup();
            g.Name = ResGeneral.TradeSkills;
            g.Add(0);
            g.Add(7);
            g.Add(8);
            g.Add(11);
            g.Add(13);
            g.Add(23);
            g.Add(44);
            g.Add(45);
            g.Add(34);
            g.Add(37);

            Add(g);
        }

        private void MakeDefaultMagic()
        {
            int count = SkillsCount;

            SkillsGroup g = new SkillsGroup();
            g.Name = ResGeneral.Magic;
            g.Add(16);

            if (count > 56)
            {
                g.Add(56);
            }

            g.Add(25);
            g.Add(46);

            if (count > 55)
            {
                g.Add(55);
            }

            g.Add(26);

            if (count > 54)
            {
                g.Add(54);
            }

            g.Add(32);

            if (count > 49)
            {
                g.Add(49);
            }

            Add(g);
        }

        private void MakeDefaultWilderness()
        {
            SkillsGroup g = new SkillsGroup();
            g.Name = ResGeneral.Wilderness;
            g.Add(2);
            g.Add(35);
            g.Add(18);
            g.Add(20);
            g.Add(38);
            g.Add(39);

            Add(g);
        }

        private void MakeDefaultThieving()
        {
            SkillsGroup g = new SkillsGroup();
            g.Name = ResGeneral.Thieving;
            g.Add(14);
            g.Add(21);
            g.Add(24);
            g.Add(30);
            g.Add(48);
            g.Add(28);
            g.Add(33);
            g.Add(47);

            Add(g);
        }

        private void MakeDefaultBard()
        {
            SkillsGroup g = new SkillsGroup();
            g.Name = ResGeneral.Bard;
            g.Add(15);
            g.Add(29);
            g.Add(9);
            g.Add(22);

            Add(g);
        }

        /// <summary>
        /// The groups skillgrp.mul describes, built as LoadMULFile built them: group 0 is Miscellaneous, then one group per
        /// name; each number gives the next skill's group, a number past the last group skips the entry, and a negative
        /// one throws (the caller then uses the built-in groups).
        /// </summary>
        internal static SkillsGroup[] GroupsFromTable(string[] names, int[] assignments, int skillsCount)
        {
            byte skillidx = 0;

            SkillsGroup g = new SkillsGroup();
            g.Name = ResGeneral.Miscellaneous;

            SkillsGroup[] groups = new SkillsGroup[names.Length + 1];
            groups[0] = g;

            for (int i = 0; i < names.Length; ++i)
            {
                groups[i + 1] = new SkillsGroup
                {
                    Name = names[i]
                };
            }

            foreach (int grp in assignments)
            {
                if (grp < groups.Length && skillidx < skillsCount)
                {
                    groups[grp].Add(skillidx++);
                }
            }

            return groups;
        }

        // skillgrp.bin: skillgrp.mul's group names and per-skill group numbers as the converter read them
        private bool LoadPackTable(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var (names, assignments) = SkillGroupsFile.Read(path);

                foreach (SkillsGroup group in GroupsFromTable(names, assignments, Client.Game.UO.FileManager.Skills.SkillsCount))
                {
                    Add(group);
                }

                Log.Trace($"Skill groups: {names.Length + 1} from {path}");
            }
            catch (Exception e)
            {
                Log.Error($"Error while reading {path}, using CUO defaults! exception given is: {e}");

                return false;
            }

            return Groups.Count != 0;
        }
    }
}