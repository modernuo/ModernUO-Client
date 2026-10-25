# GPU page-fault repro loop for the ClassicUO PoC client (see docs/modern-client/testing/gpu-faults.md).
#
# Each run: log in (Gpu world renderer) -> run a DevBench plan (default: vsync off, then world:legacy/gpu flips) -> exit.
# A run counts as a GPU fault of OUR client when it exits with 0xDEAD (GpuDeviceLost), logs "GPU device lost", or a new
# Aftermath dump carries its PID (cuo-<pid>-...). A driver event (nvlddmkm 153) or dump from another process during the
# run is recorded as "foreign": other agents' clients share the GPU and fault too, and a reset can make other clients on
# the GPU lose their device as well, so check dump PIDs before blaming a build. New files in C:\CrashDumps\aftermath are recorded per run.
#
# The password comes from CUO_TEST_PASSWORD and is written encrypted (ClassicUO.Utility.Crypter: "1-" + hex of the
# ASCII bytes XORed with the machine name) into a throwaway settings.json; it is never on a command line.
#
#   python -I repro.py --exe <bin>\cuo.exe --tag base --out <scratch>\repro --runs 60 --max-faults 3 [--env NAME=VALUE ...] [--world legacy]
#   python -I repro.py --summarize <scratch>\repro\base.jsonl
import argparse, ctypes, ctypes.wintypes as wt, datetime, json, os, shutil, subprocess, sys, tempfile, time

p = argparse.ArgumentParser()
p.add_argument('--exe')
p.add_argument('--tag')
p.add_argument('--out', help='results folder (keep it in your scratchpad)')
p.add_argument('--runs', type=int, default=60)
p.add_argument('--max-faults', type=int, default=3, help='stop after this many faults (each one resets the GPU and blanks the screen)')
p.add_argument('--account', default='kbatman2')
p.add_argument('--plan', default='vsync:off;world:legacy;world:gpu;world:legacy;world:gpu;world:legacy;world:gpu')
p.add_argument('--size', default='1920x1080')
p.add_argument('--world', default='gpu', help='world renderer at login (gpu|legacy)')
p.add_argument('--timeout', type=float, default=120)
p.add_argument('--env', action='append', default=[], help='extra NAME=VALUE for the client (repeatable); {run} in VALUE becomes the run number')
p.add_argument('--keep-logs', action='store_true', help='keep every client log, not just failed runs')
p.add_argument('--summarize', nargs='+', metavar='JSONL', help='print one line per run from earlier results and exit')
a = p.parse_args()

if a.summarize:
    for path in a.summarize:
        runs = faults = 0
        for line in open(path, encoding='utf-8'):
            r = json.loads(line)
            runs += 1
            faults += r['fault']
            watch = {k: v for k, v in r.get('watch', {}).items() if 'atlas' in k or 'filler' in k or 'NEWTEX' in k}
            print(r['run'], r['start'], r['secs'], r['exit'], 'FAULT' if r['fault'] else 'foreign' if r.get('foreign') else '', r['events'],
                  [os.path.basename(d) for d in r['dumps']], watch)
        print(f'{path}: {runs} runs, {faults} faults')
    sys.exit(0)

if not (a.exe and a.tag and a.out):
    p.error('--exe, --tag and --out are required')

pw = os.environ.get('CUO_TEST_PASSWORD')
if not pw:
    sys.exit('CUO_TEST_PASSWORD not set')

user32 = ctypes.WinDLL('user32', use_last_error=True)
os.makedirs(a.out, exist_ok=True)
summary = open(os.path.join(a.out, f'{a.tag}.jsonl'), 'a', encoding='utf-8')
textlog = open(os.path.join(a.out, f'{a.tag}.log'), 'a', encoding='utf-8')


def say(s):
    line = f'[{datetime.datetime.now().strftime("%H:%M:%S")}] {s}'
    print(line, flush=True)
    textlog.write(line + '\n')
    textlog.flush()


def encrypt_password(plain):
    key = os.environ['COMPUTERNAME']  # Environment.MachineName
    return '1-' + ''.join('%02X' % (ord(c) ^ ord(key[i % len(key)])) for i, c in enumerate(plain))


def gpu_events(since):
    cmd = ("Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='nvlddmkm','Display'; StartTime=[datetime]'" + since.isoformat() +
           "'} -ErrorAction SilentlyContinue | % { $_.TimeCreated.ToString('HH:mm:ss.fff') + ' ' + $_.ProviderName + ' ' + $_.Id }")
    return subprocess.run(['powershell', '-NoProfile', '-Command', cmd], capture_output=True, text=True).stdout.strip()


DUMP_DIRS = [r'C:\CrashDumps\aftermath', r'C:\CrashDumps']


def dump_files():
    found = set()
    for d in DUMP_DIRS:
        if os.path.isdir(d):
            found.update(os.path.join(d, f) for f in os.listdir(d) if os.path.isfile(os.path.join(d, f)))
    return found


def close(proc):
    def cb(hwnd, _):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == proc.pid:
            user32.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE
        return True
    user32.EnumWindows(ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)(cb), 0)
    try:
        proc.wait(15)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(10)


bindir = os.path.dirname(os.path.abspath(a.exe))
data_existed = os.path.isdir(os.path.join(bindir, 'Data'))
try:
    build = open(os.path.join(bindir, 'build-info.txt')).read().strip()
except OSError:
    build = '?'
