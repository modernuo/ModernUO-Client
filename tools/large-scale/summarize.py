# Summarises FRAMESTATS lines of one or more client logs per plan tag (average of the windows' averages; windows under
# 2 s are skipped). Times in ms unless noted; alloc in bytes per frame.
#   python tools/large-scale/summarize.py <log> [<log> ...] [--keys k1,k2] [--counts c1,c2]
import json, sys
from collections import defaultdict, OrderedDict

DEF_KEYS = ['frame', 'update', 'scene_update', 'world_items', 'world_mobiles', 'world_prepare', 'prep_walk', 'prep_cap_item',
            'prep_cap_mobile', 'prep_text', 'prep_hidden', 'prep_rest', 'lights', 'world_draw', 'scene_draw_ui', 'draw', 'present']
DEF_COUNTS = ['items', 'mobiles', 'objects', 'render_list', 'gpu_dynamic', 'pick_tests', 'cap_items', 'cap_mobiles']

logs, keys, counts = [], DEF_KEYS, DEF_COUNTS
args = iter(sys.argv[1:])
for x in args:
    if x == '--keys':
        keys = next(args).split(',')
    elif x == '--counts':
        counts = next(args).split(',')
    else:
        logs.append(x)

for log in logs:
    rows = OrderedDict()
    for line in open(log, encoding='utf-8', errors='replace'):
        i = line.find('FRAMESTATS {')
        if i < 0:
            continue
        d = json.loads(line[i + 11:])
        if d.get('seconds', 0) < 2:
            continue
        rows.setdefault(d.get('tag') or '-', []).append(d)
    print('##', log)
    hdr = ['tag', 'n', 'fps', 'alloc/f', 'gc0'] + keys + counts
    print('\t'.join(hdr))
    for tag, ws in rows.items():
        avg = lambda f: sum(f(w) for w in ws) / len(ws)
        cells = [tag, str(len(ws)), '%.0f' % avg(lambda w: w['fps']), '%.0f' % avg(lambda w: w['alloc_per_frame']),
                 '%.1f' % avg(lambda w: w['gc0'])]
        cells += ['%.3f' % avg(lambda w, k=k: w['phases'].get(k, [0])[0]) for k in keys]
        cells += ['%.0f' % avg(lambda w, c=c: w['counts'].get(c, [0])[0]) for c in counts]
        print('\t'.join(cells))
