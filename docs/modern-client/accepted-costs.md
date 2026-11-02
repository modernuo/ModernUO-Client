# Accepted costs ledger

Every change merged with a measured or known cost in time or memory, kept for the final performance audit at the end of the PoC. Each cost is small on its own; added together they could lower the frame rate.

The owner's rule (2026-11-02): a cost can be fine when it buys a feature, correctness or the ECS architecture. It still has to be listed here, so the final audit can check each area and decide whether to revisit it. Agents and the coordinator add a row whenever a merge accepts a cost, including "within budget" ones, and mark a row **Revisited** when a later change removes it.

Columns: what costs, how much (measured or estimated, with the scene), what it buys, where to look first if it has to go.

## Frame time

| Merge | Cost | Buys | Revisit idea |
|---|---|---|---|
| T7b part 1 (extract split) | +24 ns per captured item in `Gather` (ECS read; micro-bench, 201 rounds): about 1 % of Gpu world_prepare at the large scene, zoom 2.5; negligible at Britain. Quiet timing parked. | Extract reads the ECS, not the GameObject: a prerequisite for deleting the caches in T9 | **Partly revisited (02c0ef387):** EcsRead halves it (30–40 → 16–22 ns per item over the old field reads) |
| T4 (thin handles) | An ECS read is 9–25 ns against 1–5 ns from the cached field. Hot fields stay cached; cold fields (e.g. a status bar's 4 reads, +23–37 ns) read the ECS at UI rates. | One source of truth for world state | Same helper; keep hot fields cached until then |
| Friflo `GetComponent` (systemic) | 6–9 ns per single read in 4.0.0-preview.2 (virtual heap); the "25 ns" was inside a larger record read. **Partly revisited (02c0ef387):** the hottest per-frame reads use `EcsRead` (3.8–5.3 ns; five reads through a row 24 → 12.6 ns). `WorldMirror`/`WorldWriter` still call Friflo. | — | A stable Friflo release (main has no virtual heap): see docs/dev/ecs-friflo.md |
| T9 child-list slice B fix chain | Attach-time walks: `WouldCycle` (parent chain) and `SubtreeDeeperThan` (moved subtree) on each attach packet; `WorldMirror.ParentOf` probes up to 3 components per level. Packets only, nothing per frame. | No server packet can loop the container graph or crash the client | Cache parent and subtree height per record |

## Memory

| Merge | Cost | Buys | Revisit idea |
|---|---|---|---|
| T4 (thin handles) | About +32 B per Item (the 16-byte Friflo handle plus fields): about 0.8 MB per client at 25k items | Handle-based ECS access | Shrinks once T9 deletes the cached fields |
| T6b (tile index) | `TNext`/`TPrevious` 16 B per item, mobile and effect, now always null | — (left over) | Move the links to a list-node base for Land/Static/Multi, or a side table |
| T9 child-list slice B | `LinkedObject` fields on Item/Mobile unused: 24 B per object, about 2–5 MB per client | — (left over) | Move `LinkedObject` off `BaseGameObject` (backlog) |
| EcsRead (02c0ef387) | Depends on Friflo 4.0.0-preview.2 internals (a field offset, `[UnsafeAccessor]` names) until the upgrade; the fallback branch costs +0.25 ns per row read at 25k entities; a thread-static scratch copy per special heap type per thread (none today) | Faster ECS reads | Delete or thin the helper at the Friflo upgrade |
| perf-11 | Corpse layer tables: 8 × `Item[256]`, about 17 KB per client; can keep destroyed corpse items alive until reused | Corpse layer reads without a scan (about 500 scans → 0 per frame in the corpse scene) | Cache serials instead of Items, or clear on destroy |

For pools, caches, prefill and atlases with their MB cost, see [memory-footprint.md](memory-footprint.md).
