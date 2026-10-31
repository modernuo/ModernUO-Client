// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using System;
using System.Runtime.CompilerServices;

namespace ClassicUO.Game.GameObjects
{
    internal abstract class BaseGameObject : LinkedObject
    {
        protected BaseGameObject(World world) => World = world;

        public Point RealScreenPosition;

        public World World { get; }
    }

    internal abstract partial class GameObject : BaseGameObject
    {
        protected GameObject(World world) : base(world) { }

        public bool IsDestroyed { get; protected set; }
        public bool IsPositionChanged { get; protected set; }
        public TextContainer TextContainer { get; private set; }

        /// <summary>Listed in World.TextOwners.</summary>
        public bool InTextOwners;

        /// <summary>GameScene's camera generation when the world prepare last set RealScreenPosition.</summary>
        public int ScreenPositionGen;
        private AverageOverTime _averageOverTime;

        public int Distance
        {
            get
            {
                if (
                    World.Player == null /*|| IsDestroyed*/
                )
                {
                    return ushort.MaxValue;
                }

                if (ReferenceEquals(this, World.Player))
                {
                    return 0;
                }

                int x = X,
                    y = Y;

                if (this is Mobile mobile && mobile.Steps.Count != 0)
                {
                    ref Mobile.Step step = ref mobile.Steps.Back();
                    x = step.X;
                    y = step.Y;
                }

                int fx = World.RangeSize.X;
                int fy = World.RangeSize.Y;

                return Math.Max(Math.Abs(x - fx), Math.Abs(y - fy));
            }
        }

        public virtual void Update() { }

        public abstract bool CheckMouseSelection();

        // FIXME: remove it
        public sbyte FoliageIndex = -1;
        public ushort Graphic { get; set; }
        public ushort Hue { get; set; }
        // A struct property: `obj.Offset.Z = v` does not compile (CS1612, it would write a copy).
        // Write members through OffsetX/OffsetY/OffsetZ, or assign a whole Vector3.
        private Vector3 _offset;
        public Vector3 Offset { get => _offset; set => _offset = value; }
        public float OffsetX { get => _offset.X; set => _offset.X = value; }
        public float OffsetY { get => _offset.Y; set => _offset.Y = value; }
        public float OffsetZ { get => _offset.Z; set => _offset.Z = value; }
        public short PriorityZ;
        public GameObject TNext;
        public GameObject TPrevious;
        /// <summary>The chunk whose tile list holds this object (Chunk.AddGameObject); null once removed (RemoveFromTile).</summary>
        public Chunk TileChunk;
        public ushort X { get; set; }
        public ushort Y { get; set; }
        public sbyte Z { get; set; }

