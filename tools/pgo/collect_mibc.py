"""Collect a static-PGO profile (.mibc) for the NativeAOT ClassicUO build from a JIT run.

Runs the Release JIT client (bin/Release/net10.0/cuo.exe) through a DevBench plan against a local shard with an EventPipe
trace of the JIT's Dynamic PGO data (instrumentation counts and class/method histograms), then converts the trace with
dotnet-pgo into a .mibc file (default %TEMP%/cuo.mibc). Publish with -p:CuoMibcPath=<file> to pass it to ILC. Static PGO is
opt-in: on 2026-10-25 it gave no measurable gain (docs/modern-client/testing/aot-performance.md).

Usage (from the repo root; needs the .NET SDK, Python 3, a running shard and a test account with a character):

    set CUO_PGO_PASSWORD=<password>          (or type it at the masked prompt)
    python tools/pgo/collect_mibc.py --user <account>

The password is written only as ClassicUO's encrypted settings value into a throwaway settings folder under %TEMP%,
never on a command line or in the repo. The temp folder and the trace are deleted at the end (--keep-trace keeps the trace).
dotnet-pgo is not on nuget.org; it is installed into %TEMP%/cuo-pgo-tools from the dnceng dotnet10-transport feed.
See docs/modern-client/testing/aot-performance.md.
"""
import argparse, getpass, glob, json, os, platform, shutil, subprocess, sys, tempfile, time

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
FEED = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10-transport/nuget/v3/index.json'
PGO_VERSION = '10.0.13-servicing.26504.104'
# Microsoft-Windows-DotNETRuntime keywords: Loader (0x8) | Jit (0x10) | Type (0x80000) | Compilation, CompilationDiagnostic,
# MethodDiagnostic, TypeDiagnostic (0x1000000000..0x8000000000) | JitInstrumentationData (0x10000000000), at verbose level:
# the JIT then reports each method's Dynamic PGO counts and type/method histograms when it builds tier-1 code.
PROVIDERS = 'Microsoft-Windows-DotNETRuntime:0x1F000080018:5'
# Britain at zoom 1 and zoomed out, Gpu world renderer (the steady-state Draw/UI/composite paths), then Legacy so its
# batcher paths get profiled too. [go needs a staff account; without it the plan measures wherever the character stands.
DEFAULT_PLAN = 'vsync:off;world:gpu;britain:1495 1629:1;zoomout:1495 1629:2.5;world:legacy;britain_l:1495 1629:1;zoomout_l:1495 1629:2.5'


def enc_password(pw):
    # same scheme as ClassicUO.Utility.Crypter (XOR with the machine name)
    key = os.environ.get('COMPUTERNAME') or platform.node()
    return '1-' + ''.join('%02X' % (ord(c) ^ ord(key[i % len(key)])) for i, c in enumerate(pw))


def run(cmd, **kw):
    print('>', ' '.join(cmd), flush=True)
    subprocess.run(cmd, check=True, **kw)


