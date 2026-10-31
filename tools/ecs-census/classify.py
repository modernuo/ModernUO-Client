import os as _os
SCRIPTS = _os.path.dirname(_os.path.abspath(__file__))
CENSUS_ROOT = _os.environ.get("CENSUS_ROOT", "").replace("\\", "/").rstrip("/") + "/"
import json, re, collections, sys
INP = sys.argv[1] if len(sys.argv) > 1 else "refs.json"
OUTP = sys.argv[2] if len(sys.argv) > 2 else "refs2.json"
root = CENSUS_ROOT
d = json.load(open(INP))["refs"]
cache = {}


def src(f):
    if f not in cache:
        cache[f] = open(root + f, encoding="utf-8-sig").read().split("\n")
    return cache[f]


OP = re.compile(r"\s*(=(?![=>])|\+=|-=|\*=|/=|%=|&=|\|=|\^=|<<=|>>=|\?\?=|\+\+|--)")


def kind(r):
    k = kind0(r)
    # T0c: OffsetX/Y/Z accessor writes are the old `Offset.X op` member writes
    return "struct-member" if r.get("sub") and k in ("assign", "compound", "incdec") else k


def kind0(r):
    tok = r.get("tok", r["member"])
    L = src(r["file"])[r["line"] - 1]
    c0 = r["col"] - 1
    m = re.match(r"\w+", L[c0:])
    # r['col'] may point at '.', the analyzer already moved it, but recompute robustly
    while c0 < len(L) and not (L[c0:].startswith(tok)):
        c0 += 1
    end = c0 + len(tok)
    after = L[end:]
    before = L[:c0]
    if after.strip() == "" and r["line"] < len(src(r["file"])):
        after = src(r["file"])[r["line"]].strip()
    sm = re.match(r"\s*\.\s*\w+\s*(=(?![=>])|\+=|-=|\*=|/=|\+\+|--)", after)
    if sm and r["member"] == "Offset":
        return "struct-member"
    mo = OP.match(after)
    if mo:
        op = mo.group(1)
        if op == "=":
            return "assign"
        if op in ("++", "--"):
            return "incdec"
        return "compound"
    bm = re.search(r"(\+\+|--)\s*[\w\.\?\(\)\[\]!]*$", before)
    if bm:
        return "incdec"
    if not re.match(r"\s*\?(?!\.)", after) and re.search(r"\b(ref|out)\s+(?:[\w\?!]+(?:\[[^\]]*\])?\s*\.\s*)*$", before):
        return "ref/out"
    # tuple deconstruction / swap: `(a.X, a.Y) = ...`
    if re.match(r"\s*[,)]", after) and re.search(r"\)\s*=(?![=>])", L[end:]):
        return "deconstruct?"
    return "read"


for r in d:
    r["kind"] = kind(r)
json.dump(d, open(OUTP, "w"))
c = collections.Counter(r["kind"] for r in d)
print(c)
