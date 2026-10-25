// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;

namespace ClassicUO.Game.UI.Gumps
{
    internal abstract class TextContainerGump : Gump, IGumpDeferredDraw
    {
        protected TextContainerGump(World world, uint local, uint server) : base(world, local, server)
        {
            TextRenderer = new TextRenderer(World);
        }

        public TextRenderer TextRenderer { get; }

        public void AddText(TextObject msg)
        {
            if (msg == null)
            {
                return;
            }

            msg.Time = Time.Ticks + 4000;

            TextRenderer.AddMessage(msg);
        }

        public override void Update()
        {
            base.Update();
            TextRenderer.Update();
        }

        public override void Dispose()
        {
            TextRenderer.UnlinkD();

            //TextRenderer.Clear();
            base.Dispose();
        }


        bool IGumpDeferredDraw.DrawDeferred(UltimaBatcher2D batcher, int x, int y, float layerDepth, int arg)
        {
            TextRenderer.Draw
            (
                batcher,
                x,
                y,
                layerDepth,
                true
            );
            return true;
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
            float layerDepth = layerDepthRef;

            //TextRenderer.MoveToTopIfSelected();
            TextRenderer.ProcessWorldText(true);

            renderLists.AddGumpNoAtlas(GumpCommand.Deferred(this, x, y, layerDepth));

            return true;
        }
    }
}