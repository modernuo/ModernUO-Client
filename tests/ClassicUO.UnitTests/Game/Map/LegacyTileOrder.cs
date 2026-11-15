using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;

namespace ClassicUO.UnitTests.Game.Map
{
    /// <summary>
    /// The legacy tile list order (every object of a tile, the dynamics included), the tests' reference since T9 deleted the
    /// <c>world_tile_index</c> off path in which the client's list held everything. <see cref="ShadowTileList"/> is the
    /// former CUO_TILE_ORDER_CHECK shadow: AddGameObject's old insertion rule applied verbatim, kept by a test that sees every
    /// placement. <see cref="Of"/> rebuilds the same order from a tile's current objects by inserting them by that rule in
    /// stamp order: the rule places a new object before exactly the objects it stops at, and in a key-sorted list those form
    /// a suffix that depends on each object alone, so objects that left in between do not change the others' order.
    /// </summary>
    internal static class LegacyTileOrder
    {
        /// <summary>AddGameObject's local insertion state: 0 land, 1 a multi that goes before equal others, 2 a preview multi,
        /// -1 everything else.</summary>
        public static sbyte State(GameObject obj) => obj switch
        {
            Land => 0,
            Multi m when (m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL) != 0 => 1,
            Multi m when (m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW) != 0 => 2,
            Multi => 1,
            _ => -1
        };

        /// <summary>The tile's list objects (from its head by TNext) and its index entries, unordered.</summary>
        public static List<GameObject> Objects(Chunk chunk, int cell)
        {
            var all = new List<GameObject>();

            for (TileListObject o = chunk.HeadAt(cell); o != null; o = o.TNext)
            {
                all.Add(o);
            }

            if (chunk.Dynamic != null)
            {
                foreach (DynamicTileIndex.Entry e in chunk.Dynamic.Cell(cell))
                {
                    all.Add(e.Obj);
                }
            }

            return all;
        }

        /// <summary>The legacy list order of tile <paramref name="cell"/> of <paramref name="chunk"/>.</summary>
        public static List<GameObject> Of(Chunk chunk, int cell)
        {
            var shadow = new ShadowTileList();

            foreach (GameObject o in Objects(chunk, cell).OrderBy(o => o.TileSeq))
            {
                shadow.Add(o, cell, o.PriorityZ, State(o));
            }

            return shadow.Cell(cell) ?? new List<GameObject>();
        }
    }

    /// <summary>
    /// Per tile, every object in the legacy list order: AddGameObject's old insertion rule applied verbatim (the scan from the
    /// head stops at the first object with a higher stored priority, or an equal one when the new object is land, or a
    /// state-1 multi and the other is not land; the new object goes there), emptied by identity. An independent copy of the
    /// old list, derived from neither the index nor the T6a key.
    /// </summary>
    internal sealed class ShadowTileList
    {
        private readonly List<GameObject>[] _cells = new List<GameObject>[64];

        public List<GameObject> Cell(int cell) => _cells[cell];

        public void Add(GameObject obj, int cell, short priorityZ, sbyte state)
        {
            List<GameObject> list = _cells[cell] ??= new List<GameObject>(4);
            int i = 0;

            for (; i < list.Count; i++)
            {
                GameObject o = list[i];
                int testPriorityZ = o.PriorityZ;

                if (testPriorityZ > priorityZ || testPriorityZ == priorityZ && (state == 0 || state == 1 && !(o is Land)))
                {
                    break;
                }
            }

            list.Insert(i, obj);
        }

        public bool Remove(GameObject obj, int cell)
        {
            List<GameObject> list = _cells[cell];

            if (list == null)
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], obj))
                {
                    list.RemoveAt(i);

                    return true;
                }
            }

            return false;
        }
    }
}
