// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using ClassicUO.Utility;
using ClassicUO.Configuration;
using ClassicUO.Resources;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    internal sealed class InfoBarManager
    {
        private readonly World _world;
        private readonly List<InfoBarItem> infoBarItems;

        public InfoBarManager(World world)
        {
            infoBarItems = new List<InfoBarItem>();
            _world = world;
        }

        public List<InfoBarItem> GetInfoBars()
        {
            return infoBarItems;
        }

        public static string[] GetVars()
        {
            return Enum.GetNames(typeof(InfoBarVars));
        }

        public void AddItem(InfoBarItem ibi)
        {
            infoBarItems.Add(ibi);
        }

        public void RemoveItem(InfoBarItem item)
        {
            infoBarItems.Remove(item);
        }

        public void Clear()
        {
            infoBarItems.Clear();
        }

        private bool _loadFailed;

        public void Save() => Save(Path.Combine(ProfileManager.ProfilePath, "infobar.xml"));

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
                xml.WriteStartElement("infos");

                foreach (InfoBarItem info in infoBarItems)
                {
                    info.Save(xml);
                }

                xml.WriteEndElement();
                xml.WriteEndDocument();
                xml.Flush();
                atomic.Commit();
            }
        }

        public void Load() => Load(Path.Combine(ProfileManager.ProfilePath, "infobar.xml"));

        internal void Load(string path)
        {
            if (!File.Exists(path))
            {
                CreateDefault();
                Save(path);

                return;
            }

            XmlDocument doc = new XmlDocument();

            try
            {
                AtomicFile.LoadXml(doc, path);
            }
            catch (Exception ex)
            {
                // keep the file: the next Save() backs it up before replacing it
                Log.Error(ex.ToString());
                _loadFailed = true;

                return;
            }

            infoBarItems.Clear();

            XmlElement root = doc["infos"];

            if (root != null)
            {
                foreach (XmlElement xml in root.GetElementsByTagName("info"))
                {
                    InfoBarItem item = new InfoBarItem(xml);
                    infoBarItems.Add(item);
                }
            }
        }

        public void CreateDefault()
        {
            infoBarItems.Clear();

            infoBarItems.Add(new InfoBarItem("", InfoBarVars.NameNotoriety, 0x3D2));
            infoBarItems.Add(new InfoBarItem(ResGeneral.Hits, InfoBarVars.HP, 0x1B6));
            infoBarItems.Add(new InfoBarItem(ResGeneral.Mana, InfoBarVars.Mana, 0x1ED));
            infoBarItems.Add(new InfoBarItem(ResGeneral.Stam, InfoBarVars.Stamina, 0x22E));
            infoBarItems.Add(new InfoBarItem(ResGeneral.Weight, InfoBarVars.Weight, 0x3D2));
        }
    }

    internal enum InfoBarVars
    {
        HP = 0,
        Mana,
        Stamina,
        Weight,
        Followers,
        Gold,
        Damage,
        Armor,
        Luck,
        FireResist,
        ColdResist,
        PoisonResist,
        EnergyResist,
        LowerReagentCost,
        SpellDamageInc,
        FasterCasting,
        FasterCastRecovery,
        HitChanceInc,
        DefenseChanceInc,
        LowerManaCost,
        DamageChanceInc,
        SwingSpeedInc,
        StatsCap,
        NameNotoriety,
        TithingPoints
    }

    internal class InfoBarItem
    {
        public InfoBarItem(string label, InfoBarVars var, ushort labelColor)
        {
            this.label = label;
            this.var = var;
            hue = labelColor;
        }


        public InfoBarItem(XmlElement xml)
        {
            if (xml == null)
            {
                return;
            }

            label = xml.GetAttribute("text");
            var = (InfoBarVars) int.Parse(xml.GetAttribute("var"));
            hue = ushort.Parse(xml.GetAttribute("hue"));
        }

        public ushort hue;

        public string label;
        public InfoBarVars var;

        public void Save(XmlTextWriter writer)
        {
            writer.WriteStartElement("info");
            writer.WriteAttributeString("text", label);
            writer.WriteAttributeString("var", ((int) var).ToString());
            writer.WriteAttributeString("hue", hue.ToString());
            writer.WriteEndElement();
        }
    }
}