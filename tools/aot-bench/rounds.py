# Interleaved rounds: rounds.py <out.jsonl> <rounds> name=exe [name=exe ...]   (extra env: ROUNDS_ENV="K=V;K=V", ROUNDS_ARGS="--sample-dir X")
# Takes the CUO_ACCOUNT_LOCK file (testing-playbook lock protocol) around each client run.
import datetime, os, subprocess, sys, time
here = os.path.dirname(os.path.abspath(__file__))
LOCK = os.environ.get('CUO_ACCOUNT_LOCK') or sys.exit('set CUO_ACCOUNT_LOCK to <scratchpad>/<account>.lock')
PLAN = 'vsync:off;world:gpu;britain:1495 1629:1;bank:1430 1690:1;zoomout:1495 1629:2.5'
out, n = sys.argv[1], int(sys.argv[2])
variants = [v.split('=', 1) for v in sys.argv[3:]]
extra = []
for kv in filter(None, os.environ.get('ROUNDS_ENV', '').split(';')):
    extra += ['--env', kv]
sample_dir = os.environ.get('ROUNDS_SAMPLE_DIR')

def acquire():
    while True:
        if os.path.exists(LOCK.replace('.lock', '.owner-hold')):
            time.sleep(10); continue
        try:
            if os.path.exists(LOCK) and time.time() - os.path.getmtime(LOCK) > 15 * 60:
                os.remove(LOCK)  # stale (older than 15 min): take it over
        except OSError:
            pass
        try:
            fd = os.open(LOCK, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            os.write(fd, f'aot-bench {datetime.datetime.now().isoformat(timespec="seconds")}\n'.encode()); os.close(fd)
            return
        except FileExistsError:
            time.sleep(5)

for r in range(n):
    for name, exe in (variants if r % 2 == 0 else variants[::-1]):
        for attempt in range(3):
            tag = f'{name}#{r}'
            log = os.path.join(here, 'logs', f'{name}-{r}-{attempt}.log')
            os.makedirs(os.path.dirname(log), exist_ok=True)
            cmd = [sys.executable, '-I', os.path.join(here, 'bench.py'), '--exe', exe, '--tag', tag, '--out', out, '--keeplog', log, '--plan', PLAN] + extra
            if sample_dir: cmd += ['--sample', os.path.join(sample_dir, f'{name}-{r}.txt')]
            acquire()
            try:
                res = subprocess.run(cmd, capture_output=True, text=True)
            finally:
                os.remove(LOCK)
            print(res.stdout.strip(), res.stderr.strip()[-500:], flush=True)
            if ' 0 windows' not in res.stdout:
                break
            time.sleep(10)
        time.sleep(3)
