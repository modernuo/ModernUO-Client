// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Scenes;

namespace ClassicUO.Game.UI.Controls
{
    internal class ScissorControl : Control, IGumpDeferredDraw
    {
        public ScissorControl(bool enabled, int x, int y, int width, int height) : this(enabled)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public ScissorControl(bool enabled)
        {
            CanMove = false;
            AcceptMouseInput = false;
            AcceptKeyboardInput = false;
            Alpha = 1.0f;
            WantUpdateSize = false;
            DoScissor = enabled;
        }

        public bool DoScissor;

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            renderLists.AddGumpWithAtlas(GumpCommand.Deferred(this, x, y, layerDepthRef));
            renderLists.AddGumpNoAtlas(GumpCommand.Deferred(this, x, y, layerDepthRef));

            return true;
        }

        bool IGumpDeferredDraw.DrawDeferred(Renderer.UltimaBatcher2D batcher, int x, int y, float layerDepth, int arg)
        {
            if (DoScissor)
            {
                ClipPairTracker.Begin(batcher.ClipBegin(x, y, Width, Height));
            }
            else
            {
                if (ClipPairTracker.End())
                {
                    batcher.ClipEnd();
                }
            }

            return true;
        }
    }
}