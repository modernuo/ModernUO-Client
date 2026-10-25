# User-mode IP sampler (no admin): suspends the busiest thread ~1 kHz, reads RIP (and a few return addresses off the stack
# for a rough caller view is NOT done; exclusive only), symbolises offline with dbghelp against the module PDBs.
import ctypes, ctypes.wintypes as wt, collections, os, threading, time

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
psapi = ctypes.WinDLL('psapi', use_last_error=True)
winmm = ctypes.WinDLL('winmm')
k32.OpenThread.restype = wt.HANDLE
k32.OpenProcess.restype = wt.HANDLE
k32.CreateToolhelp32Snapshot.restype = wt.HANDLE

class THREADENTRY32(ctypes.Structure):
    _fields_ = [('dwSize', wt.DWORD), ('cntUsage', wt.DWORD), ('th32ThreadID', wt.DWORD), ('th32OwnerProcessID', wt.DWORD),
                ('tpBasePri', ctypes.c_long), ('tpDeltaPri', ctypes.c_long), ('dwFlags', wt.DWORD)]

def threads(pid):
    snap = k32.CreateToolhelp32Snapshot(0x4, 0)
    te = THREADENTRY32(); te.dwSize = ctypes.sizeof(te); out = []
    ok = k32.Thread32First(snap, ctypes.byref(te))
    while ok:
        if te.th32OwnerProcessID == pid: out.append(te.th32ThreadID)
        ok = k32.Thread32Next(snap, ctypes.byref(te))
    k32.CloseHandle(snap)
    return out

def thread_cpu(h):
    c, e, k, u = (wt.FILETIME() for _ in range(4))
    k32.GetThreadTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u))
    return (u.dwHighDateTime << 32 | u.dwLowDateTime) + (k.dwHighDateTime << 32 | k.dwLowDateTime)

def busiest_thread(pid, window=1.0):
    hs = {}
    for tid in threads(pid):
        h = k32.OpenThread(0x0040 | 0x0002 | 0x0008 | 0x0010, False, tid)  # query_info, suspend, get_context, ...
        if h: hs[tid] = h
    a = {t: thread_cpu(h) for t, h in hs.items()}
    time.sleep(window)
    b = {t: thread_cpu(h) - a[t] for t, h in hs.items()}
    best = max(b, key=b.get)
    for t, h in hs.items():
        if t != best: k32.CloseHandle(h)
    return best, hs[best]

