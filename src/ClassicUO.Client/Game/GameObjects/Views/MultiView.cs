// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal partial class Multi
    {
        private int _canBeTransparent;
        public bool IsHousePreview;

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
            if (!AllowedToDraw || IsDestroyed)
            {
                return false;
            }

            ushort hue = Hue;

            if (State != 0)
            {
                if ((State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_IGNORE_IN_RENDER) != 0)
                {
                    return false;
                }

                if ((State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_INCORRECT_PLACE) != 0)
                {
                    hue = 0x002B;
                }

                if ((State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_TRANSPARENT) != 0)
                {
                    AlphaHue = 192;
                    depth -= 0.01f;
                }
            }

            ushort graphic = Graphic;
            bool partial = ItemData.IsPartialHue;

            Profile currentProfile = ProfileManager.CurrentProfile;

            if (currentProfile.HighlightGameObjects && SelectedObject.Object == this)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (currentProfile.NoColorObjectsOutOfRange && Distance > World.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
                partial = false;
            }
            else if (World.Player.IsDead && currentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
                partial = false;
            }

            bool cot = TransparentTest(World.Player.Z + 5);
            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, AlphaHue / 255f, circletrans: cot);

            if (IsHousePreview)
            {
                hueVec.Z *= 0.5f;
            }

            posX += (int)Offset.X;
            posY += (int)(Offset.Y + Offset.Z);

            DrawStaticAnimated(batcher, graphic, posX, posY, hueVec, false, depth);

            if (ItemData.IsLight && !InChunkMesh)
            {
                Client.Game.GetScene<GameScene>().AddLight(this, this, posX + 22, posY + 22);
            }

            return true;
        }

        /// <summary>
        /// Where View.DrawStaticAnimated puts the top left of a <paramref name="width"/> x <paramref name="height"/> art
        /// sprite drawn at (<paramref name="x"/>, <paramref name="y"/>): the anchor it stores on the art entry, from the
        /// atlas UV (the pack's untrimmed size).
        /// </summary>
        internal static void SpriteTopLeft(int x, int y, int width, int height, out int left, out int top)
        {
            Item.PickEntryFromSize(width, height, out int entryWidth, out int entryHeight);
            left = x - entryWidth;
            top = y - entryHeight;
        }

        public override bool CheckMouseSelection()
        {
            if (
                !(
                    SelectedObject.Object == this
                    || IsHousePreview
                    || FoliageIndex != -1
                        && Client.Game.GetScene<GameScene>().FoliageIndex == FoliageIndex
                )
            )
            {
                if (State != 0)
                {
                    if (
                        (
                            State
                            & (
                                CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_IGNORE_IN_RENDER
                                | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW
                            )
                        ) != 0
                    )
                    {
                        return false;
                    }
                }

                // the sprite Draw draws (DrawStaticAnimated: the current animation frame, the boat's Offset), anchored from
                // the pack's sprite size: the art can be in the atlas (first-sight prefetch) before the first draw stores
                // the anchor on the art entry
                var arts = Client.Game.UO.FileManager.Arts;
                ushort drawn = (ushort)(Graphic + arts.GetEntry(Graphic + 0x4000).AnimOffset);
                var sprites = arts.PackSpriteEntries;
                uint idx = drawn + 0x4000u;

                if (idx >= (uint)sprites.Length || sprites[idx].OrigW == 0 || sprites[idx].OrigH == 0)
                {
                    return false;
                }

                Point position = RealScreenPosition;
                position.X += (int)Offset.X;
                position.Y += (int)(Offset.Y + Offset.Z);
                SpriteTopLeft(position.X, position.Y, sprites[idx].OrigW, sprites[idx].OrigH, out int left, out int top);
                int mouseX = SelectedObject.TranslatedMousePositionByViewport.X - left;
                int mouseY = SelectedObject.TranslatedMousePositionByViewport.Y - top;

                // the picker's mask is the sprite's untrimmed size: a mouse outside it misses without the picker.
                // CUO_PREP_CHECK=1 counts rejects the picker would have hit
                if (OutsideSprite(sprites[idx].OrigW, sprites[idx].OrigH, mouseX, mouseY))
                {
                    if (Scenes.PrepCheck.On)
                    {
                        Scenes.PrepCheck.StaticPicksRejected++;

                        if (Client.Game.UO.Arts.PixelCheck(drawn, mouseX, mouseY))
                        {
                            Scenes.PrepCheck.StaticPicksWrong++;
                            Scenes.PrepCheck.Sample ??= $"multi at {X},{Y},{Z} graphic 0x{drawn:X4} mouse {mouseX},{mouseY} sprite {sprites[idx].OrigW}x{sprites[idx].OrigH}";
                        }
                    }

                    return false;
                }

                return Client.Game.UO.Arts.PixelCheck(drawn, mouseX, mouseY);
            }

            return false;
        }

        /// <summary>Outside a <paramref name="width"/> x <paramref name="height"/> mask: the pixel picker's size test.</summary>
        internal static bool OutsideSprite(int width, int height, int mouseX, int mouseY)
            => (uint)mouseX >= (uint)width || (uint)mouseY >= (uint)height;
    }
}
