# startup.py N name=exe ... : time from process start to the first visible top-level window, then close it (no login)
import ctypes, ctypes.wintypes as wt, json, os, shutil, statistics as st, subprocess, sys, tempfile, time
user32 = ctypes.WinDLL('user32')
n = int(sys.argv[1]); variants = [v.split('=', 1) for v in sys.argv[2:]]
def windows(pid):
    found = []
    def cb(h, _):
        p = wt.DWORD(); user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h): found.append(h)
        return True
    user32.EnumWindows(ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)(cb), 0)
    return found
res = {name: [] for name, _ in variants}
for i in range(n):
    for name, exe in variants:
        work = tempfile.mkdtemp(prefix='cuo-startup-')
        s = os.path.join(work, 'settings.json')
        json.dump(dict(ip='127.0.0.1', port=2593, ultimaonlinedirectory=r'C:\Ultima Online Classic', clientversion='7.0.103.0',
                       fps=60, saveaccount=False, autologin=False, login_music=False, plugins=[], pack=r'C:\Repositories\uo-packs\classic-1.0'), open(s, 'w'))
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith(('CUO_', 'DOTNET_'))}
        env['DOTNET_GCHeapHardLimit'] = '0x200000000'
        t0 = time.perf_counter()
        p = subprocess.Popen([exe, '-settings', s, '-profilespath', os.path.join(work, 'p'), '-nomessagebox'], cwd=os.path.dirname(exe), env=env,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        t = None
        while time.perf_counter() - t0 < 30 and p.poll() is None:
            if windows(p.pid): t = time.perf_counter() - t0; break
            time.sleep(0.005)
        time.sleep(1.5)
        for h in windows(p.pid): user32.PostMessageW(h, 0x0010, 0, 0)
        try: p.wait(15)
        except subprocess.TimeoutExpired: p.kill(); p.wait()
        shutil.rmtree(work, ignore_errors=True)
        if os.path.isdir(os.path.join(os.path.dirname(exe), 'Data')): pass
        res[name].append(t)
        print(name, i, t, flush=True)
for name, v in res.items():
    v = [x for x in v if x]
    print(f'{name}: median {st.median(v)*1000:.0f} ms (min {min(v)*1000:.0f}, max {max(v)*1000:.0f}, n {len(v)})')
