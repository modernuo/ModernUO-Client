// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class SkillsLoader : UOFileLoader
    {
        public SkillsLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public int SkillsCount => Skills.Count;
        public readonly List<SkillEntry> Skills = new List<SkillEntry>();
        public readonly List<SkillEntry> SortedSkills = new List<SkillEntry>();

        /// <summary>
        /// The skills from the pack's skills.bin (skills.mul + Skills.idx as the converter read them). A pack without it
        /// (the install had no skills files) leaves the list empty until the server sends one.
        /// </summary>
        public override void Load()
        {
            if (SkillsCount > 0)
            {
                return;
            }

            string path = FileManager.Pack?.PathOf(MiscConst.SkillsFile);

            if (path == null || !File.Exists(path))
            {
                Log.Warn("Skills: the pack has no skills.bin; no skill names until the server sends them (convert the client again with Uo.Converter)");

                return;
            }

            (uint Value, string Text)[] entries;

            try
            {
                entries = StringListFile.Read(path, MiscConst.MagicSkills);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Error($"Skills: cannot read '{path}' ({e.Message})");

                return;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                Skills.Add(new SkillEntry(i, entries[i].Text, (entries[i].Value & MiscConst.SkillHasAction) != 0));
            }

            SortedSkills.AddRange(Skills);
            SortedSkills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.InvariantCulture));
            Log.Trace($"Skills: {SkillsCount} from {path}");
        }

        public int GetSortedIndex(int index)
        {
            if (index < SkillsCount)
            {
                return SortedSkills[index].Index;
            }

            return -1;
        }
    }

    public class SkillEntry
    {
        public SkillEntry(int index, string name, bool hasAction)
        {
            Index = index;
            Name = name;
            HasAction = hasAction;
        }

        public bool HasAction;
        public readonly int Index;
        public string Name;

        public override string ToString()
        {
            return Name;
        }

        public enum HardCodedName
        {
            Alchemy,
            Anatomy,
            AnimalLore,
            ItemID, // T2A
            ArmsLore,
            Parrying,
            Begging,
            Blacksmith,
            Bowcraft,
            Peacemaking,
            Camping,
            Carpentry,
            Cartography,
            Cooking,
            DetectHidden,
            Enticement,
            EvaluateIntelligence,
            Healing,
            Fishing,
            ForensicEvaluation,
            Herding,
            Hiding,
            Provocation,
            Inscription,
            Lockpicking,
            Magery,
            ResistingSpells,
            Tactics,
            Snooping,
            Musicanship,
            Poisoning,
            Archery,
            SpiritSpeak,
            Stealing,
            Tailoring,
            AnimalTaming,
            TasteIdentification,
            Tinkering,
            Tracking,
            Veterinary,
            Swordsmanship,
            MaceFighting,
            Fencing,
            Wrestling,
            Lumberjacking,
            Mining,
            Meditation,
            Stealth,
            Disarm,
            Necromancy,
            Focus,
            Chivalry,
            Bushido,
            Ninjitsu,
            Spellweaving,
            Mysticism,
            Imbuing,
            Throwing
        }
    }
}