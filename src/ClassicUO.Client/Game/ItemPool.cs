// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
#if CUO_DEVTOOLS
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ClassicUO.Utility.Logging;
#endif

namespace ClassicUO.Game
{
    /// <summary>
    /// <c>item_pool</c>: reuses the <see cref="Item"/> objects World drops (out of range, deleted by the server, swept) for the
    /// items that enter range next (<see cref="World.GetOrCreateItem(uint, out bool)"/>). Walking the large scene used to
    /// create and drop thousands of items (~4.3 MB per 15 s of ls_walk), which drove gen0 collections.
    ///
    /// Lifetime rules (a reused item that something still holds would show another item's state under the old reference):
    /// <list type="number">
    /// <item>Only World's removals from <see cref="World.Items"/> offer items (<see cref="Offer"/>): the sweep, a forced removal
    /// and GetOrCreateItem's replacement of a destroyed item. Detached items (character creation, the multi placement preview,
    /// spellbook entries) never reach the pool.</item>
    /// <item>Offered items wait in a pending list until the end of the item stage (<see cref="Commit"/>), where an item is
    /// retired only if it is destroyed and no longer listed anywhere World keeps items: not held by an item table, no
    /// active-list entry, no tile list or parked block, no overhead text owner entry, and the mirror's serial index does not
    /// hand it out. The selection holders (<see cref="SelectedObject"/>) that still name it are cleared there.</item>
    /// <item>A retired item stays destroyed for at least <see cref="QuarantineMs"/> before <see cref="Take"/> hands it out (FIFO,
    /// oldest first), so per-frame holders and holders that test IsDestroyed have dropped it; Take checks the same rules again
    /// and drops an item that fails them.</item>
    /// <item>Reuse starts from the constructor's state: <see cref="Item.ResetForReuse"/> resets every instance field and bumps
    /// <see cref="GameObject.Incarnation"/> (a reflection unit test fails when a new field is not reset).</item>
    /// </list>
    /// At most <see cref="MaxPooled"/> items are kept (about 2.3 MB at 280 B per item); more are left to the collector.
    /// CUO_ITEM_POOL_CHECK=1 (developer builds) compares every reused item's fields with a new Item's after the reset and
    /// checks the rules again at reuse; it logs <c>[item-pool-check] mismatch</c> lines and a summary every 5 s.
    /// </summary>
    internal sealed class ItemPool
    {
        public const int MaxPooled = 8192;
        public const uint QuarantineMs = 2000;

        private struct Entry
        {
            public Item Item;
            public uint Tick;
        }

        private readonly World _world;
        private readonly List<Item> _pending = new List<Item>();
        private readonly Queue<Entry> _queue = new Queue<Entry>();
        private bool _closed;

        /// <summary>Session counts (the checker's summary and the tests): offered by World, retired into quarantine, refused
        /// at commit (still listed somewhere), handed out again, dropped at take (listed again), left out (pool full).</summary>
        public long Offered, Retired, Refused, Reused, Dropped, Overflow;

        public ItemPool(World world) => _world = world;

        public int Count => _queue.Count;

        public int PendingCount => _pending.Count;

        /// <summary>World removed <paramref name="item"/> from its item table; it becomes a candidate at the next <see cref="Commit"/>.</summary>
        public void Offer(Item item)
        {
            if (_closed || item == null || item.InItemPool || !item.IsDestroyed)
            {
                return;
            }

            item.InItemPool = true;
            _pending.Add(item);
            Offered++;
        }

        /// <summary>End of the item stage: the pending items that nothing World knows of holds go into quarantine.</summary>
        public void Commit(uint now)
        {
#if CUO_DEVTOOLS
            if (CheckOn)
            {
                Summary(now);
            }
#endif

            if (_pending.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _pending.Count; i++)
            {
                Item item = _pending[i];

                if (!Free(item, out _))
                {
                    item.InItemPool = false;
                    Refused++;

                    continue;
                }

                ScrubSelection(item);

                if (_queue.Count >= MaxPooled)
                {
                    item.InItemPool = false;
                    Overflow++;

                    continue;
                }

                _queue.Enqueue(new Entry { Item = item, Tick = now });
                Retired++;
            }

            _pending.Clear();
        }

        /// <summary>The oldest retired item past its quarantine, reset to the constructor's state (null: allocate one).</summary>
        public Item Take(uint now)
        {
            while (_queue.Count != 0 && now - _queue.Peek().Tick >= QuarantineMs)
            {
                Item item = _queue.Dequeue().Item;
                item.InItemPool = false;

                if (!Free(item, out string why))
                {
                    // something listed it again while it waited (it never should): leave it to the collector
                    Dropped++;
#if CUO_DEVTOOLS
                    if (CheckOn)
                    {
                        Mismatch($"pooled item 0x{item.Serial:X8} (incarnation {item.Incarnation}) listed again: {why}");
                    }
#endif

                    continue;
                }

                ScrubSelection(item);
                item.ResetForReuse();
                Reused++;
#if CUO_DEVTOOLS
                if (CheckOn)
                {
                    CheckReset(item);
                }
#endif

                return item;
            }

            return null;
        }

        /// <summary>World.Clear: the pending items go (the pool itself stays: same World, bounded).</summary>
        public void ClearPending()
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                _pending[i].InItemPool = false;
            }

