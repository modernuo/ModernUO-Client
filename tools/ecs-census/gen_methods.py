"""Section 4 of the census from the second compiler pass (refsm.json)."""
import os as _os
SCRIPTS = _os.path.dirname(_os.path.abspath(__file__))
CENSUS_ROOT = _os.environ.get("CENSUS_ROOT", "").replace("\\", "/").rstrip("/") + "/"
import json, re, collections, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
PF = "src/ClassicUO.Client/"
ROOT = CENSUS_ROOT
d = json.load(open("refsm2.json"))
for r in d:
    r["rel"] = r["file"][len(PF):]

BARE_CLASS = {
    "Game/GameObjects/GameObject.cs": "GameObject", "Game/GameObjects/Entity.cs": "Entity", "Game/GameObjects/Item.cs": "Item",
    "Game/GameObjects/Mobile.cs": "Mobile", "Game/GameObjects/PlayerMobile.cs": "PlayerMobile",
    "Game/GameObjects/MobileAnimation.cs": "Mobile", "Game/GameObjects/Views/MobileView.cs": "Mobile",
    "Game/GameObjects/Views/ItemView.cs": "Item", "Game/GameObjects/Views/View.cs": "GameObject",
    "Game/LinkedObject.cs": "LinkedObject", "Game/GameObjects/EntityTextContainer.cs": "TextContainer",
}
for r in d:
    if r["rtype"] is None:
        r["rtype"] = BARE_CLASS.get(r["rel"], "(this: " + r["rel"].split("/")[-1] + ")")

REMOVE_OVERRIDE = {
    ("Game/World.cs", 1019): "Entity", ("Network/PacketHandlers.cs", 1139): "Entity",
    ("Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs", 971): "PlayerMobile",
    ("Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs", 982): "PlayerMobile",
}
for r in d:
    if r["member"] == "Remove" and (r["rel"], r["line"]) in REMOVE_OVERRIDE:
        r["rtype"] = REMOVE_OVERRIDE[(r["rel"], r["line"])]
WORLD_CLASS_FILES = ("Game/GameObjects/GameObject.cs", "Game/GameObjects/Entity.cs", "Game/GameObjects/Item.cs", "Game/GameObjects/Mobile.cs", "Game/GameObjects/PlayerMobile.cs")
src_cache = {}


def src(f):
    if f not in src_cache:
        src_cache[f] = open(ROOT + f, encoding="utf-8-sig", errors="replace").read().split("\n")
    return src_cache[f]


def text_after(r):
    L = src(r["file"])[r["line"] - 1]
    i = L.find(r["member"], max(0, r["col"] - 3))
    return L[i + len(r["member"]):] if i >= 0 else ""


def is_decl(r):
    L = src(r["file"])[r["line"] - 1]
    return bool(re.match(r"\s*(public|internal|private|protected)\b", L)) and ("(" in L) and not ("=" in L.split("(")[0])


WORLD = {"Item", "Mobile", "PlayerMobile", "Entity", "GameObject", "BaseGameObject"}
out = []
w = out.append

# ---- mutator methods ----
MUT = ["SetInWorldTile", "AddToTile", "RemoveFromTile", "UpdateScreenPosition", "FixHue", "UpdateHits", "SetSAPoison", "SetAnimation",
       "SetIdleAnimation", "EnqueueStep", "ClearSteps", "ProcessSteps"]
DESC = {
    "SetInWorldTile": "writes X, Y, Z, then `AddToTile` (-> mirror Sync)",
    "AddToTile": "tile insertion (-> `Chunk.AddGameObject`, or `Map.Park` for an unloaded chunk) then mirror Sync",
    "RemoveFromTile": "tile removal; clears `TileChunk`, `PendingBlock`, `TNext`, `TPrevious`",
    "UpdateScreenPosition": "sets `IsPositionChanged`, mirror Sync",
    "FixHue": "writes Hue (masks the server hue)",
    "UpdateHits": "writes `HitsPercentage` (and its text) from a percentage",
    "SetSAPoison": "writes `_isSA_Poisoned`, pushes `MobileInputs`",
    "SetAnimation": "writes the whole animation timer (AnimIndex, AnimationFrameCount, `_animation*`, LastAnimationChangeTime, idle due)",
    "SetIdleAnimation": "writes the animation timer for an idle fidget (draws the shared RNG)",
    "EnqueueStep": "appends to `Steps`, writes LastStepTime, may call `SetAnimation`",
    "ClearSteps": "clears `Steps`, zeroes `Offset`, pushes `MobileInputs`",
    "ProcessSteps": "pops/advances `Steps`, writes X, Y, Z, Direction, IsRunning, Offset, LastStepTime, calls `AddToTile`",
}
w("### 4.1 Methods that write world state")
w("")
w("Call sites outside the declaring class are listed with their receiver type; calls inside `GameObject`/`Entity`/`Item`/`Mobile`/`PlayerMobile` are listed as `this`. Declarations are not listed.")
w("")
for m in MUT:
    rs = [r for r in d if r["member"] == m and not is_decl(r)]
    w(f"**`{m}`**: {len(rs)} call site(s); {DESC[m]}")
    by = collections.OrderedDict()
    for r in sorted(rs, key=lambda r: (r["rel"], r["line"])):
        by.setdefault(r["rel"], []).append(r)
    for fl, lst in by.items():
        items = []
        for r in lst:
            t = r["rtype"]
            if t == BARE_CLASS.get(fl):
                t = "this"
            items.append(f"{r['line']} ({t})")
        w(f"- `{fl}`: " + ", ".join(items))
    w("")