def ensure_dotnet_pgo(env):
    tools = os.path.join(tempfile.gettempdir(), 'cuo-pgo-tools')
    exe = os.path.join(tools, 'dotnet-pgo.exe' if os.name == 'nt' else 'dotnet-pgo')
    if not os.path.exists(exe):
        run(['dotnet', 'tool', 'install', 'dotnet-pgo', '--version', PGO_VERSION, '--tool-path', tools, '--add-source', FEED], env=env)
    return exe


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--user', default=os.environ.get('CUO_PGO_USER'), help='shard account (or CUO_PGO_USER)')
    p.add_argument('--server', default='127.0.0.1')
    p.add_argument('--port', type=int, default=2593)
    p.add_argument('--uo-path', default=r'C:\Ultima Online Classic')
    p.add_argument('--client-version', default='7.0.103.0')
    p.add_argument('--pack', default=r'C:\Repositories\uo-packs\classic-1.0')
    p.add_argument('--plan', default=DEFAULT_PLAN, help='CUO_BENCH_PLAN for the profiled run')
    p.add_argument('--windows', default='4', help='5-s windows per plan entry')
    p.add_argument('--out', default=os.path.join(tempfile.gettempdir(), 'cuo.mibc'))
    p.add_argument('--no-build', action='store_true')
    p.add_argument('--keep-trace', default='', help='copy the .nettrace here')
    p.add_argument('--timeout', type=float, default=600)
    a = p.parse_args()
    if not a.user:
        sys.exit('--user (or CUO_PGO_USER) is required')
    pw = os.environ.get('CUO_PGO_PASSWORD') or getpass.getpass(f'Password for {a.user}: ')

    env = dict(os.environ)
    env['DOTNET_GCHeapHardLimit'] = '0x200000000'
    if not a.no_build:
        run(['dotnet', 'build', os.path.join(REPO, 'src', 'ClassicUO.Client', 'ClassicUO.Client.csproj'), '-c', 'Release', '-nologo', '-v', 'q'], env=env)
    bindir = os.path.join(REPO, 'bin', 'Release', 'net10.0')
    exe = os.path.join(bindir, 'cuo.exe' if os.name == 'nt' else 'cuo')
    pgo = ensure_dotnet_pgo(env)

    work = tempfile.mkdtemp(prefix='cuo-pgo-')
    data_existed = os.path.exists(os.path.join(bindir, 'Data'))
    try:
        os.makedirs(os.path.join(work, 'profiles'))
        settings = dict(username=a.user, password=enc_password(pw), ip=a.server, port=a.port, ultimaonlinedirectory=a.uo_path,
                        clientversion=a.client_version, lastservernum=1, fps=0, vsync=False, is_win_maximized=False, saveaccount=False,
                        autologin=True, login_music=False, plugins=[], pack=a.pack, world_renderer='gpu')
        spath = os.path.join(work, 'settings.json')
        with open(spath, 'w') as f:
            json.dump(settings, f)
        trace = os.path.join(work, 'cuo.nettrace')
        cenv = {k: v for k, v in env.items() if not k.upper().startswith('CUO_')}
        cenv.update({
            'DOTNET_GCHeapHardLimit': '0x200000000',
            'CUO_FRAMESTATS': '1', 'CUO_BENCH_EXIT': '1', 'CUO_BENCH_SIZE': '1920x1080', 'CUO_BENCH_WINDOWS': a.windows, 'CUO_BENCH_PLAN': a.plan,
            # Dynamic PGO data comes out in the JIT events when tier-1 code is built from the instrumented tier.
            'DOTNET_TieredPGO': '1', 'DOTNET_ReadyToRun': '0',
            'DOTNET_EnableEventPipe': '1', 'DOTNET_EventPipeOutputPath': trace, 'DOTNET_EventPipeConfig': PROVIDERS,
            'DOTNET_EventPipeCircularMB': '2048',
        })
        log = os.path.join(work, 'client.log')
        args = [exe, '-settings', spath, '-profilespath', os.path.join(work, 'profiles'), '-skiploginscreen', '-autologin', 'true',
                '-saveaccount', 'false', '-worldrenderer', 'gpu', '-fps', '0', '-vsync', 'false', '-nomessagebox']
        print(f'profiling run: {exe} (plan: {a.plan})', flush=True)
        with open(log, 'w') as lf:
            proc = subprocess.Popen(args, cwd=bindir, env=cenv, stdout=lf, stderr=subprocess.STDOUT)
            try:
                proc.wait(a.timeout)
            except subprocess.TimeoutExpired:
                proc.kill(); proc.wait()
                sys.exit('the profiled run timed out')
        text = open(log, encoding='utf-8', errors='replace').read()
        if '[bench] plan done' not in text:
            masked = text.replace(pw, '***') if len(pw) >= 3 else text
            sys.exit('the profiled run did not finish the plan; last log lines:\n' + '\n'.join(masked.splitlines()[-30:]))
        print(f'trace: {os.path.getsize(trace) / 1048576:.0f} MiB', flush=True)
        if a.keep_trace:
            shutil.copy(trace, a.keep_trace)
        os.makedirs(os.path.dirname(a.out), exist_ok=True)
        refs = []
        for pattern in ('*.dll',):
            refs += ['--reference', os.path.join(bindir, pattern)]
        run([pgo, 'create-mibc', '--trace', trace, '--output', a.out, '--compressed'] + refs, env=env)
        print(f'wrote {a.out} ({os.path.getsize(a.out) / 1024:.0f} KiB)')
    finally:
        shutil.rmtree(work, ignore_errors=True)
        if not data_existed:
            shutil.rmtree(os.path.join(bindir, 'Data'), ignore_errors=True)


if __name__ == '__main__':
    main()
