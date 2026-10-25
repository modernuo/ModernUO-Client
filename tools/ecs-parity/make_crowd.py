# Writes an -ecsdriver scenario that spawns N mixed NPCs (humans with equipment, mounted savages, animals, monsters) on a
# grid around Green Acres (5445 1153, empty), walks and runs among them, then wipes them.
#   python tools/ecs-parity/make_crowd.py 120 > <scratch>/crowd120.txt
import sys

n_max = int(sys.argv[1]) if len(sys.argv) > 1 else 120
kinds = ['Brigand', 'Brigand', 'SavageRider', 'Orc', 'Horse', 'EvilMage', 'Ninja', 'Gargoyle']
lines = ['wait 6', 'say [go 5445 1153', 'wait 3']
n = 0
for dy in range(-7, 8, 2):
    for dx in range(-9, 10):
        if abs(dx) < 2 and abs(dy) < 2:
            continue
        if n >= n_max:
            break
        lines += ['say [add ' + kinds[n % len(kinds)], 'wait 0.4', 'target ground %d %d' % (dx, dy), 'wait 0.3']
        n += 1
lines += ['wait 5', 'parity', 'wait 10', 'check', 'parity',
          'walk 2 3', 'walk 6 3', 'walk 2 3 run', 'walk 6 3 run', 'wait 8', 'check', 'parity',
          'say [wipe mobiles', 'wait 1', 'target ground -18 -18', 'wait 1', 'target ground 18 18', 'wait 3',
          'say [wipe items', 'wait 1', 'target ground -18 -18', 'wait 1', 'target ground 18 18', 'wait 3',
          'check', 'quit']
print('\n'.join(lines))
