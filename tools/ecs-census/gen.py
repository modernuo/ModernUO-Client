"""Generate the data sections of ecs-state-census.md from refs2.json (A fields), refsm2.json (mutators, E members) and a textual scan."""
import os as _os
SCRIPTS = _os.path.dirname(_os.path.abspath(__file__))
CENSUS_ROOT = _os.environ.get("CENSUS_ROOT", "").replace("\\", "/").rstrip("/") + "/"
import json, re, os, collections, sys
sys.path.insert(0, os.path.dirname(__file__))
from areas import area

ROOT = CENSUS_ROOT
PFX = "src/ClassicUO.Client/"
exec(open(_os.path.join(SCRIPTS, "names.py")).read())
A_NAMES = NAMES - {"Title"}
d = [r for r in json.load(open("refs2.json")) if r["member"] != "Title"]
for r in d:
    r["rel"] = r["file"][len(PFX):]

WORLD = {"Item", "Mobile", "PlayerMobile", "Entity"}
MAPT = {"Land", "Static", "Multi", "GameEffect"}

# receiver type of a bare reference = the class the file declares
BARE_CLASS = {
    "Game/GameObjects/Entity.cs": "Entity", "Game/GameObjects/Item.cs": "Item", "Game/GameObjects/Mobile.cs": "Mobile",
    "Game/GameObjects/PlayerMobile.cs": "PlayerMobile", "Game/GameObjects/GameObject.cs": "GameObject",
    "Game/GameObjects/MobileAnimation.cs": "Mobile", "Game/GameObjects/Views/MobileView.cs": "Mobile",
    "Game/GameObjects/Views/ItemView.cs": "Item", "Game/GameObjects/Views/View.cs": "GameObject",
    "Game/GameObjects/Land.cs": "Land", "Game/GameObjects/Static.cs": "Static", "Game/GameObjects/Multi.cs": "Multi",
    "Game/GameObjects/Views/LandView.cs": "Land", "Game/GameObjects/Views/StaticView.cs": "Static",
    "Game/GameObjects/Views/MultiView.cs": "Multi", "Game/GameObjects/GameEffect.cs": "GameEffect",
    "Game/GameObjects/MovingEffect.cs": "GameEffect", "Game/GameObjects/FixedEffect.cs": "GameEffect",
    "Game/GameObjects/DragEffect.cs": "GameEffect", "Game/GameObjects/LightningEffect.cs": "GameEffect",
    "Game/GameObjects/Views/GameEffectView.cs": "GameEffect", "Game/GameObjects/Views/LightningEffectView.cs": "GameEffect",
}
for r in d:
    if r["rtype"] is None:
        r["rtype"] = BARE_CLASS.get(r["rel"], "?")
        r["bare"] = True

# field class (A) and owner
OWNER = {}
for n in "Graphic Hue Offset X Y Z".split():
    OWNER[n] = "GameObject"
for n in "AnimIndex ExecuteAnimation LastAnimationChangeTime Flags Hits HitsMax LastStepTime Name Serial Direction".split():
    OWNER[n] = "Entity"
for n in "Amount Container IsDamageable Layer LightID Price UsedLayer DisplayedGraphic IsMulti".split():
    OWNER[n] = "Item"
for n in "AnimationFrameCount AnimationFromServer IsRenamable LastStepSoundTime IsFemale IsRunning NotorietyFlag IsDead InWarMode Race SpeedMode Stamina StaminaMax Mana ManaMax StepSoundOffset".split():
    OWNER[n] = "Mobile"

ORDER = ["Graphic", "Hue", "X", "Y", "Z", "Offset", "Direction", "Flags", "Serial", "Name", "Hits", "HitsMax", "Amount", "Container", "Layer",
         "Price", "IsDamageable", "LightID", "UsedLayer", "DisplayedGraphic", "IsMulti", "AnimIndex", "ExecuteAnimation",
         "LastAnimationChangeTime", "LastStepTime", "AnimationFrameCount", "AnimationFromServer", "LastStepSoundTime", "StepSoundOffset",
         "Race", "SpeedMode", "Stamina", "StaminaMax", "Mana", "ManaMax", "IsRenamable", "IsFemale", "IsRunning", "NotorietyFlag", "IsDead", "InWarMode"]
assert set(ORDER) == set(A_NAMES), set(ORDER) ^ set(A_NAMES)