# ---- structure fields ----
w("### 4.2 Tile-list structure (E): who writes it")
w("")
OPS = re.compile(r"\s*(=(?![=>])|\+=|-=)")
for m in ["PriorityZ", "TNext", "TPrevious", "TileChunk", "PendingBlock"]:
    rs = [r for r in d if r["member"] == m]
    wr = [r for r in rs if OPS.match(text_after(r))]
    rd = [r for r in rs if r not in wr]
    w(f"**`{m}`**: {len(wr)} write(s), {len(rd)} read(s)")
    by = collections.OrderedDict()
    for r in sorted(wr, key=lambda r: (r["rel"], r["line"])):
        by.setdefault(r["rel"], []).append(r)
    for fl, lst in by.items():
        w(f"- write `{fl}`: " + ", ".join(f"{r['line']} ({r['rtype'] if r['rtype'] != BARE_CLASS.get(fl) else 'this'})" for r in lst))
    byr = collections.Counter(r["rel"] for r in rd)
    w("- reads: " + ", ".join(f"`{f.split('/')[-1]}` {n}" for f, n in sorted(byr.items())))
    w("")

# ---- Steps ----
w("### 4.3 `Mobile.Steps` (the walk queue)")
w("")
MUTATORS = ("AddToBack", "AddToFront", "RemoveFromBack", "RemoveFromFront", "Clear", "Insert", "RemoveAt", "PopFront", "PopBack", "Back", "Front", "Remove", "Enqueue", "Dequeue")
rs = [r for r in d if r["member"] == "Steps"]
mut, rd = [], []
for r in rs:
    after = text_after(r)
    m = re.match(r"\s*\.\s*(\w+)", after)
    (mut if m and m.group(1) in MUTATORS else rd).append((r, m.group(1) if m else ("[]" if after.strip().startswith("[") else "")))
w(f"{len(rs)} references: {len(mut)} call a mutating or ref-returning method (`Back()` and `Front()` return `ref Step`, so they can write), {len(rd)} read (`Count`, indexer, enumeration).")
w("")
by = collections.OrderedDict()
for r, m in sorted(mut, key=lambda x: (x[0]["rel"], x[0]["line"])):
    by.setdefault(r["rel"], []).append((r, m))
for fl, lst in by.items():
    w(f"- mutating `{fl}`: " + ", ".join(f"{r['line']} (`{m}`, {r['rtype'] if r['rtype'] != BARE_CLASS.get(fl) else 'this'})" for r, m in lst))
byr = collections.Counter(r["rel"] for r, m in rd)
w("- reading: " + ", ".join(f"`{f.split('/')[-1]}` {n}" for f, n in sorted(byr.items())))
w("")

# ---- child lists ----
w("### 4.4 Child lists (`Items` and the `LinkedObject` API) on world objects")
w("")
w("`Items` is the head of an entity's child list: a mobile's equipment, an item's contents. Writers of the list are the `LinkedObject` methods; readers walk `Items`/`Next`. Receivers that are not `Item`/`Mobile`/`PlayerMobile`/`Entity`/`GameObject` (`TextContainer`, `EffectManager`, `MacroManager`, `Macro`, `MacroObject`) are other lists and are not in the ECS plan.")
w("")
LL = ["PushToBack", "Insert", "Remove", "Unlink", "MoveToFront", "MoveToBack", "Clear", "SortContents", "GetLast"]
for m in LL:
    rs = [r for r in d if r["member"] == m and not is_decl(r) and (r["rtype"] in WORLD or r["rel"] in WORLD_CLASS_FILES)]
    if not rs:
        continue
    w(f"**`{m}`**: {len(rs)} call site(s) on world objects")
    by = collections.OrderedDict()
    for r in sorted(rs, key=lambda r: (r["rel"], r["line"])):
        by.setdefault(r["rel"], []).append(r)
    for fl, lst in by.items():
        w(f"- `{fl}`: " + ", ".join(f"{r['line']} ({r['rtype'] if r['rtype'] != BARE_CLASS.get(fl) else 'this'})" for r in lst))
    w("")
rs = [r for r in d if r["member"] == "Items" and (r["rtype"] in WORLD or r["rel"] in WORLD_CLASS_FILES)]
wr = [r for r in rs if OPS.match(text_after(r))]
rd = [r for r in rs if r not in wr]
w(f"**`Items`** (the child list head): {len(wr)} write(s), {len(rd)} read(s) on world objects")
for r in sorted(wr, key=lambda r: (r["rel"], r["line"])):
    w(f"- write `{r['rel']}:{r['line']}` ({r['rtype']})")
byr = collections.Counter(r["rel"] for r in rd)
for h, label in (("hot", ("Views", "Scenes", "Map", "Ecs", "GameObjects")), ("cold", ())):
    pass
w("- reads by file: " + ", ".join(f"`{f.split('/')[-1]}` {n}" for f, n in sorted(byr.items())))
w("")
open("gen_methods.md", "w", encoding="utf-8").write("\n".join(out))
print(len(d), "refs;", len("\n".join(out)), "chars")
