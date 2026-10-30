"""Self-audit: every textual `.Field op` hit (and bare world write) must be listed in the census (section 3.1 or section 7)."""
import os as _os
SCRIPTS = _os.path.dirname(_os.path.abspath(__file__))
CENSUS_ROOT = _os.environ.get("CENSUS_ROOT", "").replace("\\", "/").rstrip("/") + "/"
import re, os, random, sys
ROOT = CENSUS_ROOT
doc = open(ROOT + "docs/modern-client/ecs-state-census.md", encoding="utf-8").read()
exec(open(_os.path.join(SCRIPTS, "names.py")).read())
NAMES = NAMES - {"Title"}
# parse listed (file, line, field)
listed = set()
sec31 = doc[doc.index("### 3.1 Write sites"):doc.index("### 3.2")]
cur = None
field = None
for line in sec31.split("\n"):
    m = re.match(r"\*\*`(\w+)`\*\*", line)
    if m:
        field = m.group(1)
        continue
    m = re.match(r"- (?:map/effect: )?`([^`]+)`: (.*)", line)
    if m and field:
        for n in re.findall(r"(\d+) \(", m.group(2)):
            listed.add((m.group(1), int(n), field))
sec7 = doc[doc.index("## 7."):doc.index("## 8.")]
for line in sec7.split("\n"):
    m = re.match(r"- `([^`]+)`: (.*)", line)
    if m:
        for fm in re.finditer(r"`\.(\w+)` ([\d, ]+)", m.group(2)):
            for n in re.findall(r"\d+", fm.group(2)):
                listed.add((m.group(1), int(n), fm.group(1)))
OP = r"(?:=(?![=>])|\+=|-=|\*=|/=|%=|&=|\|=|\^=|<<=|>>=|\?\?=|\+\+|--)"
rx = re.compile(r"\.\s*(" + "|".join(sorted(NAMES)) + r")\b\s*" + OP)
hits = []
for dp, _, fs in os.walk(ROOT + "src/ClassicUO.Client"):
    dpn = dp.replace("\\", "/")
    if "/obj" in dpn or "/bin" in dpn:
        continue
    for f in fs:
        if not f.endswith(".cs"):
            continue
        p = os.path.join(dp, f).replace("\\", "/")[len(ROOT + "src/ClassicUO.Client/"):]
        for i, l in enumerate(open(ROOT + "src/ClassicUO.Client/" + p, encoding="utf-8-sig", errors="replace").read().split("\n"), 1):
            for m in rx.finditer(l.split("//")[0]):
                hits.append((p, i, m.group(1)))
missing = [h for h in hits if h not in listed]
print("textual hits", len(hits), "listed", len(listed), "missing", len(missing))
for m in missing[:30]:
    print("  MISSING", m)
random.seed(int(sys.argv[1]) if len(sys.argv) > 1 else 1)
for h in random.sample(hits, 20):
    print("sample", h, "OK" if h in listed else "MISSING")
