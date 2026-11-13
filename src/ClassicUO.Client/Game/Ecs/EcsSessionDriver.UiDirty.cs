// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    internal sealed partial class EcsSessionDriver
    {
        private Gump _uiDirtyFixture;
        private uint _uiHeldSerial;

        private void ReleaseUiHeld()
        {
            if (_uiHeldSerial == 0) return;
            var pack = _gump?.World.Player?.FindItemByLayer(Layer.Backpack);
            if (pack != null && Client.Game.UO.GameCursor.ItemHold.Enabled)
                GameActions.DropItem(_uiHeldSerial, 0xFFFF, 0xFFFF, 0, pack.Serial);
            _uiHeldSerial = 0;
        }

        private bool ExecuteUiDirty(World world, string command, string[] args)
        {
            bool on = args.Length > 0 && args[0] == "on";
            switch (command)
            {
                case "uidirty": Settings.GlobalSettings.UiGumpDirty = on; break;
                case "textquads": Settings.GlobalSettings.TextQuadCache = on; break;
                case "uiresize": Client.Game.SetWindowSize(int.Parse(args[0]), int.Parse(args[1])); break;
                case "uidirtymutate":
                    if (_uiDirtyFixture != null)
                        foreach (Control control in _uiDirtyFixture.Children)
                        {
                            if (control is Label label)
                            {
                                label.Text = "M5 changed label: text, hue and alpha";
                                label.Hue = 33;
                                label.Alpha = .75f;
                            }
                            if (control is Button button) button.IsClicked = !button.IsClicked;
                        }
                    break;
                case "uinames": GameActions.AllNames(world); break;
                case "uihover": return ExecuteMouse(string.Join(" ", args));
                case "uijournal":
                    if (on && UIManager.GetGump<JournalGump>() == null)
                        UIManager.Add(new JournalGump(world) { X = 500, Y = 70 });
                    else if (!on) UIManager.GetGump<JournalGump>()?.Dispose();
                    break;
                case "uiscroll":
                    var journal = UIManager.GetGump<JournalGump>();
                    if (journal != null)
                        foreach (Control control in journal.Children)
                            if (control is ScrollBarBase scroll)
                            {
                                int before = scroll.Value;
                                scroll.Value += int.Parse(args[0]);
                                Log.Info($"[ui-dirty-driver] journal scroll {before}->{scroll.Value}, max={scroll.MaxValue}");
                            }
                    break;
                case "uidirtyfixture":
                    _uiDirtyFixture?.Dispose();
                    _uiDirtyFixture = null;
                    if (on)
                    {
                        _uiDirtyFixture = new Gump(world, 0, 0) { X = 30, Y = 400, Width = 300, Height = 120, WantUpdateSize = false };
                        _uiDirtyFixture.Add(new ResizePic(0x0A28) { Width = 300, Height = 120, WantUpdateSize = false });
                        _uiDirtyFixture.Add(new Label("M5 replay fixture: static text", true, 0xFFFF) { X = 10, Y = 15 });
                        _uiDirtyFixture.Add(new Button(0, 0x00F2, 0x00F3, 0x00F1) { X = 10, Y = 65 });
                        UIManager.Add(_uiDirtyFixture);
                    }
                    break;
                case "uihold":
                    var hold = Client.Game.UO.GameCursor.ItemHold;
                    if (!on)
                    {
                        ReleaseUiHeld();
                        break;
                    }
                    var pack = world.Player.FindItemByLayer(Layer.Backpack);
                    if (pack != null && !hold.Enabled)
                        foreach (var item in world.State.Children(pack))
                        {
                            if (GameActions.PickUp(world, item.Serial, 0, 0, 1)) _uiHeldSerial = item.Serial;
                            break;
                        }
                    Log.Info($"[ui-dirty-driver] held item enabled={_uiHeldSerial != 0}");
                    break;
                default: return false;
            }
            return true;
        }
    }
}
#endif