faults = 0
run = 0
say(f'== {a.tag}: exe={a.exe} ({build}) runs={a.runs} plan={a.plan} env={a.env}')

for run in range(1, a.runs + 1):
    work = tempfile.mkdtemp(prefix=f'cuo-repro-{a.tag}-')
    os.makedirs(os.path.join(work, 'profiles'))
    settings = dict(username=a.account, password=encrypt_password(pw), ip='127.0.0.1', port=2593,
                    ultimaonlinedirectory=r'C:\Ultima Online Classic', clientversion='7.0.103.0', lastservernum=1, fps=0, vsync=False,
                    is_win_maximized=False, saveaccount=False, autologin=True, login_music=False, plugins=[],
                    pack=r'C:\Repositories\uo-packs\classic-1.0', world_renderer=a.world)
    settings_path = os.path.join(work, 'settings.json')
    with open(settings_path, 'w') as f:
        json.dump(settings, f)
    log = os.path.join(work, 'client.log')
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith('CUO_')}
    env.update(DOTNET_GCHeapHardLimit='0x200000000', CUO_FRAMESTATS='1', CUO_BENCH_EXIT='1', CUO_BENCH_SIZE=a.size,
               CUO_BENCH_WINDOWS='1', CUO_BENCH_PLAN=a.plan)
    for kv in a.env:
        k, v = kv.split('=', 1)
        env[k] = v.replace('{run}', str(run))  # e.g. CUO_BENCH_SHOTS=<dir>\{run}: one shot folder per run
    args = [a.exe, '-settings', settings_path, '-profilespath', os.path.join(work, 'profiles'), '-skiploginscreen', '-autologin', 'true',
            '-worldrenderer', a.world, '-fps', '0', '-vsync', 'false', '-nomessagebox']
    before = dump_files()
    t0 = datetime.datetime.now() - datetime.timedelta(seconds=1)
    with open(log, 'w') as out:
        proc = subprocess.Popen(args, cwd=bindir, env=env, stdout=out, stderr=subprocess.STDOUT)
        timed_out = False
        try:
            proc.wait(a.timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            close(proc)
    code = proc.returncode & 0xFFFFFFFF
    secs = (datetime.datetime.now() - t0).total_seconds()
    try:
        lines = open(log, encoding='utf-8', errors='replace').read().splitlines()
    except OSError:
        lines = []
    ingame = any('Player [0x' in l and 'created' in l for l in lines)
    done = any('[bench] plan done' in l for l in lines)
    lost = [l for l in lines if 'GPU device lost' in l]
    events = gpu_events(t0)
    for _ in range(10):  # the client exits within a frame of the reset, often before the driver's event is logged
        if events or code != 0xDEAD:
            break
        time.sleep(0.5)
        events = gpu_events(t0)
    new_dumps = sorted(dump_files() - before)

    watch = [l[l.index('[gpuwatch]'):] for l in lines if '[gpuwatch]' in l]
    if watch:
        with open(os.path.join(a.out, f'{a.tag}-watch.log'), 'a', encoding='utf-8') as wf:
            wf.write(f'--- run {run}\n' + '\n'.join(watch) + '\n')
    counts = {}
    for l in watch:
        what = l.split(':')[0].split(' ', 2)[-1]
        kind = next((k for k, s in (('NEWTEX', 'NEWTEX'), ('REINIT', 'reinit'), ('NEW', 'NEW CONTAINER'), ('CYCLE', 'CYCLE'),
                                    ('first', 'first')) if s in l), 'other')
        counts[f'{what}:{kind}'] = counts.get(f'{what}:{kind}', 0) + 1

    own_dumps = [d for d in new_dumps if os.path.basename(d).startswith(f'cuo-{proc.pid}-')]
    fault = code == 0xDEAD or bool(lost) or bool(own_dumps)
    foreign = not fault and (bool(events) or bool(new_dumps))
    rec = dict(tag=a.tag, run=run, pid=proc.pid, exe=a.exe, build=build, env=a.env, start=t0.strftime('%H:%M:%S'), secs=round(secs, 1),
               exit=f'0x{code:X}', ingame=ingame, plan_done=done, timed_out=timed_out, fault=fault, foreign=foreign, watch=counts,
               sdl=[l for l in lines if 'SDL diagnostic build' in l][:1], lost=lost[:2], events=events, dumps=new_dumps)
    summary.write(json.dumps(rec) + '\n')
    summary.flush()
    say(f'run {run} (pid {proc.pid}): exit 0x{code:X} {secs:.0f}s ingame={ingame} done={done} timeout={timed_out} fault={fault} '
        f'foreign={foreign} events={events!r} dumps={new_dumps}')

    if fault or not done or a.keep_logs:
        shutil.copy(log, os.path.join(a.out, f'{a.tag}-run{run}-client.log'))
    shutil.rmtree(work, ignore_errors=True)
    if not data_existed:
        shutil.rmtree(os.path.join(bindir, 'Data'), ignore_errors=True)

    if fault:
        faults += 1
        if faults >= a.max_faults:
            say(f'stopping: {faults} faults in {run} runs')
            break
        time.sleep(15)  # let the driver settle after a reset
    else:
        time.sleep(4)  # the server releases the account
    if not ingame:
        say('not in game; waiting 20 s before the next run')
        time.sleep(20)

say(f'== {a.tag} finished: {run} runs, {faults} faults')
