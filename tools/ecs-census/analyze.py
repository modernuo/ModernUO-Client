"""Parse the compiler errors of the renamed build; classify each reference as read or write.
Usage: analyze.py <repo-root> <build-log> <out.json>"""
import os as _os
SCRIPTS = _os.path.dirname(_os.path.abspath(__file__))
CENSUS_ROOT = _os.environ.get("CENSUS_ROOT", "").replace("\\", "/").rstrip("/") + "/"
import re, sys, json, collections

root, log, out = sys.argv[1], sys.argv[2], sys.argv[3]
root = root.replace("\\", "/").rstrip("/")
NAMES = None
exec(open(_os.path.join(SCRIPTS, sys.argv[4] if len(sys.argv) > 4 else "names.py")).read())

ERR = re.compile(r"^(?P<file>[A-Za-z]:[^()]+)\((?P<line>\d+),(?P<col>\d+)\): error (?P<code>CS\d+): (?P<msg>.*?) \[[^\]]*\.csproj\]$")
src_cache = {}


def lines(path):
    if path not in src_cache:
        src_cache[path] = open(path, encoding="utf-8-sig").read().split("\n")
    return src_cache[path]


seen = set()
refs = []
unexplained = []
for raw in open(log, encoding="utf-8", errors="replace"):
    m = ERR.match(raw.strip())
    if not m:
        continue
    f = m["file"].replace("\\", "/")
    ln, col = int(m["line"]), int(m["col"])
    key = (f, ln, col)
    if key in seen:
        continue
    seen.add(key)
    L = lines(f)[ln - 1]
    tm = re.match(r"\??\.?(\w+)", L[col - 1:])
    if tm and tm.group(0)[0] in "?.":
        col += len(tm.group(0)) - len(tm.group(1))
    tok = tm.group(1) if tm else ""
    code = m["code"]
    msg = m["msg"]
    if tok not in NAMES:
        unexplained.append((f[len(root) + 1:], ln, col, code, msg[:100]))
        continue
    rt = re.match(r"'(\w+)' does not contain a definition", msg)
    refs.append(dict(file=f[len(root) + 1:], line=ln, col=col, code=code, member=tok, rtype=rt.group(1) if rt else None, text=L.strip()))

json.dump(dict(refs=refs, unexplained=unexplained), open(out, "w"))
print(len(refs), "member references;", len(unexplained), "other errors")
for u in unexplained[:40]:
    print("  ?", u)
