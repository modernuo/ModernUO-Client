import os as _os
SCRIPTS = _os.path.dirname(_os.path.abspath(__file__))
CENSUS_ROOT = _os.environ.get("CENSUS_ROOT", "").replace("\\", "/").rstrip("/") + "/"
import re, json, collections
f = CENSUS_ROOT + "src/ClassicUO.Client/Network/PacketHandlers.cs"
txt = open(f, encoding="utf-8-sig", errors="replace").read()
L = txt.split("\n")
names = []
for i, l in enumerate(L, 1):
    m = re.match(r"\s+(?:private|public|internal)\s+static\s+\w+\s+(\w+)\s*\(", l)
    if m:
        names.append((i, m.group(1)))
ids = collections.defaultdict(list)
for m in re.finditer(r"Handler\.Add\((0x[0-9A-Fa-f]+),\s*(\w+)\)", txt):
    ids[m.group(2)].append(m.group(1).lower())
d = json.load(open("refs2.json"))
ws = [r for r in d if r["file"].endswith("Network/PacketHandlers.cs") and r["kind"] in ("assign", "compound", "struct-member") and r["member"] != "Title"]
by = collections.OrderedDict()
for r in sorted(ws, key=lambda r: r["line"]):
    name = [n for i, n in names if i <= r["line"]][-1]
    by.setdefault(name, []).append(r)
out = ["| Handler | Packets | Sites | Lines | Fields written |", "|---|---|---:|---|---|"]
for n, lst in by.items():
    lines = f"{lst[0]['line']}-{lst[-1]['line']}" if lst[0]['line'] != lst[-1]['line'] else str(lst[0]['line'])
    fields = collections.OrderedDict()
    for r in lst:
        fields[r["member"]] = None
    SHARED = {"UpdateGameObject": "shared by 0x1a, 0x77, 0xd2, 0x78, 0xd3, 0xf3, 0xf6 (UpdateItem, UpdateCharacter, UpdateObject, UpdateItemSA, BoatMoving)",
              "AddItemToContainer": "called by UpdateContainedItem (0x25), DenyMoveItem (0x27, line 1622) and UpdateContainedItems (0x3c)"}
    p = SHARED.get(n) or ", ".join(sorted(set(ids.get(n, [])))) or "-"
    out.append(f"| `{n}` | {p} | {len(lst)} | {lines} | {', '.join(fields)} |")
out.append(f"| **total** | | **{len(ws)}** | | |")
open("gen_handlers.md", "w", encoding="utf-8").write("\n".join(out))
print("\n".join(out))
# callers of UpdateGameObject
for m in re.finditer(r"UpdateGameObject\(", txt):
    ln = txt.count("\n", 0, m.start()) + 1
    nm = [n for i, n in names if i <= ln][-1]
    print("UpdateGameObject called at", ln, "in", nm)
