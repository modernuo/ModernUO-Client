// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections;
using System.Collections.Generic;

namespace ClassicUO.Game.GameObjects
{
    /// <summary>
    /// E10: <c>World.Mobiles</c> with an order of its own. It replaces the <c>Dictionary&lt;uint, Mobile&gt;</c> the world used to
    /// hold, and enumerates in exactly the order that dictionary enumerated: entries sit in slots, a removed entry's slot goes
    /// on a LIFO free list, an insert takes the most recently freed slot (else appends), replacing the value of an existing
    /// key keeps its slot, and <see cref="Clear"/> starts over. Every <see cref="Mobile"/> carries its slot in
    /// <see cref="Mobile.Ordinal"/>, so sorting any set of mobiles by it gives the enumeration order with no walk of the
    /// table (E4 used to read <c>Dictionary._version</c> through <c>UnsafeAccessor</c> and re-walk it after each insert).
    /// <para>
    /// The due-mobile update order, tile lists, picking ties and footstep sounds depend on that order, so it must not drift
    /// from the dictionary's: <c>MobileTableTests</c> run random operations against a real dictionary.
    /// </para>
    /// Single-threaded, like the world. Removing, overwriting and clearing during enumeration are allowed (as with the dictionary); inserting a new key
    /// invalidates active enumerators (they throw <see cref="InvalidOperationException"/> on the next step).
    /// </summary>
    internal sealed class MobileTable
    {
        private readonly Dictionary<uint, Mobile> _byKey = new Dictionary<uint, Mobile>();
        private Mobile[] _slots = new Mobile[64];
        private int _count; // slots ever used since the last Clear (the dictionary's _count)
        private readonly Stack<int> _free = new Stack<int>();
        private int _version; // bumped by every insert of a new key (the dictionary's enumerators threw on those, and only those)

        public int Count => _byKey.Count;

        public ValueCollection Values => new ValueCollection(this);

        public bool TryGetValue(uint serial, out Mobile mobile) => _byKey.TryGetValue(serial, out mobile);

        public Mobile Get(uint serial)
        {
            _byKey.TryGetValue(serial, out Mobile m);

            return m;
        }

        public bool Contains(uint serial) => _byKey.ContainsKey(serial);

        public bool ContainsKey(uint serial) => _byKey.ContainsKey(serial);

        /// <summary>Gets the mobile under <paramref name="serial"/> (throws if absent) or inserts/replaces it.</summary>
        public Mobile this[uint serial]
        {
            get => _byKey[serial];
            set
            {
                if (_byKey.TryGetValue(serial, out Mobile old))
                {
                    if (ReferenceEquals(old, value))
                    {
                        return;
                    }

                    CheckUnowned(value);

                    // the dictionary overwrites the value in place: the slot stays
                    value.Ordinal = old.Ordinal;
                    value.OwnerTable = this;
                    value.TableKey = serial;
                    old.OwnerTable = null;
                    _slots[value.Ordinal] = value;
                    _byKey[serial] = value;
                }
                else
                {
                    CheckUnowned(value);
                    Insert(serial, value);
                }
            }
        }

        /// <summary>Adds <paramref name="mobile"/> under its serial unless that serial is taken.</summary>
        public bool Add(Mobile mobile)
        {
            if (_byKey.ContainsKey(mobile.Serial))
            {
                return false;
            }

            CheckUnowned(mobile);
            Insert(mobile.Serial, mobile);

            return true;
        }

        public bool Remove(uint serial)
        {
            if (!_byKey.Remove(serial, out Mobile m))
            {
                return false;
            }

            int slot = m.Ordinal;
            m.OwnerTable = null;
            _slots[slot] = null;
            _free.Push(slot);

            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                if (_slots[i] != null)
                {
                    _slots[i].OwnerTable = null;
                }
            }

            _byKey.Clear();
            Array.Clear(_slots, 0, _count);
            _count = 0;
            _free.Clear();
        }

        /// <summary>T9 slice 3: whether this table holds <paramref name="mobile"/> itself under <paramref name="serial"/> (the
        /// same answer as <c>ReferenceEquals(Get(serial), mobile)</c>), from the mobile's own table marks: no lookup.</summary>
        public bool Holds(Mobile mobile, uint serial) => ReferenceEquals(mobile.OwnerTable, this) && mobile.TableKey == serial;

        // Ordinal is the mobile's storage address, so a mobile may sit in one table under one key only: a second key or a
        // second table would let one removal free the slot the other still points at. Checked before any change.
        private static void CheckUnowned(Mobile mobile)
        {
            if (mobile.OwnerTable != null)
            {
                throw new InvalidOperationException("a Mobile can be in one MobileTable under one key only");
            }
        }

        private void Insert(uint serial, Mobile mobile)
        {
            _version++;
            mobile.OwnerTable = this;
            int slot;

            if (_free.Count != 0)
            {
                slot = _free.Pop();
            }
            else
            {
                if (_count == _slots.Length)
                {
                    Array.Resize(ref _slots, _count * 2);
                }

                slot = _count++;
            }

            mobile.Ordinal = slot;
            mobile.TableKey = serial;
            _slots[slot] = mobile;
            _byKey[serial] = mobile;
        }

        /// <summary>The mobiles in slot order (the old dictionary's enumeration order).</summary>
        public readonly struct ValueCollection : IEnumerable<Mobile>
        {
            private readonly MobileTable _table;

            internal ValueCollection(MobileTable table) => _table = table;

            public Enumerator GetEnumerator() => new Enumerator(_table);

            IEnumerator<Mobile> IEnumerable<Mobile>.GetEnumerator() => new Enumerator(_table);

            IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_table);
        }

        public struct Enumerator : IEnumerator<Mobile>
        {
            private readonly MobileTable _table;
            private readonly int _version;
            private int _index;
            private Mobile _current;

            internal Enumerator(MobileTable table)
            {
                _table = table;
                _version = table._version;
                _index = 0;
                _current = null;
            }

            public Mobile Current => _current;

            object IEnumerator.Current => _current;

            public bool MoveNext()
            {
                // removals, overwrites and Clear during the walk are fine (a removed slot is null); an insert of a new key
                // throws, as the dictionary's enumerator did
                if (_version != _table._version)
                {
                    throw new InvalidOperationException("the table was modified during enumeration");
                }

                while (_index < _table._count)
                {
                    Mobile m = _table._slots[_index++];

                    if (m != null)
                    {
                        _current = m;

                        return true;
                    }
                }

                _current = null;

                return false;
            }

            public void Reset()
            {
                if (_version != _table._version)
                {
                    throw new InvalidOperationException("the table was modified during enumeration");
                }

                _index = 0;
                _current = null;
            }

            public void Dispose()
            {
            }
        }
    }
}
