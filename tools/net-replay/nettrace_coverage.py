#!/usr/bin/env python3
"""Packet-id coverage of net harness traces (nettrace v1, CUO_NET_TRACE_OUT).

Usage: nettrace_coverage.py <PacketHandlers.cs> <name>=<trace> [<name>=<trace> ...]

Prints, per registered handler id: its handler name and the packet count in each trace, then the handlers no trace
covers. Ids seen in a trace that have no handler are listed too (they are skipped by the dispatcher).
"""
import re
import sys
from collections import Counter


def handlers(path):
    out = {}
    with open(path, encoding='utf-8') as f:
        for m in re.finditer(r'Handler\.Add\(0x([0-9A-Fa-f]{2}),\s*(\w+)\)', f.read()):
            out[int(m.group(1), 16)] = m.group(2)
    return out


def trace_ids(path):
    c = Counter()
    with open(path, encoding='utf-8') as f:
        for line in f:
            if line.startswith('P '):
                c[int(line.split()[2], 16)] += 1
    return c


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    hs = handlers(sys.argv[1])
    names, counts = [], []
    for arg in sys.argv[2:]:
        n, p = arg.split('=', 1)
        names.append(n)
        counts.append(trace_ids(p))
    total = Counter()
    for c in counts:
        total.update(c)
    w = max(6, *(len(n) for n in names))
    print('id   handler                       ' + ' '.join(n.rjust(w) for n in names))
    for i in sorted(hs):
        print(f'{i:02X}   {hs[i]:<29} ' + ' '.join(str(c.get(i, 0)).rjust(w) for c in counts))
    covered = [i for i in hs if total[i] > 0]
    missing = sorted(i for i in hs if total[i] == 0)
    print(f'\ncovered {len(covered)}/{len(hs)} handler ids; packets {sum(total.values())}')
    print('not covered: ' + ', '.join(f'{i:02X} {hs[i]}' for i in missing))
    extra = sorted(i for i in total if i not in hs)
    if extra:
        print('ids with no handler: ' + ', '.join(f'{i:02X} ({total[i]})' for i in extra))
    return 0


if __name__ == '__main__':
    sys.exit(main())
