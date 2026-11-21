// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// The options' one frame-limit control: the slider runs from <see cref="Constants.MIN_FPS"/> to
    /// <see cref="Constants.MAX_FPS"/>, and one step past the top is "Off", stored as Settings.FPS = 0 (uncapped, as the
    /// removed "Uncapped FPS" checkbox stored it, so an old settings.json loads unchanged).
    /// </summary>
    internal static class FrameLimitSlider
    {
        public const int Off = Constants.MAX_FPS + 1;

        public static int FromSetting(int fps) => fps <= 0 ? Off : System.Math.Clamp(fps, Constants.MIN_FPS, Constants.MAX_FPS);

        public static int ToSetting(int sliderValue) => sliderValue >= Off ? 0 : sliderValue;
    }
}
