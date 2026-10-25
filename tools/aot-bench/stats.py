# stats.py <file.jsonl> [file...] : per variant and spot, mean over runs of the per-run mean of window avgs (stdev across runs)
import json, os, statistics as st, sys
from collections import defaultdict
IDX = int(os.environ.get('STAT_IDX', '0'))  # 0 avg, 1 p50, 2 p95
AGG = os.environ.get('STAT_AGG', 'mean')     # mean | median | min over runs
keys = ['frame', 'update', 'world_prepare', 'world_draw', 'ui_draw', 'composite', 'draw', 'present']
runs = defaultdict(lambda: defaultdict(list))  # (variant, spot) -> run -> [window dicts]
for f in sys.argv[1:]:
    for line in open(f):
        d = json.loads(line)
        variant = d['run'].split('#')[0]
        runs[(variant, d['tag'])][d['run']].append(d)
print(f"{'variant':16} {'spot':8} {'n':>2} {'fps':>6} {'f.p50':>6} " + ' '.join(f'{k:>13}' for k in keys))
for (variant, spot) in sorted(runs, key=lambda k: (k[1], k[0])):
    per = runs[(variant, spot)]
    vals = defaultdict(list)
    for run, ws in per.items():
        vals['fps'].append(st.mean(w['fps'] for w in ws))
        vals['p50'].append(st.mean(w['phases']['frame'][1] for w in ws))
        for k in keys:
            if k in ws[0]['phases']:
                vals[k].append(st.median(w['phases'][k][IDX] for w in ws))
    agg = {'mean': st.mean, 'median': st.median, 'min': min}[AGG]
    def fmt(k):
        v = vals[k]
        if not v: return f"{'-':>13}"
        sd = st.stdev(v) if len(v) > 1 else 0
        return f'{agg(v):7.4f}+-{sd:.3f}'
    print(f"{variant:16} {spot:8} {len(per):>2} {st.mean(vals['fps']):6.0f} {st.mean(vals['p50']):6.3f} " + ' '.join(fmt(k) for k in keys))
