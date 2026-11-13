# JIT vs AOT FrameStats bench for ClassicUO. Password from CUO_TEST_PASSWORD, stored only as an encrypted settings value
# in a throwaway temp folder (never on a command line). Writes one JSON line per FRAMESTATS window to --out.
import argparse, json, os, platform, shutil, subprocess, sys, tempfile, time, ctypes, ctypes.wintypes as wt

p = argparse.ArgumentParser()
p.add_argument('--exe', required=True)
p.add_argument('--tag', required=True)
p.add_argument('--out', required=True)
p.add_argument('--plan', default='vsync:off;world:gpu;here:0 0:1;zoomout:0 0:2.5')
p.add_argument('--windows', default='3')
p.add_argument('--user', default=os.environ.get('CUO_TEST_USER', 'kbatman4'))
p.add_argument('--mode', default='gpu')
p.add_argument('--timeout', type=float, default=240)
p.add_argument('--env', action='append', default=[])
p.add_argument('--keeplog', default='')
p.add_argument('--sample', default='')
a = p.parse_args()

def enc_password(pw):
    key = os.environ.get('COMPUTERNAME', platform.node())
    return '1-' + ''.join('%02X' % (ord(c) ^ ord(key[i % len(key)])) for i, c in enumerate(pw))

pw = os.environ.get('CUO_TEST_PASSWORD')
if not pw: sys.exit('CUO_TEST_PASSWORD not set')
work = tempfile.mkdtemp(prefix=f'cuo-aotpgo-{a.tag}-')
os.makedirs(os.path.join(work, 'profiles'))
settings = dict(username=a.user, password=enc_password(pw), ip='127.0.0.1', port=2593, ultimaonlinedirectory=r'C:\Ultima Online Classic',
                clientversion='7.0.103.0', lastservernum=1, fps=0, vsync=False, settings_version=1, present_at_display_rate=False, is_win_maximized=False, saveaccount=False, autologin=True,
                login_music=False, plugins=[], pack=r'C:\Repositories\uo-packs\classic-1.0', world_renderer=a.mode)
spath = os.path.join(work, 'settings.json')
json.dump(settings, open(spath, 'w'))
log = os.path.join(work, 'client.log')
env = {k: v for k, v in os.environ.items() if not k.upper().startswith('CUO_') and not k.upper().startswith('DOTNET_')}
env.update(DOTNET_GCHeapHardLimit='0x200000000', CUO_FRAMESTATS='1', CUO_BENCH_EXIT='1', CUO_BENCH_SIZE='1920x1080',
           CUO_BENCH_WINDOWS=a.windows, CUO_BENCH_PLAN=a.plan)
for kv in a.env:
    k, v = kv.split('=', 1); env[k] = v
exe = os.path.abspath(a.exe)
args = [exe, '-settings', spath, '-profilespath', os.path.join(work, 'profiles'), '-skiploginscreen', '-autologin', 'true',
        '-saveaccount', 'false', '-worldrenderer', a.mode, '-fps', '0', '-vsync', 'false', '-nomessagebox']
t0 = time.time()
proc = subprocess.Popen(args, cwd=os.path.dirname(exe), env=env, stdout=open(log, 'w'), stderr=subprocess.STDOUT)
rows = []
try:
    if a.sample:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import sampler
        smp = None; seen = 0
        while proc.poll() is None and time.time() - t0 < a.timeout:
            lines = open(log, encoding='utf-8', errors='replace').read().splitlines()
            for line in lines[seen:]:
                if '[bench]' in line and 'measuring' in line:
                    lab = line.split('[bench] ', 1)[1].split(' at ')[0]
                    if smp is None:
                        smp = sampler.Sampler(proc.pid); smp.start(); mods = sampler.modules(proc.pid)
                    smp.label = lab
                elif '[bench]' in line and (' done at ' in line or 'plan done' in line) and smp:
                    smp.label = None
            seen = len(lines)
            time.sleep(0.2)
        if smp:
            smp.finish()
            res = sampler.symbolise(smp.samples, mods)
            with open(a.sample, 'w', encoding='utf-8') as f:
                for lab, (total, top) in res.items():
                    f.write(f'== {lab}: {total} samples (thread {smp.tid})\n')
                    for nm, n in top: f.write(f'{100.0 * n / total:6.2f}% {n:6} {nm}\n')
    try:
        proc.wait(max(1, a.timeout - (time.time() - t0)))
    except subprocess.TimeoutExpired:
        print(f'{a.tag}: timeout; closing', flush=True)
finally:
    if proc.poll() is None:
        user32 = ctypes.WinDLL('user32')
        def cb(hwnd, _):
            pid = wt.DWORD(); user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value == proc.pid: user32.PostMessageW(hwnd, 0x0010, 0, 0)
            return True
        user32.EnumWindows(ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)(cb), 0)
        try: proc.wait(15)
        except subprocess.TimeoutExpired: proc.kill(); proc.wait(10)
    raw = open(log, encoding='utf-8', errors='replace').read()
    out_lines = []
    for line in raw.splitlines():
        i = line.find('FRAMESTATS {')
        if i >= 0:
            try:
                d = json.loads(line[i + 11:]); d['run'] = a.tag; rows.append(d)
            except ValueError as e: print('bad FRAMESTATS line', e)
            out_lines.append(line)
        else:
            out_lines.append(line.replace(pw, '***') if len(pw) >= 3 else line)
    text = '\n'.join(out_lines)
    with open(a.out, 'a', encoding='utf-8') as f:
        for d in rows: f.write(json.dumps(d) + '\n')
    if a.keeplog: open(a.keeplog, 'w', encoding='utf-8').write(text)
    shutil.rmtree(work, ignore_errors=True)
    print(f'{a.tag}: exit {proc.returncode}, {len(rows)} windows, {time.time()-t0:.0f} s, temp removed {not os.path.exists(work)}', flush=True)
