# The large-scale test scene (docs/modern-client/testing/large-scale.md): 1,984 frozen NPCs and 19,476 ground items on an
# empty, flat, static-free grass field in Felucca's (map 0) unused east strip, centred on 5156 2150.
#
# The scene is built by the server: ModernUO's [TestScene command (branch modernuo-client/test-scene,
# Projects/UOContent/Commands/ClientPoc/TestScene.cs; GameMaster or above):
#   [TestScene create   builds it in one call (under a second), refuses if it exists or the area is not empty
#   [TestScene wipe     deletes exactly what create built (tracked by serial, persisted with the world save)
#   [TestScene go       moves you to Felucca 5156 2150 from any facet, remembering where you were
#   [TestScene return   takes you back there
#   [TestScene status   counts the tracked objects and everything in the area
#
# This script only writes -ecsdriver scripts that issue those commands from a client, for unattended runs:
#   python tools/large-scale/make_scene.py create > <scratch>/ls_create.txt
#   python tools/large-scale/make_scene.py wipe   > <scratch>/ls_wipe.txt
#   python tools/large-scale/make_scene.py status > <scratch>/ls_status.txt
# Each one goes to the scene (so the driver's check lines count it) and returns at the end.
import sys


def script(command):
    return ['wait 6', 'say [TestScene go', 'wait 4', 'check', f'say [TestScene {command}', 'wait 10', 'check',
            'say [TestScene status', 'wait 2', 'say [TestScene return', 'wait 4', 'quit']


if __name__ == '__main__':
    mode = sys.argv[1] if len(sys.argv) > 1 else ''
    if mode not in ('create', 'wipe', 'status'):
        sys.exit('usage: make_scene.py create|wipe|status')
    print('\n'.join(script(mode)))
