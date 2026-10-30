import json, os, re, sys
here = os.path.dirname(os.path.abspath(__file__))
t = open(os.path.join(here, "census_template.md"), encoding="utf-8").read()
d = [r for r in json.load(open("refs2.json")) if r["member"] != "Title"]
W = ("assign", "compound", "struct-member")
reads = sum(1 for r in d if r["kind"] == "read")
PF = "src/ClassicUO.Client/"
world_writes = 266
rep = {
    "SUMMARY": open("gen_summary.md", encoding="utf-8").read(),
    "WRITES": open("gen_writes.md", encoding="utf-8").read(),
    "READERS": open("gen_readers.md", encoding="utf-8").read(),
    "HANDLERS": open("gen_handlers.md", encoding="utf-8").read(),
    "NONWORLD": open("gen_nonworld.md", encoding="utf-8").read(),
    "METHODS": open("gen_methods.md", encoding="utf-8").read() if os.path.exists("gen_methods.md") else "(pending)",
    "READ_TOTAL": f"{reads:,}",
    "RW_RATIO": str(round(reads / world_writes)),
    "NONWORLD_COUNT": open("gen_nonworld_count.txt").read(),
}
for k, v in rep.items():
    t = t.replace("{{" + k + "}}", v)
left = re.findall(r"\{\{\w+\}\}", t)
print("unreplaced:", left)
out = sys.argv[1] if len(sys.argv) > 1 else "ecs-state-census.md"
open(out, "w", encoding="utf-8", newline="\n").write(t)
print(len(t), "chars", t.count("\n"), "lines")
