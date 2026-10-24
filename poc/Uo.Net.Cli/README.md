# Uo.Net.Cli

A headless test client for `poc/Uo.Net`. It logs in, enters the world, teleports, walks, stays connected, then disconnects.

## Local server

This uses the owner's ModernUO build that is already in `C:\Repositories\ModernUO\Distribution`. The scripts don't rebuild it and don't change its config. It listens on `0.0.0.0:2593` and accounts are created automatically.

```powershell
powershell -ExecutionPolicy Bypass -File poc\Uo.Net.Cli\scripts\start-modernuo.ps1   # log: %TEMP%\modernuo-poc.log
powershell -ExecutionPolicy Bypass -File poc\Uo.Net.Cli\scripts\stop-modernuo.ps1
```

- **Start** opens ModernUO in its own minimized console. Its stdout goes to the log file. Its stdin stays a console, so ModernUO doesn't switch to headless mode.
- **Stop** sends Ctrl+Break to that console. ModernUO handles it like its own `shutdown` command: `Core.Kill()`, then it waits for any world save in progress to finish. The script never force-kills the process. It uses Ctrl+Break because a launching shell can set an "ignore Ctrl+C" flag that child processes inherit.

## Run

Credentials come from environment variables or command-line arguments. They are never stored in a file.

```powershell
$env:UO_USER = '<account>'; $env:UO_PASS = '<password>'
dotnet run -c Release --project poc\Uo.Net.Cli -- --runs 3
```

| Option | Default | Purpose |
|---|---|---|
| `--host`, `--port` | `127.0.0.1`, `2593` | Login server |
| `--char NAME` | first character | Character to play. If it isn't there, it's created (0xF8). |
| `--go "x y [z]"`, `--no-go` | `1495 1629` | Sends the `[go` admin command as unicode speech (needs staff) |
| `--pattern`, `--steps` | `"E5 S5 N5 W5"`, `20` | Walk path: direction plus count. The default goes out and back, so it ends where it started. |
| `--run` | off | Run instead of walk (200 ms between steps instead of 400 ms) |
| `--stay` | `30` | Seconds to stay connected; the client pings every 10 s |
| `--runs` | `1` | Repeat the whole flow |
| `--recv-chunk N` | 16384 | Cap on bytes per socket read. `1` tests Huffman and packet reassembly. |
| `--log-packets` | off | Hex dump of every packet. The login packets 0x80 and 0x91 are redacted. |
| `--verbose` | off | Print every move result and every message |

To check NativeAOT: `dotnet publish poc\Uo.Net.Cli -c Release -r win-x64 -p:PublishAot=true`. `vswhere.exe` must be on PATH.
