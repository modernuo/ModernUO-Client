// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Data;

namespace ClassicUO.Game.GameObjects
{
    /// <summary>
    /// T9 child-list slice A: the clothes of the character creation's detached preview mobile, by layer. The preview is no
    /// world entity (no ECS record), so <c>Entity.FindItemByLayer</c> reads this table for it instead of the child list.
    /// The gump wears at most one item per layer (it takes the old one off first); the first item worn on a layer is the
    /// one found, as the list's first match was. <c>CUO_EQUIP_READ_CHECK</c> compares each read with the list.
    /// </summary>
    internal sealed class PreviewEquipment
    {
        private readonly Item[] _layers = new Item[(int)Layer.Bank + 1];
        private int _count;

        public bool Any => _count != 0;

        public Item Get(Layer layer) => (uint)layer < (uint)_layers.Length ? _layers[(int)layer] : null;

        public void Wear(Item item)
        {
            if (item == null || (uint)item.Layer >= (uint)_layers.Length || _layers[(int)item.Layer] != null)
            {
                return;
            }

            _layers[(int)item.Layer] = item;
            _count++;
        }

        public void TakeOff(Item item)
        {
            if (item != null && (uint)item.Layer < (uint)_layers.Length && ReferenceEquals(_layers[(int)item.Layer], item))
            {
                _layers[(int)item.Layer] = null;
                _count--;
            }
        }

        public void Clear()
        {
            System.Array.Clear(_layers);
            _count = 0;
        }
    }
}
