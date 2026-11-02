// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// T7b part 2b: the pick state of the mobiles and items the dynamic-sprite extract drew in the last world draw, keyed by
    /// object. Picking (MobileView/ItemView.CheckMouseSelection) runs in the list fill, before the frame's draw, so it reads
    /// the previous draw's state: a mobile's frame rectangle (FrameInfo) and mirror bit, an art item's drawn graphic and
    /// draw offset (the art entry's Width/Height), a corpse's mirror bit. Filled by the Legacy sink (<c>legacy_ecs_sprites</c>)
    /// and the Gpu world's extract path (<c>gpu_ecs_sprites</c>); objects the last draw didn't record (drawn by their view,
    /// served from the Gpu item cache, drawn on an EcsParity frame, not drawn) and items whose graphic changed since read the
    /// GameObject side effects as before, which the draws still write: the mirror bit is GetAnimDirection history the
    /// animation system reads, FrameInfo is read by the player's circle and the drag select, the art entry by statics.
    /// Two buffers: a draw writes one and <see cref="Commit"/> makes it the one read, so a draw that isn't committed (the
    /// Gpu item check's probe pass records nothing) leaves the picks of the frame's own fill on the last committed draw.
    /// No allocation once the buffers have grown to the scene's drawn objects. CUO_PICK_RECORDS_CHECK=1 compares every
    /// record-served pick state with the side effect it replaces.
    /// </summary>
    internal static class SpritePickTable
    {
        internal enum Kind : byte
        {
            None,
            Mobile,
            Item,
            Corpse
        }

        internal struct Entry
        {
            public GameObject Obj;
            public uint Incarnation;
            public Kind Kind;
            public bool Mirror;
            public bool HasArt;
            public ushort ArtGraphic;
            public int ArtW, ArtH;
            public Rectangle Frame;
        }

        private static Entry[] _write = new Entry[256], _read = new Entry[256];
        private static int _writeCount, _readCount;
        private static uint _writeStamp, _readStamp;
        private static bool _open;

        /// <summary>DevBench (pickscan's pick_records mode) and tests: false = picking ignores the records.</summary>
        internal static bool? ReadOverride;

        /// <summary>Start of a world draw: the write buffer is emptied.</summary>
        public static void BeginDraw()
        {
            Array.Clear(_write, 0, _writeCount);
            _writeCount = 0;

            // stamp 0 is "never recorded" (a new or reused object)
            if (++_writeStamp == 0)
            {
                _writeStamp = 1;
            }

            _open = true;
        }

        /// <summary>End of a world draw: its records become the ones picking reads.</summary>
        public static void Commit()
        {
            if (!_open)
            {
                return;
            }

            _open = false;
            int stale = _readCount;
            (_read, _write) = (_write, _read);
            _readCount = _writeCount;
            _readStamp = _writeStamp;
            // the old read buffer's entries are cleared at the next BeginDraw (they hold object references)
            _writeCount = stale;

#if CUO_DEVTOOLS
            CheckBeat();
#endif
        }

        /// <summary>Tests: back to the start state.</summary>
        internal static void Reset()
        {
            Array.Clear(_write);
            Array.Clear(_read);
            _writeCount = _readCount = 0;
            _writeStamp = _readStamp = 0;
            _open = false;
            ReadOverride = null;
        }

        private static ref Entry Slot(GameObject obj)
        {
            if (obj.PickStamp == _writeStamp && (uint)obj.PickSlot < (uint)_writeCount && ReferenceEquals(_write[obj.PickSlot].Obj, obj))
            {
                // drawn twice in one draw: the last draw's state stands, as the side effects' would
                return ref _write[obj.PickSlot];
            }

            if (_writeCount == _write.Length)
            {
                Array.Resize(ref _write, _write.Length * 2);
            }

            obj.PickStamp = _writeStamp;
            obj.PickSlot = _writeCount;
            ref Entry e = ref _write[_writeCount++];
            e = default;
            e.Obj = obj;
            e.Incarnation = obj.Incarnation;

            return ref e;
        }

        /// <summary>A mobile drawn from the extract: its frame rectangle and mirror bit (what the draw writes to FrameInfo and
        /// ViewFlipped).</summary>
        public static void RecordMobile(GameObject mobile, Rectangle frame, bool mirror)
        {
            if (!_open)
            {
                return;
            }

            ref Entry e = ref Slot(mobile);
            e.Kind = Kind.Mobile;
            e.Frame = frame;
            e.Mirror = mirror;
        }

        /// <summary>An item drawn from the extract: the art graphic it drew and that art's draw offset (an art item; none for
        /// missing art, whose entry write the view discards), or the corpse's mirror bit.</summary>
        public static void RecordItem(GameObject item, bool corpse, bool mirror, bool hasArt, ushort artGraphic, int artW, int artH)
        {
            if (!_open)
            {
                return;
            }

            ref Entry e = ref Slot(item);
            e.Kind = corpse ? Kind.Corpse : Kind.Item;
            e.Mirror = mirror;
            e.HasArt = hasArt;
            e.ArtGraphic = artGraphic;
            e.ArtW = artW;
            e.ArtH = artH;
        }

        /// <summary><see cref="RecordItem(GameObject, bool, bool, bool, ushort, int, int)"/> from the extract's last select.</summary>
        public static void RecordItem(GameObject item, ItemSpriteExtract extract)
        {
            if (_open && (extract.SetsFlipped || extract.HasArtPick))
            {
                RecordItem(item, extract.SetsFlipped, extract.Flipped, extract.HasArtPick, extract.ArtPickGraphic, extract.ArtPickW, extract.ArtPickH);
            }
        }

        private static bool Find(GameObject obj, Kind kind, out int slot)
        {
            slot = obj.PickSlot;

            return ReadOverride != false
                && obj.PickStamp == _readStamp
                && _readStamp != 0
                && (uint)slot < (uint)_readCount
                && ReferenceEquals(_read[slot].Obj, obj)
                && _read[slot].Incarnation == obj.Incarnation
                && _read[slot].Kind == kind;
        }

        /// <summary>A mobile's pick rectangle (FrameInfo) and mirror bit from the last draw's records. False: read the
        /// GameObject's.</summary>
        public static bool TryMobile(GameObject mobile, out Rectangle frame, out bool mirror)
        {
            if (!Find(mobile, Kind.Mobile, out int slot))
            {
                frame = default;
                mirror = false;
#if CUO_DEVTOOLS
                Count(false, Kind.Mobile);
#endif

                return false;
            }

            ref readonly Entry e = ref _read[slot];
            frame = e.Frame;
            mirror = e.Mirror;
#if CUO_DEVTOOLS
            Count(true, Kind.Mobile);

            if (Check)
            {
                Compare(mobile, Kind.Mobile, frame != mobile.FrameInfo || mirror != mobile.ViewFlipped, frame, mirror, mobile.FrameInfo, mobile.ViewFlipped);
            }
#endif

            return true;
        }

        /// <summary>A corpse's mirror bit from the last draw's records. False: read the GameObject's.</summary>
        public static bool TryCorpse(GameObject corpse, out bool mirror)
        {
            if (!Find(corpse, Kind.Corpse, out int slot))
            {
                mirror = false;
#if CUO_DEVTOOLS
                Count(false, Kind.Corpse);
#endif

                return false;
            }

            mirror = _read[slot].Mirror;
#if CUO_DEVTOOLS
            Count(true, Kind.Corpse);

            if (Check)
            {
                Compare(corpse, Kind.Corpse, mirror != corpse.ViewFlipped, default, mirror, default, corpse.ViewFlipped);
            }
#endif

            return true;
        }

        /// <summary>The draw offset of art <paramref name="graphic"/> for an item from the last draw's records, when the item
        /// drew that graphic. False: read the art entry (<paramref name="side"/>, compared by the checker).</summary>
        public static bool TryItemArt(GameObject item, ushort graphic, in ArtEntry side, out int width, out int height)
        {
            if (!Find(item, Kind.Item, out int slot) || !_read[slot].HasArt || _read[slot].ArtGraphic != graphic)
            {
                width = height = 0;
#if CUO_DEVTOOLS
                Count(false, Kind.Item);
#endif

                return false;
            }

            ref readonly Entry e = ref _read[slot];
            width = e.ArtW;
            height = e.ArtH;
#if CUO_DEVTOOLS
            Count(true, Kind.Item);

            if (Check)
            {
                Compare(item, Kind.Item, width != side.Width || height != side.Height, new Rectangle(0, 0, width, height), false, new Rectangle(0, 0, side.Width, side.Height), false);
            }
#endif

            return true;
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_PICK_RECORDS_CHECK=1, the record-served pick state against the side effect it replaces
        internal static bool Check = Environment.GetEnvironmentVariable("CUO_PICK_RECORDS_CHECK") == "1";

        private const int MAX_LOGGED = 40;
        private static long _mobiles, _items, _corpses, _fallbacks, _mismatched;
        private static int _logged;
        private static uint _nextSummary;

        internal static long Mismatched => _mismatched;

        private static void Count(bool records, Kind kind)
        {
            if (!Check)
            {
                return;
            }

            if (!records)
            {
                _fallbacks++;
            }
            else if (kind == Kind.Mobile)
            {
                _mobiles++;
            }
            else if (kind == Kind.Item)
            {
                _items++;
            }
            else
            {
                _corpses++;
            }
        }

        private static void Compare(GameObject obj, Kind kind, bool differs, Rectangle got, bool gotMirror, Rectangle want, bool wantMirror)
        {
            if (!differs)
            {
                return;
            }

            _mismatched++;

            if (_logged++ < MAX_LOGGED)
            {
                Log.Warn($"[pick-records] mismatch {kind} 0x{(obj as Entity)?.Serial ?? 0:X8} graphic 0x{obj.Graphic:X4}: records {got} mirror {gotMirror}, side effect {want} mirror {wantMirror}");
            }
        }

        private static void CheckBeat()
        {
            if (Check && Time.Ticks >= _nextSummary)
            {
                _nextSummary = Time.Ticks + 5000;
                Summary();
            }
        }

        /// <summary>The checker's counts: <c>[pick-records] picks from records … , fallback F, mismatched M</c>.</summary>
        public static void Summary()
        {
            if (!Check)
            {
                return;
            }

            Log.Info($"[pick-records] picks from records {_mobiles + _items + _corpses} (mobiles {_mobiles}, items {_items}, corpses {_corpses}), " +
                     $"fallback {_fallbacks}, last draw recorded {_readCount}; mismatched {_mismatched}");
        }
#endif
    }
}
