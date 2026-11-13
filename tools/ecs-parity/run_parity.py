# W3 parity runs (docs/modern-client/testing/ecs-animation.md).
# Logs in with a throwaway settings.json (password from CUO_TEST_PASSWORD, written encrypted, never on a command line),
# runs an -ecsdriver scenario with -ecscheck (and CUO_ECS_PARITY, read only by builds before gpu_ecs_sprites was deleted),
# closes the client, copies the log to --out and deletes the temp folder.
#
#   set CUO_TEST_PASSWORD=...   (PowerShell: $env:CUO_TEST_PASSWORD = '...')
#   python tools/ecs-parity/run_parity.py --exe bin/Release/net10.0/cuo.exe --script tools/ecs-parity/bank.txt --out <scratch>/bank.log
import argparse, ctypes, ctypes.wintypes as wt, json, os, shutil, subprocess, sys, tempfile

p = argparse.ArgumentParser()
p.add_argument('--exe', required=True)
p.add_argument('--script', required=True, help='-ecsdriver scenario file')
p.add_argument('--out', required=True, help='where the client log is copied')
p.add_argument('--user', default='kbatman3')
p.add_argument('--parity', default='1', help='CUO_ECS_PARITY (builds before gpu_ecs_sprites was deleted): compare every Nth Gpu frame')
p.add_argument('--ecscheck', default='120', help='-ecscheck interval in frames')
p.add_argument('--timeout', type=float, default=240)
p.add_argument('--pack', default=r'C:\Repositories\uo-packs\classic-1.0')
p.add_argument('--uo', default=r'C:\Ultima Online Classic')
p.add_argument('--env', action='append', default=[], help='extra KEY=VALUE for the client')
a = p.parse_args()

user32 = ctypes.WinDLL('user32', use_last_error=True)


# ClassicUO's Crypter.Encrypt: "1-" + hex of the password XOR the machine name
def enc_password(pw):
    key = os.environ.get('COMPUTERNAME', '')
    return '1-' + ''.join('%02X' % (ord(c) ^ ord(key[i % len(key)])) for i, c in enumerate(pw))


pw = os.environ.get('CUO_TEST_PASSWORD')
if not pw:
    sys.exit('CUO_TEST_PASSWORD not set')

exe = os.path.abspath(a.exe)
script = os.path.abspath(a.script)
work = tempfile.mkdtemp(prefix='cuo-parity-')
os.makedirs(os.path.join(work, 'profiles'))
settings = dict(username=a.user, password=enc_password(pw), ip='127.0.0.1', port=2593, ultimaonlinedirectory=a.uo,
                clientversion='7.0.103.0', lastservernum=1, fps=0, vsync=False, settings_version=1, present_at_display_rate=False, is_win_maximized=False, saveaccount=False, autologin=True,
                login_music=False, plugins=[], pack=a.pack, world_renderer='gpu')
spath = os.path.join(work, 'settings.json')
json.dump(settings, open(spath, 'w'))
log = os.path.join(work, 'client.log')
env = {k: v for k, v in os.environ.items() if not k.upper().startswith('CUO_')}
env['DOTNET_GCHeapHardLimit'] = '0x200000000'
env['CUO_ECS_PARITY'] = a.parity
for kv in a.env:
    k, v = kv.split('=', 1)
    env[k] = v
args = [exe, '-settings', spath, '-profilespath', os.path.join(work, 'profiles'), '-skiploginscreen', '-autologin', 'true',
        '-worldrenderer', 'gpu', '-fps', '0', '-vsync', 'false', '-nomessagebox', '-ecscheck', a.ecscheck, '-ecsdriver', script]
proc = subprocess.Popen(args, cwd=os.path.dirname(exe), env=env, stdout=open(log, 'w'), stderr=subprocess.STDOUT)
print('pid', proc.pid, flush=True)
timed_out = False
try:
    proc.wait(a.timeout)
except subprocess.TimeoutExpired:
    timed_out = True
    print('timeout; closing', flush=True)
finally:
    if proc.poll() is None:
        def close(hwnd, _):
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value == proc.pid:
                user32.PostMessageW(hwnd, 0x0010, 0, 0)
            return True
        user32.EnumWindows(ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)(close), 0)
        try:
            proc.wait(15)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(10)
    print('exit', proc.returncode, flush=True)
    shutil.copy(log, a.out)
    shutil.rmtree(work, ignore_errors=True)
    print('temp removed:', not os.path.exists(work), flush=True)

# T1 part 9 (bench-000095 exited 0 with every client crashed): a client that exits abnormally (an unhandled exception's
# 0xE0434352, a kill) or does not end its script before the timeout fails the run
if timed_out:
    sys.exit(2)
if proc.returncode != 0:
    sys.exit(1)
