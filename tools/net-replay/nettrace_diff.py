#!/usr/bin/env python3
"""Compare two net-rework parity traces (NetTrace: CUO_NET_TRACE_OUT, or the headless replay) and print the first divergence.

  python -I nettrace_diff.py A.trace B.trace [--framing] [--context N]

Lines starting with '#' (the header naming the run) are ignored. --framing compares only what the receive path decides (packet
id, length and bytes hash of every dispatched packet): use it for a headless trace against a client trace, or for a live trace
against a replay. Exit 0 when the traces match, 1 when they differ, 2 on a usage error.
"""
import argparse
import sys


def load(path, framing):
    out = []
    with open(path, encoding='utf-8') as f:
        for n, line in enumerate(f, 1):
            line = line.rstrip('\r\n')
            if not line or line.startswith('#'):
                continue
            if framing:
                fields = line.split()
                if fields[0] != 'P':
                    continue
                line = ' '.join(fields[:5])
            out.append((n, line))
    return out


def describe(lines):
    packets = sum(1 for _, l in lines if l.startswith('P '))
    checkpoints = sum(1 for _, l in lines if l.startswith('C '))
    return f'{packets} packets, {checkpoints} checkpoints'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('a')
    ap.add_argument('b')
    ap.add_argument('--framing', action='store_true', help='compare the packet id/length/bytes columns only')
    ap.add_argument('--context', type=int, default=6)
    args = ap.parse_args()
    a, b = load(args.a, args.framing), load(args.b, args.framing)
    print(f'A {args.a}: {describe(a)}')
    print(f'B {args.b}: {describe(b)}')
    for i in range(min(len(a), len(b))):
        if a[i][1] != b[i][1]:
            break
    else:
        if len(a) == len(b):
            print('SAME' + (' (framing)' if args.framing else ''))
            return 0
        i = min(len(a), len(b))
    lo = max(0, i - args.context)
    print(f'DIFFER at entry {i}' + (f' (A line {a[i][0]})' if i < len(a) else ' (A ended)') +
          (f' (B line {b[i][0]})' if i < len(b) else ' (B ended)'))
    for name, lines in (('A', a), ('B', b)):
        print(f'--- {name}')
        for j in range(lo, min(len(lines), i + args.context + 1)):
            print(('>> ' if j == i else '   ') + lines[j][1])
    return 1


if __name__ == '__main__':
    sys.exit(main())
