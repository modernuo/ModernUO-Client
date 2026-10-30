# ECS state census tools

Regenerates the data sections of `docs/modern-client/ecs-state-census.md` from the compiler. See that document, section 1, for why: a grep cannot tell `item.X = 1` from `control.X = 1`, the compiler can.

Scratch-copy workflow (never commit the renamed sources). Run from the repository root; `WORK` is any empty folder outside the repo; `ROOT` is the repository root with forward slashes. Hold `bench.lock` for each build.

1. Pass 1, the A members: `python tools/ecs-census/instrument.py ROOT R`, then
   `dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -nologo -v:q -clp:NoSummary > WORK/build-R.log`, then `git checkout -- src` (the build is expected to fail with a few thousand errors).
2. Pass 2, mutator methods, tile-list members, `Steps`, the `LinkedObject` list API: `python tools/ecs-census/instrument2.py ROOT`, build into `WORK/build-M.log` the same way, `git checkout -- src`.
3. From `WORK`, with the environment variable `CENSUS_ROOT=ROOT`:
   - `python <repo>/tools/ecs-census/analyze.py ROOT build-R.log refs.json` then `classify.py refs.json refs2.json`
   - `python <repo>/tools/ecs-census/analyze.py ROOT build-M.log refsm.json names2.py` then `classify.py refsm.json refsm2.json`
   - `gen.py`, `gen_nonworld.py`, `gen_handlers.py`, `gen_methods.py` (they read the json files in the current folder and write `gen_*.md`)
   - `assemble.py OUT.md` fills `census_template.md` with the generated parts.
4. `audit.py <seed>` (needs `CENSUS_ROOT` and a finished census at `docs/modern-client/ecs-state-census.md`) checks that every textual `.Field op` hit in the client is listed in the census and samples 20 of them.

Notes:
- `names.py` and `names2.py` hold the renamed member sets; `instrument.py` holds the declaration patterns. When a member is added or moved, update both.
- `census_template.md` carries the hand-written prose (classification tables, holders, findings). Its numbers (266 sites, 14 files, ...) are written by hand; recheck them after a regeneration. `assemble.py` fills the counts it can compute.
- The budget test (`tests/ClassicUO.UnitTests/Ecs/WriteSiteBudgetTests.cs`) is the committed guard; `CUO_WRITE_BUDGET_DUMP=<file>` dumps its sites as `file:line:field:receiver:kind` for comparison with `refs2.json`.
- The scripts are Python 3 only, standard library.
