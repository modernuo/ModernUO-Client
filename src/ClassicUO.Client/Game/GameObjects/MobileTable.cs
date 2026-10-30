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
    /// The legacy mobile loop, tile lists, picking ties and footstep sounds depend on that order, so it must not drift from the
    /// dictionary's: <c>MobileTableTests</c> run random operations against a real dictionary, and with
    /// <c>CUO_MOBILE_UPDATE_CHECK</c> a shadow dictionary receives every operation and is compared every frame
    /// (<see cref="VerifyAgainstShadow"/>).
    /// </para>
    /// Single-threaded, like the world. Removing during enumeration is allowed (as with the dictionary); adding during one is
    /// not supported.
    /// </summary>
    internal sealed class MobileTable
    {
        private readonly Dictionary<uint, Mobile> _byKey = new Dictionary<uint, Mobile>();
        private Mobile[] _slots = new Mobile[64];
        private int _count; // slots ever used since the last Clear (the dictionary's _count)
        private readonly Stack<int> _free = new Stack<int>();
        private Dictionary<uint, Mobile> _shadow;

        /// <summary>
        /// Keeps a real dictionary beside the table, fed every operation, for <see cref="VerifyAgainstShadow"/>. Only on an
        /// empty table (the shadow's order comes from seeing every operation).
        /// </summary>
        public void EnableShadow()
        {
            if (_byKey.Count != 0)
            {
                throw new InvalidOperationException("the shadow must see every operation");
            }

            _shadow ??= new Dictionary<uint, Mobile>();
        }

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
                    // the dictionary overwrites the value in place: the slot stays
                    value.Ordinal = old.Ordinal;
                    _slots[old.Ordinal] = value;
                    _byKey[serial] = value;
                }
                else
                {
                    Insert(serial, value);
                }

                if (_shadow != null)
                {
                    _shadow[serial] = value;
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

            Insert(mobile.Serial, mobile);

            if (_shadow != null)
            {
                _shadow[mobile.Serial] = mobile;
            }

            return true;
        }

        public bool Remove(uint serial)
        {
            if (!_byKey.Remove(serial, out Mobile m))
            {
                return false;
            }

            int slot = m.Ordinal;
            _slots[slot] = null;
            _free.Push(slot);
            _shadow?.Remove(serial);

            return true;
        }

        public void Clear()
        {
            _byKey.Clear();
            Array.Clear(_slots, 0, _count);
            _count = 0;
            _free.Clear();
            _shadow?.Clear();
        }

        private void Insert(uint serial, Mobile mobile)
        {
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
            _slots[slot] = mobile;
            _byKey[serial] = mobile;
        }

        /// <summary>
        /// Checker: the table's enumeration against the shadow dictionary's (same mobiles, same order) and every
        /// <see cref="Mobile.Ordinal"/> against the slot. Returns the number of differences (0 = identical).
        /// </summary>
        public int VerifyAgainstShadow()
        {
            if (_shadow == null)
            {
                return 0;
            }

            int diffs = _shadow.Count == _byKey.Count ? 0 : 1;
            IEnumerator<Mobile> expected = _shadow.Values.GetEnumerator();

            for (int i = 0; i < _count; i++)
            {
                Mobile m = _slots[i];

                if (m == null)
                {
                    continue;
                }

                if (m.Ordinal != i || !expected.MoveNext() || !ReferenceEquals(expected.Current, m))
                {
                    diffs++;
                }
            }

            if (expected.MoveNext())
            {
                diffs++;
            }

            return diffs;
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
            private int _index;
            private Mobile _current;

            internal Enumerator(MobileTable table)
            {
                _table = table;
                _index = 0;
                _current = null;
            }

            public Mobile Current => _current;

            object IEnumerator.Current => _current;

            public bool MoveNext()
            {
                // removals during the walk null their slot; _count is read live (adds are not supported mid-walk)
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
                _index = 0;
                _current = null;
            }

            public void Dispose()
            {
            }
        }
    }
}