        public void AddDamage(int damage)
        {
            _averageOverTime ??= new AverageOverTime(TimeSpan.FromSeconds(15));

            _averageOverTime.AddValue(Time.Ticks, damage);
        }
        public double GetCurrentDPS()
        {
            if (_averageOverTime == null)
            {
                return 0;
            }
            return Math.Round(_averageOverTime.LastAveragePerSecond, 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 GetScreenPosition()
        {
            return new Vector2(
                RealScreenPosition.X + Offset.X,
                RealScreenPosition.Y + (Offset.Y - Offset.Z)
            );
        }

        public void AddToTile()
        {
            AddToTile(X, Y);
        }

        public void AddToTile(int x, int y)
        {
            if (!IsDestroyed)
            {
                RequestFirstSightArt();
            }

            // a chunk that is not loaded yet does not load here (a teleport's packet burst used to load dozens in one
            // frame): the map keeps the object and adds it to (x, y) when the chunk loads (Map.Park)
            if (!IsDestroyed && World.Map != null && World.Map.Park(this, x, y))
            {
                if (this is Entity parked)
                {
                    World.Ecs.Sync(parked);
                }

                return;
            }

            AddToTile(World.Map?.GetChunk(x, y), x % 8, y % 8);
        }

        /// <summary>
        /// Placed on the map (AddToTile, parked or not): ground items and multi components request their art from the
        /// first-sight workers here (FirstSightArt), since the chunk loader's block scan never sees them.
        /// </summary>
        protected virtual void RequestFirstSightArt() { }

        /// <summary>The map block this object is parked on, waiting for the chunk to load (Map.Park); -1 when not parked.</summary>
        public int PendingBlock = -1;

        public void AddToTile(Chunk chunk, int chunkX, int chunkY)
        {
            RemoveFromTile();

            if (!IsDestroyed && chunk != null)
            {
                chunk.AddGameObject(this, chunkX, chunkY);
            }

            if (this is Entity entity)
            {
                World.Ecs.Sync(entity);
            }
        }

        public void RemoveFromTile()
        {
            if (TileChunk != null)
            {
                // the Gpu prepare's tile masks and item cache (GameSceneGpuItemCache) follow the tile lists
                TileChunk.OnTileContentChanged(this);
                TileChunk = null;
            }

            if (TPrevious != null)
            {
                TPrevious.TNext = TNext;
            }

            if (TNext != null)
            {
                TNext.TPrevious = TPrevious;
            }

            TNext = null;
            TPrevious = null;
            PendingBlock = -1;
        }

        public virtual void UpdateGraphicBySeason() { }

        public void UpdateScreenPosition()
        {
            IsPositionChanged = true;
            OnPositionChanged();

            if (this is Entity entity)
            {
                World.Ecs.Sync(entity);
            }
        }

        /// <summary><see cref="UpdateRealScreenPosition"/> without <see cref="UpdateTextCoordsV"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateRealScreenPositionOnly(int offsetX, int offsetY)
        {
            RealScreenPosition.X = ((X - Y) * 22) - offsetX - 22;
            RealScreenPosition.Y = ((X + Y) * 22 - (Z << 2)) - offsetY - 22;
            IsPositionChanged = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateRealScreenPosition(int offsetX, int offsetY)
        {
            RealScreenPosition.X = ((X - Y) * 22) - offsetX - 22;
            RealScreenPosition.Y = ((X + Y) * 22 - (Z << 2)) - offsetY - 22;
            IsPositionChanged = false;

            UpdateTextCoordsV();
        }

        public void SetInWorldTile(ushort x, ushort y, sbyte z)
        {
            X = x;
            Y = y;
            Z = z;

            // E11: no mirror Sync here: AddToTile below always ends in one (parked, chunkless or placed), and nothing between
            // the two reads the mirror, so the first Sync only wrote the same values the second one does
            IsPositionChanged = true;
            OnPositionChanged();
            AddToTile(x, y);
        }

        public void AddMessage(MessageType type, string message, TextType text_type)
        {
            AddMessage(
                type,
                message,
                ProfileManager.CurrentProfile.ChatFont,
                ProfileManager.CurrentProfile.SpeechHue,
                true,
                text_type
            );
        }

        // W5: TextContainer (and its Version) when every text in it was found expired; texts never un-expire (their Time is
        // set before TextContainer.Add), so this holds until a text is added
        private TextContainer _textExpiredContainer;
        private uint _textExpiredVersion;

        /// <summary>
        /// W5: true when every text of <see cref="TextContainer"/> has expired: then
        /// <see cref="UpdateTextCoordsV"/> places nothing (Mobile's and Item's skip expired texts until a live one, and
        /// FixTextCoordinatesInScreen skips expired ones). Cached until a text is added.
        /// </summary>
        public bool TextAllExpired()
        {
            TextContainer c = TextContainer;

            if (c == null)
            {
                return true;
            }

            if (ReferenceEquals(c, _textExpiredContainer) && c.Version == _textExpiredVersion)
            {
                return true;
            }

            // every text, whatever its RenderedText's state (a pooled RenderedText could come back to life): Time alone
            // decides, and it never moves
            for (TextObject t = (TextObject)c.Items; t != null; t = (TextObject)t.Next)
            {
                if (t.Time >= Time.Ticks)
                {
                    return false;
                }
            }

            _textExpiredContainer = c;
            _textExpiredVersion = c.Version;

            return true;
        }

        public virtual void UpdateTextCoordsV()
        {
            if (TextContainer == null)
            {
                return;
            }

            TextObject last = (TextObject)TextContainer.Items;

            while (last?.Next != null)
            {
                last = (TextObject)last.Next;
            }

            if (last == null)
            {
                return;
            }

            int offY = 0;

            Point p = RealScreenPosition;

            var bounds = Client.Game.UO.Arts.GetRealArtBounds(Graphic);

            p.Y -= bounds.Height >> 1;

            p.X += (int)Offset.X + 22;
            p.Y += (int)(Offset.Y - Offset.Z) + 44;

            p = Client.Game.Scene.Camera.WorldToScreen(p, true);

            for (; last != null; last = (TextObject)last.Previous)
            {
                if (last.RenderedText != null && !last.RenderedText.IsDestroyed)
                {
                    if (offY == 0 && last.Time < Time.Ticks)
                    {
                        continue;
                    }

                    last.OffsetY = offY;
                    offY += last.RenderedText.Height;

                    last.RealScreenPosition.X = p.X - (last.RenderedText.Width >> 1);
                    last.RealScreenPosition.Y = p.Y - offY;
                }
            }

            FixTextCoordinatesInScreen();
        }

        protected void FixTextCoordinatesInScreen()
        {
            if (this is Item it && SerialHelper.IsValid(it.Container))
            {
                return;
            }

            int topLeftGameWorldX = Client.Game.Scene.Camera.Bounds.X + 4;
            int topLeftGameWorldY = Client.Game.Scene.Camera.Bounds.Y;

            int bottomRightGameWorldX = Client.Game.Scene.Camera.Bounds.X + Client.Game.Scene.Camera.Bounds.Width;
            int systemChatHeight = UIManager.SystemChat.TextBoxControl.IsVisible ? UIManager.SystemChat.TextBoxControl.Height : 0;
            int bottomRightGameWorldY = Client.Game.Scene.Camera.Bounds.Y + Client.Game.Scene.Camera.Bounds.Height - systemChatHeight;
            
            for (
                TextObject item = (TextObject)TextContainer.Items;
                item != null;
                item = (TextObject)item.Next
            )
            {
                if (
                    item.RenderedText == null
                    || item.RenderedText.IsDestroyed
                    || !item.RenderedText.HasContent
                    || item.Time < Time.Ticks
                )
                {
                    continue;
                }

                int startX = item.RealScreenPosition.X;
                int endX = startX + item.RenderedText.Width;

                if (startX < topLeftGameWorldX)
                {
                    item.RealScreenPosition.X += topLeftGameWorldX - startX;
                }

                if (endX > bottomRightGameWorldX)
                {
                    item.RealScreenPosition.X -= endX - bottomRightGameWorldX;
                }

                int startY = item.RealScreenPosition.Y;
                int endY = startY + item.RenderedText.Height;

                if (startY < topLeftGameWorldY)
                {
                    item.RealScreenPosition.Y += topLeftGameWorldY - startY;
                }

                if (endY > bottomRightGameWorldY)
                {
                    item.RealScreenPosition.Y -= endY - bottomRightGameWorldY;
                }
            }
        }

        public void AddMessage(
            MessageType type,
            string text,
            byte font,
            ushort hue,
            bool isunicode,
            TextType text_type
        )
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            TextObject msg = World.MessageManager.CreateMessage(
                text,
                hue,
                font,
                isunicode,
                type,
                text_type
            );

            AddMessage(msg);
        }

        public void AddMessage(TextObject msg)
        {
            if (TextContainer == null)
            {
                TextContainer = new TextContainer();
            }

            msg.Owner = this;
            TextContainer.Add(msg);

            if (!InTextOwners && this is Entity entity)
            {
                InTextOwners = true;
                World.TextOwners.Add(entity);
            }

            if (this is Item it && SerialHelper.IsValid(it.Container))
            {
                UpdateTextCoordsV();
            }
            else
            {
                IsPositionChanged = true;
                World.WorldTextManager.AddMessage(msg);

                if (this is Land || this is Static)
                {
                    // the Gpu world prepare skips baked land and statics: walk this tile again so the text follows the camera
                    var chunk = World.Map?.GetChunk(X, Y, false);

                    if (chunk != null)
                    {
                        chunk.GpuMaskDirty = true;
                    }
                }
            }
        }

        protected virtual void OnPositionChanged() { }

        protected virtual void OnDirectionChanged() { }

        public virtual void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            Next = null;
            Previous = null;

            Clear();
            RemoveFromTile();
            TextContainer?.Clear();
            _averageOverTime = null;
            IsDestroyed = true;
            PriorityZ = 0;
            IsPositionChanged = false;
            Hue = 0;
            Offset = Vector3.Zero;
            RealScreenPosition = Point.Zero;
            IsFlipped = false;
            Graphic = 0;
            ObjectHandlesStatus = ObjectHandlesStatus.NONE;
            FrameInfo = Rectangle.Empty;
        }

        /// <summary>Retired by Chunk.Destroy into the map object recycler and not handed out again yet (MapObjectRecycler).</summary>
        public bool InRecycler;

        /// <summary>Bumped each time the object is reused for another map cell (MapObjectRecycler). A holder that keeps a map
        /// object across frames can compare it to tell a reused object from the one it kept.</summary>
        public uint Incarnation;

        /// <summary>
        /// Puts every instance field back to the constructor's value (World excepted, which never changes), and bumps
        /// <see cref="Incarnation"/>. MapObjectRecycler reuse only; Land and Static add their own fields. The unit test
        /// MapObjectRecyclerTests checks every field by reflection, so a new field must be reset here or in the subclass.
        /// </summary>
        public virtual void ResetForReuse()
        {
            // LinkedObject, BaseGameObject
            Previous = Next = Items = null;
            RealScreenPosition = Point.Zero;
            // GameObject
            IsDestroyed = false;
            IsPositionChanged = false;
            TextContainer = null;
            InTextOwners = false;
            ScreenPositionGen = 0;
            _averageOverTime = null;
            FoliageIndex = -1;
            Graphic = 0;
            Hue = 0;
            Offset = Vector3.Zero;
            PriorityZ = 0;
            TNext = TPrevious = null;
            TileChunk = null;
            X = Y = 0;
            Z = 0;
            PendingBlock = -1;
            _textExpiredContainer = null;
            _textExpiredVersion = 0;
            // views (View.cs)
            AlphaHue = 0;
            AllowedToDraw = true;
            InChunkMesh = false;
            MeshSpriteIndex = -1;
            DrawnFrame = 0;
            ObjectHandlesStatus = ObjectHandlesStatus.NONE;
            FrameInfo = Rectangle.Empty;
            IsFlipped = false;
            // recycler
            InRecycler = false;
            Incarnation++;
        }

        public static bool CanBeDrawn(World world, ushort g)
        {
            if (Client.Game == null)
                return true;

            switch (g)
            {
                case 0x0001:
                case 0x21BC:
                case 0xA1FE:
                case 0xA1FF:
                case 0xA200:
                case 0xA201:
                    //case 0x5690:
                    return false;

                case 0x9E4C:
                case 0x9E64:
                case 0x9E65:
                case 0x9E7D:
                    ref var data = ref Client.Game.UO.FileManager.TileData.StaticData[g];

                    return !data.IsBackground && !data.IsSurface;
            }

            if (g != 0x63D3)
            {
                if (g >= 0x2198 && g <= 0x21A4)
                {
                    return false;
                }

                // Easel fix.
                // In older clients the tiledata flag for this
                // item contains NoDiagonal for some reason.
                // So the next check will make the item invisible.
                if (g == 0x0F65 && Client.Game.UO.Version < ClientVersion.CV_60144)
                {
                    return true;
                }

                if (g < Client.Game.UO.FileManager.TileData.StaticData.Length)
                {
                    ref var data = ref Client.Game.UO.FileManager.TileData.StaticData[g];

                    // Hacky way to do not render "nodraw"
                    if (!string.IsNullOrEmpty(data.Name) && data.Name.StartsWith("nodraw", StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (
                        !data.IsNoDiagonal
                        || data.IsAnimated
                            && world.Player != null
                            && world.Player.Race == RaceType.GARGOYLE
                    )
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
