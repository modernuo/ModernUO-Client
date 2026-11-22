// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace ClassicUO.Game.UI.Gumps
{
    internal class InfoBarGump : Gump
    {
        private readonly AlphaBlendControl _background;

        private readonly List<InfoBarControl> _infobarControls = new List<InfoBarControl>();
        private long _refreshTime;

        public InfoBarGump(World world) : base(world, 0, 0)
        {
            CanMove = true;
            AcceptMouseInput = true;
            AcceptKeyboardInput = false;
            CanCloseWithRightClick = false;
            Height = 20;

            Add(_background = new AlphaBlendControl(0.7f) { Width = Width, Height = Height });

            ResetItems();
        }

        public override GumpType GumpType => GumpType.InfoBar;

        public void ResetItems()
        {
            foreach (InfoBarControl c in _infobarControls)
            {
                c.Dispose();
            }

            _infobarControls.Clear();

            List<InfoBarItem> infoBarItems = World.InfoBars.GetInfoBars();

            for (int i = 0; i < infoBarItems.Count; i++)
            {
                InfoBarControl info = new InfoBarControl(this, infoBarItems[i].label, infoBarItems[i].var, infoBarItems[i].hue);

                _infobarControls.Add(info);
                Add(info);
            }
        }

        public override void Save(XmlTextWriter writer)
        {
            base.Save(writer);
            //writer.WriteStartElement("controls");

            //foreach (InfoBarControl co in _infobarControls)
            //{
            //    writer.WriteStartElement("control");
            //    writer.WriteAttributeString("label", co.Text);
            //    writer.WriteAttributeString("var", ((int) co.Var).ToString());
            //    writer.WriteAttributeString("hue", co.Hue.ToString());
            //    writer.WriteEndElement();
            //}
            //writer.WriteEndElement();
        }

        public override void Restore(XmlElement xml)
        {
            base.Restore(xml);

            //XmlElement controlsXml = xml["controls"];
            //_infobarControls.Clear();

            //if (controlsXml != null)
            //{
            //    foreach (XmlElement controlXml in controlsXml.GetElementsByTagName("control"))
            //    {
            //        InfoBarControl control = new InfoBarControl(controlXml.GetAttribute("label"),
            //                                                    (InfoBarVars) int.Parse(controlXml.GetAttribute("var")),
            //                                                    ushort.Parse(controlXml.GetAttribute("hue")));

            //        Add(control);
            //        _infobarControls.Add(control);
            //    }
            //}
        }

        public override void Update()
        {
            if (IsDisposed)
            {
                return;
            }

            if (_refreshTime < Time.Ticks)
            {
                _refreshTime = (long)Time.Ticks + 125;

                int x = 5;

                foreach (InfoBarControl c in _infobarControls)
                {
                    c.X = x;
                    x += c.Width + 5;
                }
            }

            base.Update();

            if (Children.Count != 0)
            {
                Width = Children[Children.Count - 1].Bounds.Right;
            }

            _background.Width = Width;
        }
    }


    internal class InfoBarControl : Control
    {
        private readonly InfoBarGump _gump;
        private readonly Label _data;
        private readonly Label _label;
        private ushort _warningLinesHue;

        public InfoBarControl(InfoBarGump gump, string label, InfoBarVars var, ushort hue)
        {
            _gump = gump;
            AcceptMouseInput = false;
            WantUpdateSize = true;
            CanMove = false;

            _label = new Label(label, true, 999) { Height = 20, Hue = hue };
            Var = var;

            _data = new Label("", true, 999) { Height = 20, X = _label.Width, Hue = 0x0481 };
            Add(_label);
            Add(_data);
        }

        public string Text => _label.Text;
        public InfoBarVars Var { get; }

        public ushort Hue => _label.Hue;
        protected long _refreshTime;

        public override void Update()
        {
            if (IsDisposed)
            {
                return;
            }

            if (_refreshTime < Time.Ticks)
            {
                _refreshTime = (long)Time.Ticks + 125;

                SetVarData(Var);

                if (ProfileManager.CurrentProfile.InfoBarHighlightType == 0 || Var == InfoBarVars.NameNotoriety)
                {
                    _data.Hue = GetVarHue(Var);
                }
                else
                {
                    _data.Hue = 0x0481;
                    _warningLinesHue = GetVarHue(Var);
                }

                _data.WantUpdateSize = true;
            }

            WantUpdateSize = true;

            base.Update();
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
            float layerDepth = layerDepthRef;

            if (Var != InfoBarVars.NameNotoriety && ProfileManager.CurrentProfile.InfoBarHighlightType == 1 && _warningLinesHue != 0x0481)
            {
                Vector3 hueVector = ShaderHueTranslator.GetHueVector(_warningLinesHue);

                Texture2D white = SolidColorTextureCache.GetTexture(Color.White);

                renderLists.AddGumpNoAtlas(GumpCommand.SpriteStretched(white, new Rectangle(_data.ScreenCoordinateX, _data.ScreenCoordinateY, _data.Width, 2), hueVector, layerDepth));
                renderLists.AddGumpNoAtlas(GumpCommand.SpriteStretched(white, new Rectangle(_data.ScreenCoordinateX, _data.ScreenCoordinateY + Parent.Height - 2, _data.Width, 2), hueVector, layerDepth));
            }

            return true;
        }

        // G2: formats into the label on the stack; a string is made only when the shown text changes
        private void SetVarData(InfoBarVars var)
        {
            switch (var)
            {
                case InfoBarVars.HP: _data.SetPair(_gump.World.Player.Hits, '/', _gump.World.Player.HitsMax); return;

                case InfoBarVars.Mana: _data.SetPair(_gump.World.Player.Mana, '/', _gump.World.Player.ManaMax); return;

                case InfoBarVars.Stamina: _data.SetPair(_gump.World.Player.Stamina, '/', _gump.World.Player.StaminaMax); return;

                case InfoBarVars.Weight: _data.SetPair(_gump.World.PlayerState.Weight, '/', _gump.World.PlayerState.WeightMax); return;

                case InfoBarVars.Followers: _data.SetPair(_gump.World.PlayerState.Followers, '/', _gump.World.PlayerState.FollowersMax); return;

                case InfoBarVars.Gold: _data.SetNumber(_gump.World.PlayerState.Gold); return;

                case InfoBarVars.Damage: _data.SetPair(_gump.World.PlayerState.DamageMin, '-', _gump.World.PlayerState.DamageMax); return;

                case InfoBarVars.Armor: _data.SetNumber(_gump.World.PlayerState.PhysicalResistance); return;

                case InfoBarVars.Luck: _data.SetNumber(_gump.World.PlayerState.Luck); return;

                case InfoBarVars.FireResist: _data.SetNumber(_gump.World.PlayerState.FireResistance); return;

                case InfoBarVars.ColdResist: _data.SetNumber(_gump.World.PlayerState.ColdResistance); return;

                case InfoBarVars.PoisonResist: _data.SetNumber(_gump.World.PlayerState.PoisonResistance); return;

                case InfoBarVars.EnergyResist: _data.SetNumber(_gump.World.PlayerState.EnergyResistance); return;

                case InfoBarVars.LowerReagentCost: _data.SetNumber(_gump.World.PlayerState.LowerReagentCost); return;

                case InfoBarVars.SpellDamageInc: _data.SetNumber(_gump.World.PlayerState.SpellDamageIncrease); return;

                case InfoBarVars.FasterCasting: _data.SetNumber(_gump.World.PlayerState.FasterCasting); return;

                case InfoBarVars.FasterCastRecovery: _data.SetNumber(_gump.World.PlayerState.FasterCastRecovery); return;

                case InfoBarVars.HitChanceInc: _data.SetNumber(_gump.World.PlayerState.HitChanceIncrease); return;

                case InfoBarVars.DefenseChanceInc: _data.SetNumber(_gump.World.PlayerState.DefenseChanceIncrease); return;

                case InfoBarVars.LowerManaCost: _data.SetNumber(_gump.World.PlayerState.LowerManaCost); return;

                case InfoBarVars.DamageChanceInc: _data.SetNumber(_gump.World.PlayerState.DamageIncrease); return;

                case InfoBarVars.SwingSpeedInc: _data.SetNumber(_gump.World.PlayerState.SwingSpeedIncrease); return;

                case InfoBarVars.StatsCap: _data.SetNumber(_gump.World.PlayerState.StatsCap); return;

                case InfoBarVars.NameNotoriety: _data.Text = _gump.World.Player.Name; return;

                case InfoBarVars.TithingPoints: _data.SetNumber(_gump.World.PlayerState.TithingPoints); return;

                default: _data.Text = ""; return;
            }
        }

        private ushort GetVarHue(InfoBarVars var)
        {
            float percent;

            switch (var)
            {
                case InfoBarVars.HP:
                    percent = _gump.World.Player.Hits / (float) _gump.World.Player.HitsMax;

                    if (percent <= 0.25)
                    {
                        return 0x0021;
                    }
                    else if (percent <= 0.5)
                    {
                        return 0x0030;
                    }
                    else if (percent <= 0.75)
                    {
                        return 0x0035;
                    }
                    else
                    {
                        return 0x0481;
                    }

                case InfoBarVars.Mana:
                    percent = _gump.World.Player.Mana / (float) _gump.World.Player.ManaMax;

                    if (percent <= 0.25)
                    {
                        return 0x0021;
                    }
                    else if (percent <= 0.5)
                    {
                        return 0x0030;
                    }
                    else if (percent <= 0.75)
                    {
                        return 0x0035;
                    }
                    else
                    {
                        return 0x0481;
                    }

                case InfoBarVars.Stamina:
                    percent = _gump.World.Player.Stamina / (float)_gump.World.Player.StaminaMax;

                    if (percent <= 0.25)
                    {
                        return 0x0021;
                    }
                    else if (percent <= 0.5)
                    {
                        return 0x0030;
                    }
                    else if (percent <= 0.75)
                    {
                        return 0x0035;
                    }
                    else
                    {
                        return 0x0481;
                    }

                case InfoBarVars.Weight:
                    percent = _gump.World.PlayerState.Weight / (float)_gump.World.PlayerState.WeightMax;

                    if (percent >= 1)
                    {
                        return 0x0021;
                    }
                    else if (percent >= 0.75)
                    {
                        return 0x0030;
                    }
                    else if (percent >= 0.5)
                    {
                        return 0x0035;
                    }
                    else
                    {
                        return 0x0481;
                    }

                case InfoBarVars.NameNotoriety: return Notoriety.GetHue(_gump.World.Player.NotorietyFlag);

                default: return 0x0481;
            }
        }
    }
}