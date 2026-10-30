"""Second rename pass: mutator methods, structure members (E), the LinkedObject child list API, Steps.
Usage: instrument2.py <repo-root>   (revert with git checkout -- src)"""
import re, sys

root = sys.argv[1]
G = root + "/src/ClassicUO.Client/Game/"

METHODS = {
    "GameObjects/GameObject.cs": ["AddToTile", "RemoveFromTile", "UpdateScreenPosition", "SetInWorldTile"],
    "GameObjects/Entity.cs": ["FixHue", "UpdateHits"],
    "GameObjects/Mobile.cs": ["SetSAPoison", "ClearSteps", "EnqueueStep", "SetAnimation", "SetIdleAnimation", "ProcessSteps"],
    "LinkedObject.cs": ["PushToBack", "Remove", "Unlink", "Insert", "MoveToFront", "MoveToBack", "Clear", "GetLast", "SortContents"],
}
FIELDS = {
    "GameObjects/GameObject.cs": [r"public short (PriorityZ);", r"public GameObject (TNext);", r"public GameObject (TPrevious);",
                                  r"public Chunk (TileChunk);", r"public int (PendingBlock) = -1;"],
    "GameObjects/Mobile.cs": [r"public Deque<Step> (Steps) \{ get; \}"],
    "LinkedObject.cs": [r"public LinkedObject Previous, Next, (Items);"],
}

for f in set(METHODS) | set(FIELDS):
    p = G + f
    t = open(p, encoding="utf-8-sig").read()
    for name in METHODS.get(f, []):
        rx = re.compile(r"(\b(?:public|internal|protected|private)\s+(?:override\s+|virtual\s+|static\s+)*[\w<>\[\],?]+\s+)" + name + r"(\s*(?:<\w+>)?\()")
        t, n = rx.subn(r"\1Zz_" + name + r"\2", t)
        if n == 0:
            print("NO METHOD", f, name)
    for rx in FIELDS.get(f, []):
        m = re.search(rx, t)
        if not m:
            print("NO FIELD", f, rx)
            continue
        s, e = m.span(1)
        t = t[:s] + "Zz_" + t[s:e] + t[e:]
    open(p, "w", encoding="utf-8").write(t)
