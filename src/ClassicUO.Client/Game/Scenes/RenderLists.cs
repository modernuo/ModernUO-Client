// SPDX-License-Identifier: BSD-2-Clause
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClassicUO.Game.Scenes
{
    internal enum GumpCommandKind : byte
    {
        Callback,
        /// <summary><see cref="RenderedText.Draw(UltimaBatcher2D, int, int, float, float, ushort, float)"/>, skipped when the text no longer has content.</summary>
        Text,
        /// <summary><c>batcher.Draw(texture, destination, source, hue, depth)</c></summary>
        Sprite,
        /// <summary><c>batcher.Draw(texture, destination, hue, depth)</c> (the whole texture)</summary>
        SpriteStretched,
        /// <summary><c>batcher.Draw(texture, new Vector2(x, y), source, hue, depth)</c></summary>
        SpriteAt,
        /// <summary><c>batcher.DrawTiled(texture, destination, source, hue, depth)</c></summary>
        Tiled,
        /// <summary><c>batcher.DrawRectangle(texture, x, y, w, h, hue, depth)</c> (an outline)</summary>
        Outline,
        /// <summary><see cref="IGumpDeferredDraw.DrawDeferred"/> on the queued control.</summary>
        Deferred
    }

    /// <summary>A control that queues itself and draws later, during the gump layer flush, instead of queueing a closure.</summary>
    internal interface IGumpDeferredDraw
    {
        bool DrawDeferred(UltimaBatcher2D batcher, int x, int y, float layerDepth, int arg);
    }

    /// <summary>
    /// A queued draw into one of the gump layers. A plain value: queueing one allocates nothing, unlike a
    /// closure per control per frame. Use the typed kinds (text, sprites, tiles, outlines, a deferred control);
    /// <see cref="Callback"/> remains for one-off compound draws.
    ///
    /// Text entries keep a reference to the <see cref="RenderedText"/> and are skipped when it was
    /// destroyed/recycled (pooled via <see cref="RenderedText"/>'s internal pool) between queue and flush.
    /// </summary>
    internal readonly struct GumpCommand
    {
        public readonly GumpCommandKind Kind;
        public readonly RenderedText Text;
        public readonly Texture2D Texture;
        public readonly IGumpDeferredDraw Target;
        public readonly Func<UltimaBatcher2D, bool> Callback;
        public readonly int X;
        public readonly int Y;
        public readonly int Width;
        public readonly int Height;
        public readonly Rectangle Source;
        public readonly Vector3 HueVector;
        public readonly float LayerDepth;
        public readonly float Alpha;
        public readonly ushort Hue;
        public readonly int Arg;

        private GumpCommand
        (
            GumpCommandKind kind,
            RenderedText text,
            Texture2D texture,
            IGumpDeferredDraw target,
            Func<UltimaBatcher2D, bool> callback,
            int x,
            int y,
            int width,
            int height,
            Rectangle source,
            Vector3 hueVector,
            float layerDepth,
            float alpha,
            ushort hue,
            int arg
        )
        {
            Kind = kind;
            Text = text;
            Texture = texture;
            Target = target;
            Callback = callback;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Source = source;
            HueVector = hueVector;
            LayerDepth = layerDepth;
            Alpha = alpha;
            Hue = hue;
            Arg = arg;
        }

        public GumpCommand(RenderedText text, int x, int y, float layerDepth, float alpha, ushort hue)
            : this(GumpCommandKind.Text, text, null, null, null, x, y, 0, 0, default, default, layerDepth, alpha, hue, 0) { }

        public GumpCommand(Func<UltimaBatcher2D, bool> callback)
            : this(GumpCommandKind.Callback, null, null, null, callback, 0, 0, 0, 0, default, default, 0f, 0f, 0, 0) { }

        public Rectangle Destination => new Rectangle(X, Y, Width, Height);

        public static GumpCommand Sprite(Texture2D texture, Rectangle destination, Rectangle source, Vector3 hue, float layerDepth)
            => new(GumpCommandKind.Sprite, null, texture, null, null, destination.X, destination.Y, destination.Width, destination.Height, source, hue, layerDepth, 0f, 0, 0);

        public static GumpCommand SpriteStretched(Texture2D texture, Rectangle destination, Vector3 hue, float layerDepth)
            => new(GumpCommandKind.SpriteStretched, null, texture, null, null, destination.X, destination.Y, destination.Width, destination.Height, default, hue, layerDepth, 0f, 0, 0);

        public static GumpCommand SpriteAt(Texture2D texture, int x, int y, Rectangle source, Vector3 hue, float layerDepth)
            => new(GumpCommandKind.SpriteAt, null, texture, null, null, x, y, 0, 0, source, hue, layerDepth, 0f, 0, 0);

        public static GumpCommand Tiled(Texture2D texture, Rectangle destination, Rectangle source, Vector3 hue, float layerDepth)
            => new(GumpCommandKind.Tiled, null, texture, null, null, destination.X, destination.Y, destination.Width, destination.Height, source, hue, layerDepth, 0f, 0, 0);

        public static GumpCommand Outline(Texture2D texture, int x, int y, int width, int height, Vector3 hue, float layerDepth)
            => new(GumpCommandKind.Outline, null, texture, null, null, x, y, width, height, default, hue, layerDepth, 0f, 0, 0);

        public static GumpCommand Deferred(IGumpDeferredDraw target, int x, int y, float layerDepth, int arg = 0)
            => new(GumpCommandKind.Deferred, null, null, target, null, x, y, 0, 0, default, default, layerDepth, 0f, 0, arg);

        public bool Execute(UltimaBatcher2D batcher)
        {
            switch (Kind)
            {
                case GumpCommandKind.Text:
                    // HasContent rejects destroyed/empty text, possible when the instance went back to the pool between queue and flush
                    return Text != null && Text.HasContent && Text.Draw(batcher, X, Y, LayerDepth, Alpha, Hue);

                case GumpCommandKind.Sprite:
                    batcher.Draw(Texture, Destination, Source, HueVector, LayerDepth);

                    return true;

                case GumpCommandKind.SpriteStretched:
                    batcher.Draw(Texture, Destination, HueVector, LayerDepth);

                    return true;

                case GumpCommandKind.SpriteAt:
                    batcher.Draw(Texture, new Vector2(X, Y), Source, HueVector, LayerDepth);

                    return true;

                case GumpCommandKind.Tiled:
                    batcher.DrawTiled(Texture, Destination, Source, HueVector, LayerDepth);

                    return true;

                case GumpCommandKind.Outline:
                    return batcher.DrawRectangle(Texture, X, Y, Width, Height, HueVector, LayerDepth);

                case GumpCommandKind.Deferred:
                    return Target.DrawDeferred(batcher, X, Y, LayerDepth, Arg);

                default:
                    return Callback != null && Callback.Invoke(batcher);
            }
        }
    }

    /// <summary>
    /// Represents an ordered queue of GameObjects to be rendered.
    /// The order is determined by the draw order, not by the insertion order.
    /// Implementation for sorting and processing is passed as delegates.
    /// </summary>
    internal class RenderLists
    {
        private readonly List<GameObject> _tiles = [];
        private readonly List<GameObject> _stretchedTiles = [];
        private readonly List<GameObject> _statics = [];
        private readonly List<GameObject> _animations = [];
        private readonly List<GameObject> _effects = [];
        private readonly List<GameObject> _transparentObjects = [];
        private readonly List<GumpCommand> _gumpSprites = [];
        private readonly List<GumpCommand> _gumpTexts = [];

        public void Clear()
        {
            _tiles.Clear();
            _stretchedTiles.Clear();
            _statics.Clear();
            _animations.Clear();
            _effects.Clear();
            _transparentObjects.Clear();
            _gumpSprites.Clear();
            _gumpTexts.Clear();
        }

        public void Add(GameObject toRender, bool isTransparent = false)
        {
            if (isTransparent)
            {
                _transparentObjects.Add(toRender);
                return;
            }

            switch (toRender)
            {
                case Land land:
                    if (land.IsStretched)
                    {
                        _stretchedTiles.Add(toRender);
                    }
                    else
                    {
                        _tiles.Add(toRender);
                    }
                    break;

                case Static:
                case Multi:
                    _statics.Add(toRender);
                    break;

                case Mobile:
                    _animations.Add(toRender);
                    break;

                case Item item:
                    if (item.IsCorpse)
                    {
                        _animations.Add(toRender);
                    }
                    else
                    {
                        _statics.Add(toRender);
                    }
                    break;

                case GameEffect:
                    _effects.Add(toRender);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// This is an intermediate, crappy solution. Rewriting gump rendering would be way too much at this point.
        /// Adding gump elements that use atlas textures for efficient rendering.
        /// </summary>
        /// <param name="toRender"></param>
        public void AddGumpWithAtlas(Func<UltimaBatcher2D, bool> toRender)
        {
            _gumpSprites.Add(new GumpCommand(toRender));
        }

        /// <summary>Queue a typed draw (see <see cref="GumpCommand"/>'s factories) into the atlas gump layer; allocation-free.</summary>
        public void AddGumpWithAtlas(in GumpCommand command)
        {
            _gumpSprites.Add(command);
        }

        /// <summary>Queue a typed draw (see <see cref="GumpCommand"/>'s factories) into the non-atlas gump layer; allocation-free.</summary>
        public void AddGumpNoAtlas(in GumpCommand command)
        {
            _gumpTexts.Add(command);
        }

        /// <summary>
        /// Queue a <see cref="RenderedText"/> draw into the non-atlas gump layer.
        /// This is the preferred path: allocation-free (struct value), insertion-order
        /// preserved alongside <see cref="AddGumpNoAtlas(Func{UltimaBatcher2D, bool})"/>
        /// fallback entries, and flushed with a validity guard against destroyed/recycled
        /// text references.
        /// </summary>
        public void AddGumpNoAtlas(RenderedText text, int x, int y, float layerDepth, float alpha = 1f, ushort hue = 0)
        {
            if (text == null)
            {
                return;
            }

            _gumpTexts.Add(new GumpCommand(text, x, y, layerDepth, alpha, hue));
        }

        /// <summary>
        /// Fallback: queue an arbitrary draw closure into the non-atlas gump layer.
        /// Use this for compound operations (clipping, nested render lists, solid-color
        /// rectangles) that don't fit the <see cref="RenderedText"/> fast path. New code
        /// should prefer the typed overload when drawing text.
        /// </summary>
        public void AddGumpNoAtlas(Func<UltimaBatcher2D, bool> toRender)
        {
            if (toRender == null)
            {
                return;
            }

            _gumpTexts.Add(new GumpCommand(toRender));
        }

        // Test accessors. Kept internal; allow unit tests to inspect what was queued
        // without requiring a live graphics device to invoke the flush path.
        internal int GumpTextsCount => _gumpTexts.Count;
        internal GumpCommand PeekGumpText(int index) => _gumpTexts[index];

        public int DrawRenderLists(UltimaBatcher2D batcher, sbyte maxGroundZ)
        {
            int result = DrawRenderList(batcher, _tiles, maxGroundZ) +
                   DrawRenderList(batcher, _stretchedTiles, maxGroundZ) +
                   DrawRenderList(batcher, _statics, maxGroundZ) +
                   DrawRenderList(batcher, _animations, maxGroundZ) +
                   DrawRenderList(batcher, _effects, maxGroundZ);

            if (_transparentObjects.Count > 0 || _gumpSprites.Count > 0 || _gumpTexts.Count > 0)
            {
                result += DrawRenderList(batcher, _transparentObjects, maxGroundZ);
                result += DrawRenderListWithAtlas(batcher, _gumpSprites);
                result += DrawRenderListNoAtlas(batcher, _gumpTexts);
            }

            return result;
        }

        public int DrawRenderLists(UltimaBatcher2D batcher, sbyte maxGroundZ, List<Chunk> visibleChunks, int offsetX, int offsetY)
        {
            int result = 0;

            // Build visible indices for all chunks (skip rebuild if visibility unchanged)
            foreach (var chunk in visibleChunks)
            {
                var mesh = chunk.Mesh;
                if (mesh.Land.Count > 0)
                    mesh.Land.BuildVisibleIndices();
                if (mesh.Statics.Count > 0)
                    mesh.Statics.BuildVisibleIndices();
            }

            // Draw chunk mesh land tiles from GPU buffers with per-frame visibility
            batcher.SetWorldOffset(offsetX, offsetY);
            foreach (var chunk in visibleChunks)
                result += DrawMeshLayer(batcher, chunk.Mesh.Land);
            batcher.ResetWorldOffset();

            // Draw excluded land tiles (animated water, etc.)
            result += DrawRenderList(batcher, _tiles, maxGroundZ);
            result += DrawRenderList(batcher, _stretchedTiles, maxGroundZ);

            // Draw chunk mesh statics from GPU buffers with per-frame visibility
            batcher.SetWorldOffset(offsetX, offsetY);
            foreach (var chunk in visibleChunks)
                result += DrawMeshLayer(batcher, chunk.Mesh.Statics);
            batcher.ResetWorldOffset();

            // Draw excluded statics + animations + effects
            result += DrawRenderList(batcher, _statics, maxGroundZ) +
                   DrawRenderList(batcher, _animations, maxGroundZ) +
                   DrawRenderList(batcher, _effects, maxGroundZ);

            if (_transparentObjects.Count > 0 || _gumpSprites.Count > 0 || _gumpTexts.Count > 0)
            {
                //batcher.SetStencil(DepthStencilState.DepthRead);
                result += DrawRenderList(batcher, _transparentObjects, maxGroundZ);
                result += DrawRenderListWithAtlas(batcher, _gumpSprites);
                result += DrawRenderListNoAtlas(batcher, _gumpTexts);
                //batcher.SetStencil(null);
            }

            return result;
        }

        /// <summary>Statics queued so far: where the Gpu prepare's cached ground items (GameSceneGpuItemCache) go in capture order.</summary>
        public int StaticsCount => _statics.Count;

        /// <summary>CUO_GPU_ITEM_CHECK: the statics list's entry <paramref name="index"/>.</summary>
        internal GameObject StaticAt(int index) => _statics[index];

        /// <summary>CUO_GPU_ITEM_CHECK: a hash of what the walk queued, in order, leaving out <paramref name="skip"/>'s objects.</summary>
        internal ulong DebugHash(Func<GameObject, bool> skip)
        {
            ulong h = 1469598103934665603UL;

            void Mix(List<GameObject> list, ulong salt)
            {
                h = (h ^ salt) * 1099511628211UL;

                foreach (var obj in list)
                {
                    if (!skip(obj))
                    {
                        h = (h ^ (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj) ^ (ulong)obj.AlphaHue << 32 ^
                             (obj.AllowedToDraw ? 1UL << 40 : 0)) * 1099511628211UL;
                    }
                }
            }

            Mix(_statics, 1);
            Mix(_animations, 2);
            Mix(_effects, 3);
            Mix(_transparentObjects, 4);

            return h;
        }

        /// <summary>The statics-list index being captured (-1 outside that list): CUO_GPU_ITEM_CHECK's insertion check.</summary>
        internal static int CaptureStaticsIndex = -1;

        /// <summary>GPU world: items, multis, mobiles and corpses go to the world pass through their views (land and statics are baked).
        /// <paramref name="cached"/>: the scene whose cached ground items join the statics list's capture.</summary>
        public int CaptureRenderLists(UltimaBatcher2D batcher, sbyte maxGroundZ, GpuWorldRenderer gpu, GameScene cached = null)
        {
            int done = CaptureRenderList(batcher, _statics, maxGroundZ, gpu, cached, true);
            CaptureStaticsIndex = -1;

            return done + CaptureRenderList(batcher, _animations, maxGroundZ, gpu, null, false) +
                   CaptureRenderList(batcher, _transparentObjects, maxGroundZ, gpu, null, false);
        }

        /// <summary>GPU world: effects (into the world pass when it can reproduce their blend, else FNA over it) and what FNA
        /// draws over the world pass (object handles).</summary>
        public int DrawDeferredRenderLists(UltimaBatcher2D batcher, sbyte maxGroundZ, GpuWorldRenderer gpu)
        {
            int result = 0;

            foreach (var obj in _effects)
            {
                if (obj.Z > maxGroundZ)
                {
                    continue;
                }

                float depth = obj.CalculateDepthZ();
                bool drawn = obj is GameEffect effect && GpuWorldRenderer.CanCapture(effect)
                    ? gpu.DrawObject(batcher, obj, depth)
                    : obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);

                if (drawn)
                {
                    result++;
                }
            }

            if (_gumpSprites.Count > 0 || _gumpTexts.Count > 0)
            {
                result += DrawRenderListWithAtlas(batcher, _gumpSprites);
                result += DrawRenderListNoAtlas(batcher, _gumpTexts);
            }

            return result;
        }

        private static int CaptureRenderList(UltimaBatcher2D batcher, List<GameObject> renderList, sbyte maxGroundZ, GpuWorldRenderer gpu, GameScene cached, bool statics)
        {
            int done = 0;
            // FrameStats: time runs of one object kind (a clock read per object cost about 0.5 ms per frame with 20,000
            // objects in view, inflating the capture it measured)
            bool stats = FrameStats.On;
            FramePhase runPhase = FramePhase.PrepCaptureOther;
            long stamp = FrameStats.Stamp();
            // cached ground items (GameSceneGpuItemCache) go where the walk would have queued them
            int nextCached = cached?.GpuItemNextInsert ?? int.MaxValue;
            int count = renderList.Count;

            for (int i = 0; i <= count; i++)
            {
                if (i >= nextCached)
                {
                    stamp = FrameStats.AddSince(runPhase, stamp);
                    done += cached.EmitGpuItemRuns(gpu, i);
                    stamp = FrameStats.AddSince(FramePhase.PrepCaptureCached, stamp);
                    nextCached = cached.GpuItemNextInsert;
                }

                if (i == count)
                {
                    break;
                }

                var obj = renderList[i];

                if (obj is Land || obj is Static || obj.Z > maxGroundZ)
                {
                    continue;
                }

                if (statics)
                {
                    CaptureStaticsIndex = i;
                }

                if (stats)
                {
                    bool mobile = obj is Mobile, item = !mobile && obj is Item;
                    FramePhase phase = mobile ? FramePhase.PrepCaptureMobile : item ? FramePhase.PrepCaptureItem : FramePhase.PrepCaptureOther;

                    if (phase != runPhase)
                    {
                        stamp = FrameStats.AddSince(runPhase, stamp);
                        runPhase = phase;
                    }

                    FrameStats.Inc(mobile ? FrameCounter.CaptureMobiles : item ? FrameCounter.CaptureItems : FrameCounter.CaptureOther);
                }

                if (gpu.DrawObject(batcher, obj, obj.CalculateDepthZ()))
                {
                    done++;
                }
            }

            FrameStats.AddSince(runPhase, stamp);

            return done;
        }

        private static int DrawMeshLayer(UltimaBatcher2D batcher, MeshLayer layer)
        {
            if (layer.VisibleSpriteCount == 0 || layer.VertexBuffer == null || layer.VertexBuffer.IsDisposed)
                return 0;

            layer.FlushAlphaChanges();

            var indexBuffer = batcher.GetDynamicIndexBuffer(layer.VisibleSpriteCount * 6);
            layer.UploadVisibleIndices(indexBuffer);

            batcher.GraphicsDevice.SetVertexBuffer(layer.VertexBuffer);
            batcher.GraphicsDevice.Indices = indexBuffer;

            for (int i = 0; i < layer.VisibleRunCount; i++)
            {
                ref var run = ref layer.VisibleRuns[i];
                batcher.DrawDirectIndexed(run.Texture, run.Start * 6, run.Count * 2, layer.Count * 4);
            }

            return layer.VisibleSpriteCount;
        }

        private static int DrawRenderList(UltimaBatcher2D batcher, List<GameObject> renderList, sbyte maxGroundZ)
        {
            int done = 0;

            foreach (var obj in renderList)
            {
                if (obj.Z <= maxGroundZ)
                {
                    float depth = obj.CalculateDepthZ();

                    if (obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth))
                    {
                        done++;
                    }
                }
            }

            return done;
        }

        private static int DrawRenderListWithAtlas(UltimaBatcher2D batcher, List<GumpCommand> renderList)
            => DrawGumpCommands(batcher, renderList);

        private static int DrawRenderListNoAtlas(UltimaBatcher2D batcher, List<GumpCommand> renderList)
            => DrawGumpCommands(batcher, renderList);

        private static int DrawGumpCommands(UltimaBatcher2D batcher, List<GumpCommand> renderList)
        {
            int done = 0;

            // A span, not the List<T> enumerator. Commands never queue into the list being drawn (nested
            // controls such as ResizePic draw their children through their own RenderLists).
            var span = CollectionsMarshal.AsSpan(renderList);
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].Execute(batcher))
                {
                    done++;
                }
            }

            return done;
        }
    }
}
