"""Textual `.Field op` hits (and bare ones in GameObject classes) that the compiler did not report as writes to a world-object member."""
import os as _os
SCRIPTS = _os.path.dirname(_os.path.abspath(__file__))
CENSUS_ROOT = _os.environ.get("CENSUS_ROOT", "").replace("\\", "/").rstrip("/") + "/"
import json, re, os, collections
ROOT = CENSUS_ROOT
PFX = "src/ClassicUO.Client/"
exec(open(_os.path.join(SCRIPTS, "names.py")).read())
NAMES = NAMES - {"Title"}
d = json.load(open("refs2.json"))
W = {(r["file"], r["line"], r["member"]) for r in d if r["kind"] in ("assign", "compound", "struct-member", "incdec", "ref/out")}
OP = r"(?:=(?![=>])|\+=|-=|\*=|/=|%=|&=|\|=|\^=|<<=|>>=|\?\?=|\+\+|--)"
rx = re.compile(r"(?P<recv>[\w\.\[\]\(\)\?!]*?)\s*\.\s*(?P<f>" + "|".join(sorted(NAMES)) + r")\b\s*" + OP)
res = collections.defaultdict(list)
total = 0
for dp, _, fs in os.walk(ROOT + PFX):
    dpn = dp.replace("\\", "/")
    if "/obj" in dpn or "/bin" in dpn:
        continue
    for f in sorted(fs):
        if not f.endswith(".cs"):
            continue
        p = os.path.join(dp, f).replace("\\", "/")[len(ROOT):]
        txt = open(ROOT + p, encoding="utf-8-sig", errors="replace").read().split("\n")
        for i, l in enumerate(txt, 1):
            code = l.split("//")[0]
            for m in rx.finditer(code):
                if (p, i, m["f"]) in W:
                    continue
                res[p[len(PFX):]].append((i, m["f"], m["recv"][-40:]))
                total += 1
lines = []
for fl in sorted(res):
    byf = collections.OrderedDict()
    for ln, fld, recv in res[fl]:
        byf.setdefault(fld, []).append(f"{ln}")
    lines.append(f"- `{fl}`: " + "; ".join(f"`.{fld}` {', '.join(v)}" for fld, v in byf.items()))
open("gen_nonworld.md", "w", encoding="utf-8").write("\n".join(lines))
print(total, "textual hits not world-object writes in", len(res), "files")

open("gen_nonworld_count.txt", "w").write(str(total))
