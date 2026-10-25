# The large-scale test scene (docs/modern-client/testing/large-scale.md): about 2,000 frozen NPCs and 20,000 ground
# items on an empty, flat, static-free grass field in Felucca's (map 0) unused east strip, centred on 5156 2150: no
# region, no statics, land z 0 for 36+ tiles in every direction, water beyond about 50 tiles. Nobody goes there.
#
#   python tools/large-scale/make_scene.py create > <scratch>/ls_create.txt   (-ecsdriver script; Owner/GM account)
#   python tools/large-scale/make_scene.py wipe   > <scratch>/ls_wipe.txt
#   (an optional "x y" after create/wipe moves the centre)
#
# Both scripts [go to the centre first. create uses [TileXYZ (absolute coordinates, no targeting), so it is
# deterministic; wipe uses [wipe with a targeted box of +-20 tiles around the centre (the field is empty for 32+ tiles
# around it).
#
# A one-tile lane along row CY (5138..5173, 2150) is left free of mobiles and items, so a character can walk through
# the scene (DevBench plan entry lswalk walks it end to end).
# Mobiles: 32x32 tiles (5140..5171, 2134..2165) minus the lane, two per tile = 1,984. Frozen + Blessed (+ CantWalk for
# creatures), so they never move, fight or path. Their idle (stand) animations still loop on the client.
# Items: 36x36 tiles (5138..5173, 2132..2167) minus the lane: 15 full layers (gold and arrow stacks, bandages, art) =
# 18,900, plus four 12x12 blocks of lit/animated art (brazier, candle, animated art 0x3996, fire 0x19AB) = 576.
# Total 19,476. All items are non-movable, so they never decay. Everything is within 18 tiles of the centre, so a
# client standing there holds all of it.
import sys

CX, CY = 5156, 2150
if len(sys.argv) > 3:
    CX, CY = int(sys.argv[2]), int(sys.argv[3])
MX, MY, MS = CX - 16, CY - 16, 32
IX, IY, IS = CX - 18, CY - 18, 36

FROZEN = 'set Frozen true Blessed true'
CREATURE = FROZEN + ' CantWalk true'

# (type, extra set) per 4-row strip; two passes over the same 32x32 block
PASSES = [
    ['Brigand', 'SavageRider', 'Horse', 'Orc', 'EvilMage', 'Cow', 'Mongbat', 'Skeleton'],
    ['TownCrier', 'Ninja', 'Brigand', 'Gargoyle', 'Llama', 'Zombie', 'SavageRider', 'Dragon'],
]
NO_AI = {'TownCrier'}  # plain Mobile, no CantWalk property

LAYERS = [
    ('Gold 1000', 0), ('Arrow 50', 0), ('Bandage 20', 1), ('Static 0xE75', 1), ('Static 0x1F1B', 2),
    ('Static 0x13B9', 2), ('Static 0xF0E', 3), ('Static 0x9D0', 3), ('Static 0x1BDD', 4), ('Static 0xE7C', 4),
    ('Static 0x1443', 5), ('Static 0xEFA', 5), ('Static 0xF45', 6), ('Static 0x1F03', 6), ('Gold 1', 7),
]
# lit / animated art: 12x12 blocks in the four quadrants of the item area, on top of the layers
BLOCKS = [('Static 0xE31', IX, IY), ('Static 0xA0F', IX + 24, IY), ('Static 0x3996', IX, IY + 24), ('Static 0x19AB', IX + 24, IY + 24)]


def create():
    out = ['wait 6', f'say [go {CX} {CY}', 'wait 4', 'check']
    for kinds in PASSES:
        for i, kind in enumerate(kinds):
            props = FROZEN if kind in NO_AI else CREATURE
            y0, rows = MY + i * 4, 4
            if y0 <= CY < y0 + rows:  # the walking lane (row CY) stays free
                rows -= 1
                if y0 == CY:
                    y0 += 1
                else:
                    assert y0 + rows == CY or CY == y0 + 0, 'lane must be the first or last row of a strip'
            out += [f'say [TileXYZ {MX} {y0} {MS} {rows} 0 {kind} {props}', 'wait 1.5']
    out += ['wait 3', 'check']
    for kind, z in LAYERS:
        props = '' if kind.startswith('Static') else ' set Movable false'
        # two rectangles around the walking lane (row CY)
        out += [f'say [TileXYZ {IX} {IY} {IS} {CY - IY} {z} {kind}{props}', 'wait 1.5',
                f'say [TileXYZ {IX} {CY + 1} {IS} {IY + IS - CY - 1} {z} {kind}{props}', 'wait 1.5']
    for kind, x, y in BLOCKS:
        out += [f'say [TileXYZ {x} {y} 12 12 8 {kind}', 'wait 1']
    out += ['wait 8', 'check', 'quit']
    return out


def wipe():
    return ['wait 6', f'say [go {CX} {CY}', 'wait 6', 'check',
            'say [wipe', 'wait 1', 'target ground -20 -20', 'wait 1', 'target ground 20 20', 'wait 10',
            'check', 'quit']


if __name__ == '__main__':
    mode = sys.argv[1] if len(sys.argv) > 1 else ''
    if mode not in ('create', 'wipe'):
        sys.exit('usage: make_scene.py create|wipe')
    print('\n'.join(create() if mode == 'create' else wipe()))
