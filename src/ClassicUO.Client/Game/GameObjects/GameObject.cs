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
    internal abstract class BaseGameObject
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
                    ref readonly Mobile.Step step = ref mobile.Steps.Back();
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
        // T1: every A setter reports to FunnelCheck (CUO_FUNNEL_CHECK=1, CUO_DEVTOOLS builds only; the call is compiled out
        // otherwise). Writes on world objects outside a WorldWriter scope are counted as direct writes.
        private ushort _graphic, _hue;
        public ushort Graphic
        {
            get => _graphic;
            set
            {
                FunnelCheck.Write(this, FunnelField.Graphic);

                // a placed object's graphic changed in place (no tile-list change: the 0xBF/0x1B spellbook content, a
                // season swap, Static.SetGraphic): its tiledata flags are an input of the max-Z decision (mobiles are not)
                if (value != _graphic && TileChunk != null && this is not Mobile)
                {
                    TileChunk.OwnerMap?.NoteContentChanged(TileChunk.X, TileChunk.Y);
                }

                _graphic = value;
            }
        }
        public ushort Hue { get => _hue; set { FunnelCheck.Write(this, FunnelField.Hue); _hue = value; } }
        // A struct property: `obj.Offset.Z = v` does not compile (CS1612, it would write a copy).
        // Write members through OffsetX/OffsetY/OffsetZ, or assign a whole Vector3.
        private Vector3 _offset;
        public Vector3 Offset { get => _offset; set { FunnelCheck.Write(this, FunnelField.Offset); _offset = value; } }
        public float OffsetX { get => _offset.X; set { FunnelCheck.Write(this, FunnelField.OffsetX); _offset.X = value; } }
        public float OffsetY { get => _offset.Y; set { FunnelCheck.Write(this, FunnelField.OffsetY); _offset.Y = value; } }
        public float OffsetZ { get => _offset.Z; set { FunnelCheck.Write(this, FunnelField.OffsetZ); _offset.Z = value; } }
        public short PriorityZ;
        public GameObject TNext;
        public GameObject TPrevious;
        /// <summary>The chunk whose tile list holds this object (Chunk.AddGameObject); null once removed (RemoveFromTile).</summary>
        public Chunk TileChunk;
        /// <summary>The cell (x &lt;&lt; 3 | y) of <see cref="TileChunk"/> whose tile list holds this object; meaningful only while
        /// TileChunk is set. Stored because X/Y can change before the object leaves its tile (SetInWorldTile).</summary>
        public byte TileCell;
        /// <summary>T6a: the global insertion stamp of the last Chunk.AddGameObject of this object (TileOrder.NextSeq); with
        /// PriorityZ it gives the tile-list order key (TileOrder.Key).</summary>
        public ulong TileSeq;
        private ushort _x, _y;
        private sbyte _z;
        public ushort X { get => _x; set { FunnelCheck.Write(this, FunnelField.X); _x = value; } }
        public ushort Y { get => _y; set { FunnelCheck.Write(this, FunnelField.Y); _y = value; } }
        public sbyte Z { get => _z; set { FunnelCheck.Write(this, FunnelField.Z); _z = value; } }

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

        /// <summary>
        /// The object has a world placement: it is in a tile list (TileChunk) or parked until its chunk loads (PendingBlock).
        /// Movers re-add on this, not on having list neighbours: a lone object on a landless tile has none.
        /// </summary>
        public bool IsPlaced => TileChunk != null || PendingBlock >= 0;

        public void RemoveFromTile()
        {
            if (TileChunk != null)
            {
                // the Gpu prepare's tile masks and item cache (GameSceneGpuItemCache) follow the tile lists
                TileChunk.OnTileContentChanged(this);
                // Chunk.Tiles[cell] must not keep pointing at an object that left (it is how the walk finds the list)
                TileChunk.ReleaseAnchor(this);
                // T6b part 3: the index alone holds the dynamics; their TNext/TPrevious stay null, so the unlink below and
                // ReleaseAnchor, which never sees them as the anchor, are no-ops
                if (TileChunk.Dynamic != null && DynamicTileIndex.Holds(this))
                {
                    TileChunk.Dynamic.Remove(this, TileCell);
                }
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
            msg.OwnerIncarnation = Incarnation;
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
            // BaseGameObject
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
            TileCell = 0;
            TileSeq = 0;
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
            PickStamp = 0;
            PickSlot = 0;
            ObjectHandlesStatus = ObjectHandlesStatus.NONE;
            FrameInfo = Rectangle.Empty;
            IsFlipped = false;
            // recycler
            InRecycler = false;
            Incarnation++;
        }

        /// <summary>Whether a static, item, multi component or effect graphic is drawn. The rules are data: the pack's
        /// tiledata draw flags (<see cref="Uo.Pack.TilePackFlags"/>); only the player's race is decided here.</summary>
        public static bool CanBeDrawn(World world, ushort g)
        {
            ClassicUO.Assets.StaticTiles[] data = Client.Game != null ? Client.Game.UO.FileManager.TileData.StaticData : TestStaticData;

            if (data == null)
                return true;

            return CanBeDrawn(data, g, PlayerIsGargoyle(world));
        }

        /// <summary><see cref="CanBeDrawn(World, ushort)"/> with the player's race already read: a caller that tests many
        /// graphics in one synchronous pass (a chunk's statics, a multi's components) reads it once, since each
        /// <see cref="Mobile.Race"/> read is an ECS lookup.</summary>
        public static bool CanBeDrawn(ushort g, bool playerIsGargoyle)
        {
            ClassicUO.Assets.StaticTiles[] data = Client.Game != null ? Client.Game.UO.FileManager.TileData.StaticData : TestStaticData;

            if (data == null)
                return true;

            return CanBeDrawn(data, g, playerIsGargoyle);
        }

        /// <summary>The only player input of <see cref="CanBeDrawn(World, ushort)"/>. Game thread only (the
        /// race is an unsynchronized ECS read); the player is read once, so a logout between two
        /// reads cannot see it go null.</summary>
        public static bool PlayerIsGargoyle(World world)
        {
            PlayerMobile player = world.Player;

            return player != null && player.Race == RaceType.GARGOYLE;
        }

        /// <summary>Unit tests only: the static tile data <see cref="CanBeDrawn(World, ushort)"/> reads when there is no
        /// running client (per thread, so parallel tests don't see it).</summary>
        [ThreadStatic] internal static ClassicUO.Assets.StaticTiles[] TestStaticData;

        internal static bool CanBeDrawn(ClassicUO.Assets.StaticTiles[] staticData, ushort g, bool playerIsGargoyle) =>
            g < staticData.Length && Uo.Pack.TileDraw.Static(staticData[g].PackFlags, playerIsGargoyle);
    }
}
