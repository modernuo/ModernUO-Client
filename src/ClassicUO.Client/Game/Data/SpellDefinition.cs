#region license

// Copyright (c) 2024, andreakarasho
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using System;
using System.Collections.Generic;
using ClassicUO.Game.Managers;
using ClassicUO.Resources;
using ClassicUO.Utility;

namespace ClassicUO.Game.Data
{
    internal class SpellDefinition : IEquatable<SpellDefinition>
    {
        public static SpellDefinition EmptySpell = new SpellDefinition
        (
            "",
            0,
            0,
            "",
            0,
            0,
            0
        );

        internal static Dictionary<string, SpellDefinition> WordToTargettype = new Dictionary<string, SpellDefinition>();


        public SpellDefinition
        (
            string name,
            int index,
            int gumpIconID,
            int gumpSmallIconID,
            string powerwords,
            int manacost,
            int minskill,
            int tithingcost,
            TargetType target,
            params Reagents[] regs
        )
        {
            Name = name;
            ID = index;
            GumpIconID = gumpIconID;
            GumpIconSmallID = gumpSmallIconID;
            Regs = regs;
            ManaCost = manacost;
            MinSkill = minskill;
            PowerWords = powerwords;
            TithingCost = tithingcost;
            TargetType = target;
            AddToWatchedSpell();
        }

        public SpellDefinition
        (
            string name,
            int index,
            int gumpIconID,
            string powerwords,
            int manacost,
            int minskill,
            TargetType target,
            params Reagents[] regs
        )
        {
            Name = name;
            ID = index;
            GumpIconID = gumpIconID;
            GumpIconSmallID = gumpIconID;
            Regs = regs;
            ManaCost = manacost;
            MinSkill = minskill;
            PowerWords = powerwords;
            TithingCost = 0;
            TargetType = target;
            AddToWatchedSpell();
        }

        public SpellDefinition
        (
            string name,
            int index,
            int gumpIconID,
            string powerwords,
            TargetType target,
            params Reagents[] regs
        )
        {
            Name = name;
            ID = index;
            GumpIconID = gumpIconID;
            GumpIconSmallID = gumpIconID - 0x1298;
            Regs = regs;
            ManaCost = 0;
            MinSkill = 0;
            TithingCost = 0;
            PowerWords = powerwords;
            TargetType = target;
            AddToWatchedSpell();
        }

        public bool Equals(SpellDefinition other)
        {
            return ID.Equals(other.ID);
        }

        public readonly int GumpIconID;
        public readonly int GumpIconSmallID;
        public readonly int ID;
        public readonly int ManaCost;
        public readonly int MinSkill;

        public readonly string Name;
        public readonly string PowerWords;
        public readonly Reagents[] Regs;
        public readonly TargetType TargetType;
        public readonly int TithingCost;

        private void AddToWatchedSpell()
        {
            if (!string.IsNullOrEmpty(PowerWords))
            {
                WordToTargettype[PowerWords] = this;
            }
            else if (!string.IsNullOrEmpty(Name))
            {
                WordToTargettype[Name] = this;
            }
        }


        public string CreateReagentListString(string separator)
        {
            using var sb = new ValueStringBuilder();
            for (int i = 0; i < Regs.Length; i++)
            {
                string message = Regs[i] switch
                {
                    // britanian reagents
                    Reagents.BlackPearl => ResGeneral.BlackPearl,
                    Reagents.Bloodmoss => ResGeneral.Bloodmoss,
                    Reagents.Garlic => ResGeneral.Garlic,
                    Reagents.Ginseng => ResGeneral.Ginseng,
                    Reagents.MandrakeRoot => ResGeneral.MandrakeRoot,
                    Reagents.Nightshade => ResGeneral.Nightshade,
                    Reagents.SulfurousAsh => ResGeneral.SulfurousAsh,
                    Reagents.SpidersSilk => ResGeneral.SpidersSilk,

                    // pagan reagents
                    Reagents.BatWing => ResGeneral.BatWing,
                    Reagents.GraveDust => ResGeneral.GraveDust,
                    Reagents.DaemonBlood => ResGeneral.DaemonBlood,
                    Reagents.NoxCrystal => ResGeneral.NoxCrystal,
                    Reagents.PigIron => ResGeneral.PigIron,
                    < Reagents.None => StringHelper.AddSpaceBeforeCapital(Regs[i].ToString()),
                    _ => null
                };

                if (message != null)
                {
                    sb.Append(message);
                }

                if (i < Regs.Length - 1)
                {
                    sb.Append(separator);
                }
            }

            return sb.ToString();
        }

        public static SpellDefinition FullIndexGetSpell(int fullidx)
        {
            return fullidx switch
            {
                < 1 => EmptySpell,
                > 799 => EmptySpell,
                < 100 => SpellsMagery.GetSpell(fullidx),
                < 200 => SpellsNecromancy.GetSpell(fullidx % 100),
                < 300 => SpellsChivalry.GetSpell(fullidx % 100),
                < 500 => SpellsBushido.GetSpell(fullidx % 100),
                < 600 => SpellsNinjitsu.GetSpell(fullidx % 100),
                < 678 => SpellsSpellweaving.GetSpell(fullidx % 100),
                < 700 => SpellsMysticism.GetSpell((fullidx - 77) % 100),
                _ => SpellsMastery.GetSpell(fullidx % 100)
            };
        }

        public static void FullIndexSetModifySpell
        (
            int fullidx,
            int id,
            int iconid,
            int smalliconid,
            int minskill,
            int manacost,
            int tithing,
            string name,
            string words,
            TargetType target,
            params Reagents[] regs
        )
        {
            if (fullidx < 1 || fullidx > 799)
            {
                return;
            }

            SpellDefinition sd = FullIndexGetSpell(fullidx);

            if (sd.ID == fullidx) //we are not using an emptyspell spelldefinition
            {
                if (iconid == 0)
                {
                    iconid = sd.GumpIconID;
                }

                if (smalliconid == 0)
                {
                    smalliconid = sd.GumpIconSmallID;
                }

                if (tithing == 0)
                {
                    tithing = sd.TithingCost;
                }

                if (manacost == 0)
                {
                    manacost = sd.ManaCost;
                }

                if (minskill == 0)
                {
                    minskill = sd.MinSkill;
                }

                if (!string.IsNullOrEmpty(sd.PowerWords) && sd.PowerWords != words)
                {
                    WordToTargettype.Remove(sd.PowerWords);
                }

                if (!string.IsNullOrEmpty(sd.Name) && sd.Name != name)
                {
                    WordToTargettype.Remove(sd.Name);
                }
            }

            sd = new SpellDefinition
            (
                name,
                fullidx,
                iconid,
                smalliconid,
                words,
                manacost,
                minskill,
                tithing,
                target,
                regs
            );

            if (fullidx < 100)
            {
                SpellsMagery.SetSpell(id, in sd);
            }
            else if (fullidx < 200)
            {
                SpellsNecromancy.SetSpell(id, in sd);
            }
            else if (fullidx < 300)
            {
                SpellsChivalry.SetSpell(id, in sd);
            }
            else if (fullidx < 500)
            {
                SpellsBushido.SetSpell(id, in sd);
            }
            else if (fullidx < 600)
            {
                SpellsNinjitsu.SetSpell(id, in sd);
            }
            else if (fullidx < 678)
            {
                SpellsSpellweaving.SetSpell(id, in sd);
            }
            else if (fullidx < 700)
            {
                SpellsMysticism.SetSpell(id - 77, in sd);
            }
            else
            {
                SpellsMastery.SetSpell(id, in sd);
            }
        }
    }
}