def modules(pid):
    hp = k32.OpenProcess(0x0410, False, pid)
    arr = (ctypes.c_void_p * 1024)(); need = wt.DWORD()
    psapi.EnumProcessModulesEx(hp, arr, ctypes.sizeof(arr), ctypes.byref(need), 3)
    out = []
    class MI(ctypes.Structure): _fields_ = [('base', ctypes.c_void_p), ('size', wt.DWORD), ('entry', ctypes.c_void_p)]
    for i in range(need.value // 8):
        name = ctypes.create_unicode_buffer(520); mi = MI()
        psapi.GetModuleFileNameExW(hp, ctypes.c_void_p(arr[i]), name, 520)
        psapi.GetModuleInformation(hp, ctypes.c_void_p(arr[i]), ctypes.byref(mi), ctypes.sizeof(mi))
        out.append((mi.base or 0, mi.size, name.value))
    k32.CloseHandle(hp)
    return out

class Sampler:
    def __init__(self, pid):
        self.pid = pid; self.samples = collections.defaultdict(collections.Counter); self.label = None
        self.stop = threading.Event(); self.thread = None; self.h = None; self.mods = []

    def start(self):
        self.tid, self.h = busiest_thread(self.pid)
        self.hp = k32.OpenProcess(0x0410, False, self.pid)
        m = modules(self.pid)
        self.exe = (m[0][0], m[0][0] + m[0][1]) if m and m[0][2].lower().endswith('.exe') else None
        self.thread = threading.Thread(target=self._run, daemon=True); self.thread.start()

    def _run(self):
        buf = ctypes.create_string_buffer(1232 + 16)
        stack = (ctypes.c_uint64 * 1024)()
        addr = (ctypes.addressof(buf) + 15) & ~15
        winmm.timeBeginPeriod(1)
        try:
            while not self.stop.is_set():
                lab = self.label
                if lab:
                    ctypes.memmove(addr + 0x30, ctypes.byref(wt.DWORD(0x100001)), 4)  # ContextFlags = CONTEXT_CONTROL
                    if k32.SuspendThread(self.h) != 0xFFFFFFFF:
                        ok = k32.GetThreadContext(self.h, ctypes.c_void_p(addr))
                        k32.ResumeThread(self.h)
                        if ok:
                            ip = ctypes.c_uint64.from_address(addr + 0xF8).value
                            rsp = ctypes.c_uint64.from_address(addr + 0x98).value
                            caller = 0
                            if self.exe and not (self.exe[0] <= ip < self.exe[1]):
                                got = ctypes.c_size_t()
                                if k32.ReadProcessMemory(self.hp, ctypes.c_void_p(rsp), stack, ctypes.sizeof(stack), ctypes.byref(got)):
                                    for i in range(got.value // 8):
                                        v = stack[i]
                                        if self.exe[0] <= v < self.exe[1]:
                                            caller = v; break
                            self.samples[lab][(ip, caller)] += 1
                time.sleep(0.001)
        finally:
            winmm.timeEndPeriod(1)

    def finish(self):
        self.stop.set()
        if self.thread: self.thread.join(2)

def symbolise(samples, mods, top=60):
    dbg = ctypes.WinDLL(r'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\dbghelp.dll', use_last_error=True)
    dbg.SymSetOptions(0x2 | 0x4)  # UNDNAME | DEFERRED_LOADS
    hfake = wt.HANDLE(0x1234)
    cache_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'symcache')
    dbg.SymInitializeW(hfake, f'srv*{cache_dir}*https://msdl.microsoft.com/download/symbols', False)
    dbg.SymLoadModuleExW.restype = ctypes.c_uint64
    for base, size, path in mods:
        dbg.SymLoadModuleExW(hfake, None, path, None, ctypes.c_uint64(base), size, None, 0)
    class SYMBOL_INFOW(ctypes.Structure):
        _fields_ = [('SizeOfStruct', wt.ULONG), ('TypeIndex', wt.ULONG), ('Reserved', ctypes.c_uint64 * 2), ('Index', wt.ULONG), ('Size', wt.ULONG),
                    ('ModBase', ctypes.c_uint64), ('Flags', wt.ULONG), ('Value', ctypes.c_uint64), ('Address', ctypes.c_uint64), ('Register', wt.ULONG),
                    ('Scope', wt.ULONG), ('Tag', wt.ULONG), ('NameLen', wt.ULONG), ('MaxNameLen', wt.ULONG), ('Name', wt.WCHAR * 1024)]
    cache = {}
    def name(ip):
        si = SYMBOL_INFOW(); si.SizeOfStruct = 88; si.MaxNameLen = 1024; disp = ctypes.c_uint64()
        if dbg.SymFromAddrW(hfake, ctypes.c_uint64(ip), ctypes.byref(disp), ctypes.byref(si)):
            return si.Name
        for base, size, path in mods:
            if base <= ip < base + size: return os.path.basename(path) + '!?'
        return '?'
    out = {}
    for lab, ctr in samples.items():
        fn = collections.Counter(); callers = collections.defaultdict(collections.Counter)
        for (ip, caller), n in ctr.items():
            if ip not in cache: cache[ip] = name(ip)
            if caller and caller not in cache: cache[caller] = name(caller)
            fn[cache[ip]] += n
            callers[cache[ip]][cache[caller] if caller else '-'] += n
        rows = []
        for nm, n in fn.most_common(top):
            cs = callers[nm].most_common(3)
            rows.append((nm + ('' if cs[0][0] == '-' and len(cs) == 1 else '   <- ' + '; '.join(f'{c} {k}' for c, k in cs)), n))
        out[lab] = (sum(ctr.values()), rows)
    dbg.SymCleanup(hfake)
    return out
