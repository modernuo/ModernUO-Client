// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ClassicUO.Game.UI.Controls
{
    // Composition prevents a List<T> cast from bypassing invalidation on public child-list mutations.
    internal sealed class ControlChildren : Collection<Control>
    {
        private readonly Control _owner;
        public ControlChildren(Control owner) : base(new List<Control>()) => _owner = owner;

        protected override void InsertItem(int index, Control item)
        {
            base.InsertItem(index, item);
            _owner.InvalidateDraw();
        }

        protected override void RemoveItem(int index)
        {
            base.RemoveItem(index);
            _owner.InvalidateDraw();
        }

        protected override void SetItem(int index, Control item)
        {
            base.SetItem(index, item);
            _owner.InvalidateDraw();
        }

        protected override void ClearItems()
        {
            if (Count == 0) return;
            base.ClearItems();
            _owner.InvalidateDraw();
        }

        // Collection<T>'s interface enumerator would box List<T>.Enumerator in every control walk.
        public new List<Control>.Enumerator GetEnumerator() => ((List<Control>)Items).GetEnumerator();

        public void RemoveRange(int index, int count)
        {
            ((List<Control>)Items).RemoveRange(index, count);
            if (count != 0) _owner.InvalidateDraw();
        }

        public int RemoveAll(Predicate<Control> match)
        {
            int removed = ((List<Control>)Items).RemoveAll(match);
            if (removed != 0) _owner.InvalidateDraw();
            return removed;
        }

        public void ForEach(Action<Control> action) => ((List<Control>)Items).ForEach(action);
        public List<Control> FindAll(Predicate<Control> match) => ((List<Control>)Items).FindAll(match);
        public List<T> ConvertAll<T>(Converter<Control, T> convert) => ((List<Control>)Items).ConvertAll(convert);
    }
}
