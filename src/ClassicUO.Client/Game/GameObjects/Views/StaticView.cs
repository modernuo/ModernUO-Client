// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class Static
    {
        private int _canBeTransparent;

        public override bool TransparentTest(int z)
        {
            bool r = true;

            if (Z <= z - ItemData.Height)
            {
                r = false;
            }
            else if (z < Z && (_canBeTransparent & 0xFF) == 0)
            {
                r = false;
            }

            return r;
        }

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (Map.MapObjectRecycler.Check && InRecycler)
            {
                Map.MapObjectRecycler.Touched(this, Map.MapObjectRecycler.Site.StaticDraw);
            }

            if (!AllowedToDraw || IsDestroyed)
            {
                return false;
            }

            ushort graphic = Graphic;
            ushort hue = Hue;
            bool partial = ItemData.IsPartialHue;

            if (ProfileManager.CurrentProfile.HighlightGameObjects && SelectedObject.Object == this)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (
                ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                && Distance > World.ClientViewRange
            )
            {
                hue = Constants.OUT_RANGE_COLOR;
                partial = false;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
                partial = false;
            }

            bool isTree = StaticFilters.IsTree(graphic, out _);

            // Trees and foliage stay visible inside CoT circle
            bool cot = !isTree && !ItemData.IsFoliage && TransparentTest(World.Player.Z + 5);
            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, AlphaHue / 255f, circletrans: cot);

            if (isTree && ProfileManager.CurrentProfile.TreeToStumps)
            {
                graphic = Constants.TREE_REPLACE_GRAPHIC;
            }

            DrawStaticAnimated(
                batcher,
                graphic,
                posX,
                posY,
                hueVec,
                ProfileManager.CurrentProfile.ShadowsEnabled
                    && ProfileManager.CurrentProfile.ShadowsStatics
                    && (isTree || ItemData.IsFoliage || StaticFilters.IsRock(graphic)),
                depth,
                ProfileManager.CurrentProfile.AnimatedWaterEffect && ItemData.IsWet
            );

            if (ItemData.IsLight && !InChunkMesh)
            {
                Client.Game.GetScene<GameScene>().AddLight(this, this, posX + 22, posY + 22);
            }

            return true;
        }

        public override bool CheckMouseSelection()
        {
            if (Map.MapObjectRecycler.Check && InRecycler)
            {
                Map.MapObjectRecycler.Touched(this, Map.MapObjectRecycler.Site.StaticPick);
            }

            if (
                !(
                    SelectedObject.Object == this
                    || FoliageIndex != -1
                        && Client.Game.GetScene<GameScene>().FoliageIndex == FoliageIndex
                )
            )
            {
                ushort graphic = Graphic;

                bool isTree = StaticFilters.IsTree(graphic, out _);

                if (isTree && ProfileManager.CurrentProfile.TreeToStumps)
                {
                    graphic = Constants.TREE_REPLACE_GRAPHIC;
                }

                ref ArtEntry index = ref Client.Game.UO.FileManager.Arts.GetEntry(graphic + 0x4000);

                Point position = RealScreenPosition;
                position.X -= index.Width;
                position.Y -= index.Height;
                int mouseX = SelectedObject.TranslatedMousePositionByViewport.X - position.X;
                int mouseY = SelectedObject.TranslatedMousePositionByViewport.Y - position.Y;

                // most drawn statics are not under the mouse: an entry with a stored draw offset bounds the picker's mask,
                // so reject outside it without the picker. An unset entry (0, 0: not drawn yet, or 44 x 44 art) keeps the
                // picker test. CUO_PREP_CHECK=1 counts rejects the picker would have hit
                if ((index.Width | index.Height) != 0 && OutsidePickBounds(index.Width, index.Height, mouseX, mouseY))
                {
                    if (PrepCheck.On)
                    {
                        PrepCheck.StaticPicksRejected++;

                        if (Client.Game.UO.Arts.PixelCheck(graphic, mouseX, mouseY))
                        {
                            PrepCheck.StaticPicksWrong++;
                            PrepCheck.Sample ??= $"static graphic 0x{graphic:X4} mouse {mouseX},{mouseY} entry {index.Width},{index.Height}";
                        }
                    }

                    return false;
                }

                return Client.Game.UO.Arts.PixelCheck(graphic, mouseX, mouseY);
            }

            return false;
        }

        /// <summary>
        /// The static pick test's reject. Every writer of an art entry stores ((OrigW &gt;&gt; 1) - 22, OrigH - 44) from the
        /// sprite's untrimmed size, and the picker's mask is OrigW x OrigH, so the mask fits in (Width + 23) * 2 by
        /// Height + 44 pixels from the art's top left: a mouse outside that misses.
        /// </summary>
        internal static bool OutsidePickBounds(int entryWidth, int entryHeight, int mouseX, int mouseY)
            => (uint)mouseX >= (uint)((entryWidth + 23) * 2) || (uint)mouseY >= (uint)(entryHeight + 44);
    }
}