WRITEKINDS = {"assign", "compound", "struct-member", "incdec", "ref/out", "deconstruct?"}


def is_write(r):
    return r["kind"] in WRITEKINDS


def kindname(r):
    return {"assign": "=", "compound": "op=", "struct-member": ".sub=", "incdec": "++/--"}.get(r["kind"], r["kind"])


out = []
w = out.append

# ---------------- summary table ----------------
w("| Field | Owner | World writes | Map/effect writes | Reads: hot (views / scenes / map / ecs / entity / world) | Reads: network | Reads: cold (ui / managers / other) | Reads: dev/checkers |")
w("|---|---|---:|---:|---:|---:|---:|---:|")
tot_w = tot_mw = 0
for f in ORDER:
    rs = [r for r in d if r["member"] == f]
    ww = [r for r in rs if is_write(r) and r["rtype"] in WORLD | {"GameObject", "?"}]
    mw = [r for r in rs if is_write(r) and r["rtype"] in MAPT]
    rd = [r for r in rs if not is_write(r)]
    cnt = collections.Counter(area(r["rel"])[1] for r in rd)
    tot_w += len(ww)
    tot_mw += len(mw)
    w(f"| `{f}` | {OWNER[f]} | {len(ww)} | {len(mw)} | {cnt['hot']} | {cnt['net']} | {cnt['cold']} | {cnt['dev']} |")
w(f"| **total** | | **{tot_w}** | **{tot_mw}** | | | | |")
w("")
SUMMARY = "\n".join(out)
out.clear()

# ---------------- write sites ----------------
w("Sites are `file:line`, relative to `src/ClassicUO.Client/`. `(T)` is the receiver's static type; a bare name inside the class is the class itself. `=` plain assignment, `op=` compound (`|=`, `&=`, `+=`), `.sub=` assignment to a member of the struct (`Offset.X = `).")
w("")
for f in ORDER:
    ws = [r for r in d if r["member"] == f and is_write(r)]
    if not ws:
        w(f"**`{f}`** ({OWNER[f]}): no writes outside the declaring class' own setter body.")
        w("")
        continue
    wor = [r for r in ws if r["rtype"] in WORLD | {"GameObject", "?"}]
    mp = [r for r in ws if r["rtype"] in MAPT]
    w(f"**`{f}`** ({OWNER[f]}): {len(wor)} world-object write site(s)" + (f", {len(mp)} on map/effect objects (not ECS)" if mp else ""))
    for label, lst in (("", wor), ("map/effect: ", mp)):
        by = collections.OrderedDict()
        for r in sorted(lst, key=lambda r: (r["rel"], r["line"])):
            by.setdefault(r["rel"], []).append(r)
        for fl, rs in by.items():
            items = []
            for r in rs:
                t = r["rtype"] if r["rtype"] != BARE_CLASS.get(fl) else "this"
                k = kindname(r)
                items.append(f"{r['line']} ({t}{'' if k == '=' else ' ' + k})")
            w(f"- {label}`{fl}`: " + ", ".join(items))
    w("")
WRITES = "\n".join(out)
out.clear()

# ---------------- readers by file ----------------
w("Reads of each A field per file, grouped hot (per-frame, per-object code) and cold. Counts are references, a line with two is counted twice.")
w("")
for f in ORDER:
    rs = [r for r in d if r["member"] == f and not is_write(r)]
    byf = collections.Counter(r["rel"] for r in rs)
    groups = collections.OrderedDict()
    for fl, n in sorted(byf.items()):
        a, h = area(fl)
        groups.setdefault((h, a), []).append((fl, n))
    w(f"**`{f}`**: {len(rs)} reads")
    for h in ("hot", "net", "cold", "dev"):
        for (hh, a), lst in groups.items():
            if hh != h:
                continue
            w(f"- {h}, {a} ({sum(n for _, n in lst)}): " + ", ".join(f"`{fl.split('/')[-1]}` {n}" for fl, n in lst))
    w("")
READERS = "\n".join(out)
out.clear()

open("gen_summary.md", "w", encoding="utf-8").write(SUMMARY)
open("gen_writes.md", "w", encoding="utf-8").write(WRITES)
open("gen_readers.md", "w", encoding="utf-8").write(READERS)
print(len(SUMMARY), len(WRITES), len(READERS))
print(SUMMARY)
