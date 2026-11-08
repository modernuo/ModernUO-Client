// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.Data;
using System;
using System.Collections.Generic;

namespace ClassicUO.Game
{
    /// <summary>
    /// The player's session data (status packet stats, resistances, skills, buffs, weapon abilities, stat locks), owned
    /// by the World rather than by the <c>PlayerMobile</c> handle (ecs-transition-plan Q4: a typed store, not ECS
    /// components). <see cref="World.CreatePlayer"/> installs a fresh one with each new player object (0x1B), and
    /// <see cref="World.Clear"/> drops it, which is the lifetime these fields had on <c>PlayerMobile</c>; a re-created
    /// player handle that does not go through <c>CreatePlayer</c> keeps it.
    /// </summary>
    internal sealed class PlayerState
    {
        private readonly Dictionary<BuffIconType, BuffIcon> _buffIcons = new Dictionary<BuffIconType, BuffIcon>();

        /// <summary>No skills: the store before the first login and after a logout.</summary>
        public PlayerState() : this(Array.Empty<SkillEntry>())
        {
        }

        public PlayerState(IReadOnlyList<SkillEntry> skills)
        {
            Skills = new Skill[skills.Count];

            for (int i = 0; i < Skills.Length; i++)
            {
                SkillEntry skill = skills[i];
                Skills[i] = new Skill(skill.Name, skill.Index, skill.HasAction);
            }
        }

        public Skill[] Skills { get; }
        public IReadOnlyDictionary<BuffIconType, BuffIcon> BuffIcons => _buffIcons;

        public readonly Ability[] Abilities = [Ability.Invalid, Ability.Invalid];
        public ref Ability PrimaryAbility => ref Abilities[0];
        public ref Ability SecondaryAbility => ref Abilities[1];

        public short ColdResistance;
        public short DamageIncrease;
        public short DamageMax;
        public short DamageMin;
        public long DeathScreenTimer;
        public short DefenseChanceIncrease;
        public Lock DexLock;
        public ushort Dexterity;
        public short DexterityIncrease;
        public short EnergyResistance;
        public short EnhancePotions;
        public short FasterCasting;
        public short FasterCastRecovery;
        public short FireResistance;
        public byte Followers;
        public byte FollowersMax;
        public uint Gold;
        public short HitChanceIncrease;
        public short HitPointsIncrease;
        public short HitPointsRegeneration;
        public ushort Intelligence;
        public short IntelligenceIncrease;
        public Lock IntLock;
        public short LowerManaCost;
        public short LowerReagentCost;
        public ushort Luck;
        public short ManaIncrease;
        public short ManaRegeneration;
        public short MaxColdResistence;
        public short MaxDefenseChanceIncrease;
        public short MaxEnergyResistence;
        public short MaxFireResistence;
        public short MaxHitPointsIncrease;
        public short MaxManaIncrease;
        public short MaxPhysicResistence;
        public short MaxPoisonResistence;
        public short MaxStaminaIncrease;
        public short PhysicalResistance;
        public short PoisonResistance;
        public short ReflectPhysicalDamage;
        public short SpellDamageIncrease;
        public short StaminaIncrease;
        public short StaminaRegeneration;
        public short StatsCap;
        public ushort Strength;
        public short StrengthIncrease;
        public Lock StrLock;
        public short SwingSpeedIncrease;
        public uint TithingPoints;
        public ushort Weight;
        public ushort WeightMax;

        public void AddBuff(BuffIconType type, ushort graphic, uint time, string text)
        {
            _buffIcons[type] = new BuffIcon(type, graphic, time, text);
        }

        public bool IsBuffIconExists(BuffIconType graphic)
        {
            return _buffIcons.ContainsKey(graphic);
        }

        public void RemoveBuff(BuffIconType graphic)
        {
            _buffIcons.Remove(graphic);
        }
    }
}