            _pending.Clear();
        }

        /// <summary>Terminal release (World.ReleasePools): keeps nothing from now on.</summary>
        public void Close()
        {
            _closed = true;
            ClearPending();

            while (_queue.Count != 0)
            {
                _queue.Dequeue().Item.InItemPool = false;
            }
        }

        // the rules of retirement: destroyed and listed nowhere World keeps items
        private bool Free(Item item, out string why)
        {
            why = null;

            if (!ReferenceEquals(item.World, _world))
            {
                why = "another World's item";
            }
            else if (!item.IsDestroyed)
            {
                why = "alive";
            }
            else if (item.HeldBy != null)
            {
                why = "held by an item table";
            }
            else if (item.ActiveEntryId != 0)
            {
                why = "in the active item list";
            }
            else if (item.TileChunk != null || item.PendingBlock >= 0)
            {
                why = "placed (tile list or parked)";
            }
            else if (item.InTextOwners)
            {
                why = "in World.TextOwners";
            }
            else if (_world.Ecs != null && ReferenceEquals(_world.Ecs.Mirror.GetHandle(item.Serial), item))
            {
                why = "the mirror's serial index hands it out";
            }

            return why == null;
        }

        private static void ScrubSelection(Item item)
        {
            if (ReferenceEquals(SelectedObject.Object, item))
            {
                SelectedObject.Object = null;
            }

            if (ReferenceEquals(SelectedObject.LastLeftDownObject, item))
            {
                SelectedObject.LastLeftDownObject = null;
            }

            if (ReferenceEquals(SelectedObject.HealthbarObject, item))
            {
                SelectedObject.HealthbarObject = null;
            }

            if (ReferenceEquals(SelectedObject.SelectedContainer, item))
            {
                SelectedObject.SelectedContainer = null;
            }

            if (ReferenceEquals(SelectedObject.CorpseObject, item))
            {
                SelectedObject.CorpseObject = null;
            }
        }

        // ---- the switch and CUO_ITEM_POOL_CHECK ----

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ITEM_POOL=1|0) and the checker
        private static readonly bool s_on = Environment.GetEnvironmentVariable("CUO_ITEM_POOL") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings.ItemPool
        };

        private static readonly bool s_check = Environment.GetEnvironmentVariable("CUO_ITEM_POOL_CHECK") == "1";
#else
        private static readonly bool s_on = Configuration.Settings.GlobalSettings.ItemPool;
#endif

        /// <summary>The setting (read once at startup; World.ItemPoolOverride wins in tests).</summary>
        public static bool SettingOn => s_on;

#if CUO_DEVTOOLS
        /// <summary>Tests: the checker for every pool (null: <c>CUO_ITEM_POOL_CHECK=1</c>).</summary>
        internal static bool? CheckOverride;

        public static bool CheckOn => CheckOverride ?? s_check;

        /// <summary>Checker counts over the session: reused items compared, fields or rules found different.</summary>
        public static long Checks, Mismatches;

        /// <summary>Tests: sees each mismatch line.</summary>
        internal static Action<string> MismatchObserver;

        private Item _fresh;
        private uint _nextLog;

        private void CheckReset(Item item)
        {
            Checks++;
            _fresh ??= new Item(_world);

            foreach (string field in ResetDifferences(_fresh, item))
            {
                Mismatch($"reused item (incarnation {item.Incarnation}): {field} differs from a new Item's");
            }

            if (ReferenceEquals(SelectedObject.Object, item) || ReferenceEquals(SelectedObject.LastLeftDownObject, item))
            {
                Mismatch($"reused item (incarnation {item.Incarnation}) still selected");
            }
        }

        private void Summary(uint now)
        {
            if (now < _nextLog)
            {
                return;
            }

            _nextLog = now + 5000;
            Log.Info($"[item-pool-check] item_pool=on, offered {Offered}, retired {Retired}, refused {Refused}, reused {Reused}, dropped {Dropped}, overflow {Overflow}, pooled {_queue.Count}; checks {Checks}, mismatches {Mismatches}");
        }

        private static void Mismatch(string what)
        {
            Mismatches++;
            MismatchObserver?.Invoke(what);

            if (Mismatches <= 20)
            {
                Log.Warn($"[item-pool-check] mismatch: {what}");
            }
        }

        /// <summary>
        /// The instance fields (World and Incarnation excepted) on which <paramref name="actual"/> differs from
        /// <paramref name="expected"/>: values compared with Equals, references by identity. Reflection over the four declaring
        /// types named here, so NativeAOT keeps their fields' metadata.
        /// </summary>
        internal static IEnumerable<string> ResetDifferences(Item expected, Item actual)
        {
            foreach (FieldInfo f in Fields)
            {
                if (f.FieldType == typeof(World) || f.Name == nameof(GameObject.Incarnation))
                {
                    continue;
                }

                object e = f.GetValue(expected), a = f.GetValue(actual);

                if (f.FieldType.IsValueType ? !Equals(e, a) : !ReferenceEquals(e, a))
                {
                    yield return $"{f.DeclaringType?.Name}.{f.Name} ({Show(a)}, new: {Show(e)})";
                }
            }
        }

        // a value's text for the mismatch line; a stale ECS entity's ToString can throw (its store is gone)
        private static string Show(object o)
        {
            try
            {
                return o?.ToString() ?? "null";
            }
            catch (Exception ex)
            {
                return $"<{o.GetType().Name}: {ex.GetType().Name}>";
            }
        }

        private static FieldInfo[] _fields;

        internal static FieldInfo[] Fields => _fields ??= CollectFields();

        private static FieldInfo[] CollectFields()
        {
            var list = new List<FieldInfo>();
            AddFields(typeof(Item), list);
            AddFields(typeof(Entity), list);
            AddFields(typeof(GameObject), list);
            AddFields(typeof(BaseGameObject), list);

            return list.ToArray();
        }

        private static void AddFields(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] Type t,
            List<FieldInfo> list)
        {
            list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }
#endif
    }
}
