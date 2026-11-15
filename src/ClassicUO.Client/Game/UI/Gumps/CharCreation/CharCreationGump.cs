// SPDX-License-Identifier: BSD-2-Clause

using System.Linq;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps.Login;
using ClassicUO.Assets;
using ClassicUO.Utility;

namespace ClassicUO.Game.UI.Gumps.CharCreation
{
    internal class CharCreationGump : Gump
    {
        private PlayerMobile _character;
        // T9 (cold-caches): the preview mobile is not in World, so it has no record to hold a name
        private string _characterName;
        // the new character's stats and skills (the PlayerMobile constructor made them before PlayerState moved to the World)
        private PlayerState _characterState;
        private int _cityIndex;
        private CharCreationStep _currentStep;
        private LoadingGump _loadingGump;
        private readonly LoginScene _loginScene;
        private ProfessionInfo _selectedProfession;

        public CharCreationGump(World world, LoginScene scene) : base(world, 0, 0)
        {
            _loginScene = scene;
            Add(new CreateCharAppearanceGump(world), 1);
            SetStep(CharCreationStep.Appearence);
            CanCloseWithRightClick = false;
        }

        internal static int _skillsCount => Client.Game.UO.Version >= ClientVersion.CV_70160 ? 4 : 3;

        public void SetCharacter(PlayerMobile character, string name)
        {
            if (!ReferenceEquals(_character, character))
            {
                _characterState = new PlayerState(Client.Game.UO.FileManager.Skills.Skills);
            }

            _character = character;
            _characterName = name;
            SetStep(CharCreationStep.ChooseProfession);
        }

        public void SetAttributes(bool force = false)
        {
            SetStep(_selectedProfession.DescriptionIndex >= 0 || force ? CharCreationStep.ChooseCity : CharCreationStep.ChooseTrade);
        }

        public void SetCity(int cityIndex)
        {
            _cityIndex = cityIndex;
        }

        public void SetProfession(ProfessionInfo info)
        {
            for (int i = 0; i < _skillsCount; i++)
            {
                int skillIndex = info.SkillDefVal[i, 0];

                if (skillIndex >= _characterState.Skills.Length)
                {
                    continue;
                }

                if ((World.ClientFeatures.Flags & CharacterListFlags.CLF_SAMURAI_NINJA) == 0 && (skillIndex == 52 || skillIndex == 53))
                {
                    // reset skills if needed
                    for (int k = 0; k < i; k++)
                    {
                        Skill skill = _characterState.Skills[info.SkillDefVal[k, 0]];
                        skill.ValueFixed = 0;
                        skill.BaseFixed = 0;
                        skill.CapFixed = 0;
                        skill.Lock = Lock.Locked;
                    }

                    MessageBoxGump messageBox = new MessageBoxGump
                    (
                        World,
                        400,
                        300,
                        Client.Game.UO.FileManager.Clilocs.GetString(1063016),
                        null,
                        true
                    )
                    {
                        X = 470 / 2 - 400 / 2 + 100,
                        Y = 372 / 2 - 300 / 2 + 20,
                        CanMove = false
                    };

                    UIManager.Add(messageBox);

                    return;
                }

                Skill skill2 = _characterState.Skills[skillIndex];
                skill2.ValueFixed = (ushort) info.SkillDefVal[i, 1];
                skill2.BaseFixed = 0;
                skill2.CapFixed = 0;
                skill2.Lock = Lock.Locked;
            }

            _selectedProfession = info;
            _characterState.Strength = (ushort) _selectedProfession.StatsVal[0];
            _characterState.Intelligence = (ushort) _selectedProfession.StatsVal[1];
            _characterState.Dexterity = (ushort) _selectedProfession.StatsVal[2];

            SetAttributes();

            SetStep(_selectedProfession.DescriptionIndex > 0 ? CharCreationStep.ChooseCity : CharCreationStep.ChooseTrade);
        }

        public void CreateCharacter(byte profession)
        {
            _loginScene.CreateCharacter(_character, _characterName, _characterState, _cityIndex, profession);
        }

        public void StepBack(int steps = 1)
        {
            if (_currentStep == CharCreationStep.Appearence)
            {
                _loginScene.StepBack();
            }
            else
            {
                SetStep(_currentStep - steps);
            }
        }

        public void ShowMessage(string message)
        {
            int currentPage = ActivePage;

            if (_loadingGump != null)
            {
                Remove(_loadingGump);
            }

            Add(_loadingGump = new LoadingGump(World, message, LoginButtons.OK, a => ChangePage(currentPage)), 4);
            ChangePage(4);
        }

        private void SetStep(CharCreationStep step)
        {
            _currentStep = step;

            switch (step)
            {
                default:
                case CharCreationStep.Appearence:
                    ChangePage(1);

                    break;

                case CharCreationStep.ChooseProfession:
                    Control existing = Children.FirstOrDefault(page => page.Page == 2);

                    if (existing != null)
                    {
                        Remove(existing);
                    }

                    Add(new CreateCharProfessionGump(World), 2);

                    ChangePage(2);

                    break;

                case CharCreationStep.ChooseTrade:
                    existing = Children.FirstOrDefault(page => page.Page == 3);

                    if (existing != null)
                    {
                        Remove(existing);
                    }

                    Add(new CreateCharTradeGump(World, _character, _characterState, _selectedProfession), 3);
                    ChangePage(3);

                    break;

                case CharCreationStep.ChooseCity:
                    existing = Children.FirstOrDefault(page => page.Page == 4);

                    if (existing != null)
                    {
                        Remove(existing);
                    }

                    Add(new CreateCharSelectionCityGump(World, (byte) _selectedProfession.DescriptionIndex, _loginScene), 4);

                    ChangePage(4);

                    break;
            }
        }

        private enum CharCreationStep
        {
            Appearence = 0,
            ChooseProfession = 1,
            ChooseTrade = 2,
            ChooseCity = 3
        }
    }
}