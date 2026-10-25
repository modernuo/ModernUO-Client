<#
.SYNOPSIS
    Legacy vs Gpu world renderer FPS test for the NativeAOT ClassicUO build. One command, about 6 minutes.

.DESCRIPTION
    Logs in to the local ModernUO shard with a throwaway settings/profile folder under %TEMP% and runs the DevBench
    plan: Britain, Britain bank, zoomed out and a short walk at 1920x1080, then zoomed out in a maximised window,
    then the large-scale test scene (about 2,000 mobiles and 19,500 items, Felucca 5156 2150) at 1920x1080, each spot
    under the Legacy and then the Gpu world renderer. The scene spot needs a GameMaster+ account: it says
    [TestScene go before and [TestScene return after, so the character ends where (and on the facet where) it
    started. FrameStats is on, FPS uncapped, VSync off, inactive throttling off and the window stays on top. Do not
    touch the mouse or keyboard while it runs.

    Writes results-<timestamp>.md to -ResultsDir and prints the same tables. The client is closed and the temp
    folder deleted at the end, also on error or Ctrl+C.

    The password is asked for with a masked prompt and written, encrypted the way the client stores it (Crypter), into the
    throwaway settings.json; it is never on a command line, and the temp folder is deleted at the end. For unattended
    runs, set CUO_FPS_TEST_PASSWORD instead (optional).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\Repositories\classicuo\tools\fps-test\run-fps-test.ps1
#>
param(
    [string]$Username = 'kbatman2',
    [string]$Bin = 'C:\Repositories\cuo-fps-test\bin',
    [string]$ResultsDir = 'C:\Repositories\cuo-fps-test',
    [string]$Pack = 'C:\Repositories\uo-packs\classic-1.0',
    [string]$UoPath = 'C:\Ultima Online Classic',
    [string]$ClientVersion = '7.0.103.0',
    [string]$Server = '127.0.0.1',
    [int]$Port = 2593,
    # client area for the main spots, and for the last spot ("max" = maximised window)
    [string]$Size = '1920x1080',
    [string]$LargeSize = 'max',
    # 5-second measurement windows per spot
    [int]$Windows = 3,
    [int]$TimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
$started = Get-Date
$stamp = $started.ToString('yyyyMMdd-HHmmss')

function Fmt([double]$v, [string]$f) { $v.ToString($f, $inv) }

function Quote-Arg([string]$s) {
    if ($s.Length -gt 0 -and $s -notmatch '[\s"]') { return $s }
    '"' + (($s -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

# ---- spots: Legacy then Gpu at each ----
$spots = @(
    @{ Key = 'britain'; Label = 'Britain'; Entry = '1495 1629:1' },
    @{ Key = 'bank'; Label = 'Britain bank'; Entry = '1430 1690:1' },
    @{ Key = 'zoomout'; Label = 'Zoomed out'; Entry = '1495 1629:2.5' },
    @{ Key = 'walk'; Label = 'Walk'; Entry = 'walk:1' },
    @{ Key = 'zoomout_large'; Label = 'Zoomed out, large window'; Entry = '1495 1629:2.5'; Large = $true },
    # the large-scale test scene (docs/modern-client/testing/large-scale.md), built by ModernUO's [TestScene create;
    # [TestScene go remembers where the character was (any facet) and [TestScene return puts it back
    @{ Key = 'scene'; Label = 'Large scene'; Entry = '5156 2150:1'; Scene = $true }
)
$modes = @('legacy', 'gpu')
$modeName = @{ legacy = 'Legacy'; gpu = 'Gpu' }

$planEntries = New-Object System.Collections.Generic.List[string]
$planEntries.Add('vsync:off')
$runs = New-Object System.Collections.Generic.List[object]
$largeAdded = $false
# plan entries that are not measured spots, in seconds (DevBench waits 4 s after say:, 3 s after size:)
$extraSeconds = 0

foreach ($s in $spots) {
    if ($s.Large -and -not $largeAdded) {
        $planEntries.Add("size:$LargeSize")
        $largeAdded = $true
        $extraSeconds += 3
    }
    elseif (-not $s.Large -and $largeAdded) {
        # back to the main size after the large-window spot
        $planEntries.Add("size:$Size")
        $largeAdded = $false
        $extraSeconds += 3
    }

    if ($s.Scene) {
        $planEntries.Add('say:[TestScene go')
        $extraSeconds += 4
    }

    foreach ($m in $modes) {
        $tag = "$($s.Key)_$m"
        $planEntries.Add("world:$m")
        $planEntries.Add("${tag}:$($s.Entry)")
        $runs.Add(@{ Tag = $tag; Spot = $s; Mode = $m; Index = $runs.Count + 1 })
    }

    if ($s.Scene) {
        $planEntries.Add('say:[TestScene return')
        $extraSeconds += 4
    }
}

$total = $runs.Count
$estimate = [int](25 + $total * (1 + 8 + 5 * $Windows + 2) + $extraSeconds + 5)

# ---- pre-flight ----
$exe = Join-Path $Bin 'cuo.exe'
$problems = @()
if (-not (Test-Path $exe)) { $problems += "Client not found: $exe (publish it with tools\fps-test\publish-aot.ps1)" }
if (-not (Test-Path $Pack)) { $problems += "Pack not found: $Pack" }
if (-not (Test-Path $UoPath)) { $problems += "UO directory not found: $UoPath (gumps, fonts and cliloc still load from it)" }
if ($problems) {
    $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}

$tcp = New-Object System.Net.Sockets.TcpClient
$reachable = $false
try {
    $ar = $tcp.BeginConnect($Server, $Port, $null, $null)
    $reachable = $ar.AsyncWaitHandle.WaitOne(3000) -and $tcp.Connected
}
catch { }
finally { $tcp.Close() }

if (-not $reachable) {
    Write-Host "The ModernUO server is not reachable at ${Server}:$Port." -ForegroundColor Red
    Write-Host 'Start it first (C:\Repositories\ModernUO\Distribution, or poc\Uo.Net.Cli\scripts\start-modernuo.ps1) and run this again.' -ForegroundColor Red
    exit 1
}

if (Get-Process cuo -ErrorAction SilentlyContinue) {
    Write-Host 'Note: another cuo.exe is running. If it is logged in as the same account, the login will fail.' -ForegroundColor Yellow
}

if ($env:CUO_FPS_TEST_PASSWORD) {
    $password = $env:CUO_FPS_TEST_PASSWORD
    Write-Host "Using the password from CUO_FPS_TEST_PASSWORD for $Username."
}
else {
    $secure = Read-Host -AsSecureString "Password for $Username"
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

if ([string]::IsNullOrEmpty($password)) {
    Write-Host 'No password given.' -ForegroundColor Red
    exit 1
}

# ClassicUO.Utility.Crypter.Encrypt: "1-" + hex of the ASCII bytes XORed with the machine name, which is what the client's
# settings.json holds, so the password never goes on the client's command line
function Protect-Password([string]$plain) {
    $key = [Environment]::MachineName
    $bytes = [Text.Encoding]::ASCII.GetBytes($plain)
    $sb = New-Object Text.StringBuilder('1-')
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $null = $sb.Append(($bytes[$i] -bxor [byte][char]$key[$i % $key.Length]).ToString('X2'))
    }
    $sb.ToString()
}

function Hide-Secret([string]$text) {
    if ($password -and $password.Length -ge 3) { $text.Replace($password, '***') } else { $text }
}

# ---- machine info (before the run, so WMI doesn't run during it) ----
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem
$gpus = @(Get-CimInstance Win32_VideoController)
$ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$power = (powercfg /getactivescheme 2>$null | Out-String).Trim() -replace '^.*\((.*)\)\s*$', '$1'
$buildInfoPath = Join-Path $Bin 'build-info.txt'
$buildInfo = if (Test-Path $buildInfoPath) { (Get-Content $buildInfoPath -Raw).Trim() } else { 'unknown (no build-info.txt in the bin folder)' }

# ---- throwaway settings/profile folder ----
$work = Join-Path $env:TEMP "cuo-fps-test-$stamp"
New-Item -ItemType Directory -Force (Join-Path $work 'profiles') | Out-Null
$settingsPath = Join-Path $work 'settings.json'
$settings = [ordered]@{
    username = $Username; password = (Protect-Password $password)
    ip = $Server; port = $Port; ultimaonlinedirectory = $UoPath; clientversion = $ClientVersion; lastservernum = 1
    fps = 0; vsync = $false; is_win_maximized = $false; saveaccount = $false; autologin = $true; login_music = $false
    plugins = @(); pack = $Pack; world_renderer = 'gpu'
}
$settings | ConvertTo-Json | Set-Content -Encoding ASCII $settingsPath
$log = Join-Path $work 'client.log'
$dataExisted = Test-Path (Join-Path $Bin 'Data')

$envVars = [ordered]@{
    DOTNET_GCHeapHardLimit = '0x200000000'
    CUO_FRAMESTATS = '1'; CUO_FRAMESTATS_PHASES = $null
    CUO_BENCH_EXIT = '1'; CUO_BENCH_TOPMOST = '1'; CUO_BENCH_SIZE = $Size; CUO_BENCH_WINDOWS = "$Windows"
    CUO_BENCH_PLAN = ($planEntries -join ';')
    CUO_BENCH_GO = $null; CUO_BENCH_SPOTS = $null; CUO_BENCH_UI = $null; CUO_BENCH_PICK = $null; CUO_BENCH_SHOTS = $null
}
$savedEnv = @{}
foreach ($k in $envVars.Keys) {
    $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
    [Environment]::SetEnvironmentVariable($k, $envVars[$k], 'Process')
}

$clientArgs = @('-settings', $settingsPath, '-profilespath', (Join-Path $work 'profiles'),
    '-autologin', 'true', '-skiploginscreen', '-saveaccount', 'false', '-ip', $Server, '-port', "$Port", '-fps', '0', '-vsync', 'false',
    '-worldrenderer', 'gpu', '-pack', $Pack)
$argLine = ($clientArgs | ForEach-Object { Quote-Arg $_ }) -join ' '

Write-Host ''
Write-Host "FPS test: $total spots (Legacy then Gpu at each), $Windows x 5 s per spot, about $([math]::Ceiling($estimate / 60)) minutes."
Write-Host 'The client window stays on top. Please do not touch the mouse or keyboard until it finishes.' -ForegroundColor Yellow
Write-Host "Build: $buildInfo"
Write-Host ''

$proc = $null
$rendererLine = 'not logged (a build from before 2026-10-25''s startup renderer selection)'
$reader = $null
$lines = New-Object System.Collections.Generic.List[string]
$failure = $null

try {
    $proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $Bin -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $proc.Handle
    Write-Host 'Starting the client and logging in...'

    $deadline = $started.AddMinutes($TimeoutMinutes)
    $loginDeadline = (Get-Date).AddMinutes(2)
    $inGame = $false
    $planDone = $false
    $pending = ''
    $sizeNow = ''
    $inScene = $false

    for ($i = 0; $i -lt 50 -and -not (Test-Path $log); $i++) { Start-Sleep -Milliseconds 100 }
    $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite, Delete')
    $reader = New-Object IO.StreamReader($fs)

    while ($true) {
        $exited = $proc.HasExited
        $pending += $reader.ReadToEnd()
        $parts = $pending -split "`r?`n"
        $pending = $parts[-1]

        foreach ($line in $parts[0..($parts.Count - 2)]) {
            if ($parts.Count -lt 2) { break }
            $lines.Add($line)

            if ($line -match '\[bench\] world renderer (\w+) \(gpu available: (\w+)\)') {
                if (-not $inGame) { Write-Host 'In game.'; $inGame = $true }
                if ($Matches[1] -eq 'gpu' -and $Matches[2] -ne 'True') {
                    Write-Host 'WARNING: the Gpu world renderer is not available (FNA3D is not on SDL_GPU); Gpu rows will show Legacy.' -ForegroundColor Red
                }
            }
            elseif ($line -match '\[renderer\] (\w+: .*)$') {
                # the client's startup renderer decision (world_renderer, RendererStartup); -worldrenderer gpu forces Gpu here
                $rendererLine = $Matches[1]
                Write-Host "Renderer: $rendererLine"
            }
            elseif ($line -match '\[bench\] window client (\d+x\d+)') {
                $sizeNow = $Matches[1]
            }
            elseif ($line -match '\[bench\] (\w+):.*; measuring$') {
                $r = $runs | Where-Object { $_.Tag -eq $Matches[1] } | Select-Object -First 1
                if ($r) {
                    $r.Size = $sizeNow
                    if ($line -match 'mobiles (\d+), items (\d+); measuring$') { $r.Mobiles = [int]$Matches[1]; $r.Items = [int]$Matches[2] }
                    Write-Host ("Spot {0}/{1}: {2}, {3} ({4}) - measuring {5} s..." -f $r.Index, $total, $r.Spot.Label, $modeName[$r.Mode], $sizeNow, (5 * $Windows))
                }
            }
            elseif ($line -match '\[bench\] (\w+):.* done at ') {
                Write-Host '    done'
            }
            elseif ($line -match '\[bench\] said \[TestScene (go|return)') {
                $inScene = $Matches[1] -eq 'go'
            }
            elseif ($line -match '\[bench\] plan done') {
                $planDone = $true
            }
        }

        if ($exited) { break }

        if (-not $inGame -and (Get-Date) -gt $loginDeadline) {
            $failure = 'The client did not get in game within 2 minutes (wrong password, account already logged in, or the server refused the login).'
            break
        }

        if ((Get-Date) -gt $deadline) {
            $failure = "The test did not finish within $TimeoutMinutes minutes."
            break
        }

        Start-Sleep -Milliseconds 500
    }

    if (-not $failure -and -not $planDone) {
        if ($proc.ExitCode -eq 0xDEAD) {
            # GpuDeviceLost.ExitCode: the client saw the GPU device lost, saved and exited
            $failure = "GPU device lost (driver reset). The client detected it and exited (exit code 0x{0:X})." -f $proc.ExitCode
        }
        else {
            $failure = "The client exited before the plan finished (exit code {0} / 0x{0:X8})." -f $proc.ExitCode
        }
    }
}
catch {
    $failure = "Error: $($_.Exception.Message)"
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Write-Host 'Closing the client...'
        $null = $proc.CloseMainWindow()
        if (-not $proc.WaitForExit(15000)) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $null = $proc.WaitForExit(5000)
        }
    }

    if ($reader) {
        $rest = $reader.ReadToEnd()
        $reader.Dispose()
        if ($rest) { $pending += $rest }
        if ($pending) { ($pending -split "`r?`n") | Where-Object { $_ } | ForEach-Object { $lines.Add($_) } }
    }

    foreach ($k in $savedEnv.Keys) {
        [Environment]::SetEnvironmentVariable($k, $savedEnv[$k], 'Process')
    }

    if ($failure) {
        # A GPU driver reset (nvlddmkm event 153, a Display TDR 4101, ...) takes the client's graphics device with it
        $gpuEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'nvlddmkm', 'amdkmdag', 'igfx', 'Display'; StartTime = $started } -ErrorAction SilentlyContinue)
        # The client exits within a frame of the reset, often before the driver's event is in the log
        for ($i = 0; $i -lt 10 -and $gpuEvents.Count -eq 0 -and $proc -and $proc.HasExited -and $proc.ExitCode -eq 0xDEAD; $i++) {
            Start-Sleep -Milliseconds 500
            $gpuEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'nvlddmkm', 'amdkmdag', 'igfx', 'Display'; StartTime = $started } -ErrorAction SilentlyContinue)
        }
        $gpuText = ''
        if ($gpuEvents.Count -gt 0) {
            $failure += " The GPU driver logged $($gpuEvents.Count) event(s) during the run (" +
                (($gpuEvents | ForEach-Object { "$($_.ProviderName) $($_.Id) at $($_.TimeCreated.ToString('HH:mm:ss'))" }) -join ', ') +
                '); a driver reset takes the client''s graphics device with it.'
            $gpuText = "`r`nGPU driver events:`r`n" + (($gpuEvents | ForEach-Object { "$($_.TimeCreated.ToString('o')) $($_.ProviderName) $($_.Id) $($_.Message)" }) -join "`r`n")
        }
        $failLog = Join-Path $ResultsDir "fps-test-failed-$stamp.log"
        Hide-Secret (($lines -join "`r`n") + "`r`n" + $(if (Test-Path "$log.err") { Get-Content "$log.err" -Raw }) + $gpuText) |
            Set-Content -Encoding UTF8 $failLog
    }

    for ($i = 0; $i -lt 10 -and (Test-Path $work); $i++) {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
        if (Test-Path $work) { Start-Sleep -Milliseconds 500 }
    }

    if (-not $dataExisted) {
        Remove-Item -Recurse -Force (Join-Path $Bin 'Data') -ErrorAction SilentlyContinue
    }
}

if ($failure) {
    Write-Host ''
    Write-Host "FAILED: $failure" -ForegroundColor Red
    Write-Host "Client log (password masked): $failLog"
    if ($inScene) {
        Write-Host "The character is still in the large test scene. Log in with $Username and say [TestScene return to go back." -ForegroundColor Yellow
    }
    $lines | Select-Object -Last 15 | ForEach-Object { Write-Host "  $(Hide-Secret $_)" }
    $password = $null
    exit 1
}

$password = $null

# ---- results ----
$windowsByTag = @{}
$driver = ''
$dpi = ''

foreach ($line in $lines) {
    $at = $line.IndexOf('FRAMESTATS {')
    if ($at -ge 0) {
        $j = $line.Substring($at + 11) | ConvertFrom-Json
        # StopWindow can report a sliver of a window at the end of a spot; skip it
        if ($j.seconds -ge 2) {
            if (-not $windowsByTag.ContainsKey($j.tag)) { $windowsByTag[$j.tag] = New-Object System.Collections.Generic.List[object] }
            $windowsByTag[$j.tag].Add($j)
        }
    }
    elseif (-not $driver -and $line -match 'SDL_GPU device \S+ driver=(\S+) shaders=(\S+)') { $driver = "SDL_GPU $($Matches[1]) ($($Matches[2]))" }
    elseif (-not $dpi -and $line -match 'dpi scale ([\d.]+)') { $dpi = $Matches[1] }
}

function Avg($ws, [scriptblock]$f) { ($ws | ForEach-Object $f | Measure-Object -Average).Average }
function Sum($ws, [scriptblock]$f) { ($ws | ForEach-Object $f | Measure-Object -Sum).Sum }

function Get-Stats($tag) {
    $ws = $windowsByTag[$tag]
    if (-not $ws -or $ws.Count -eq 0) { return $null }
    $secs = Sum $ws { $_.seconds }
    [ordered]@{
        fps = (Sum $ws { $_.frames }) / $secs
        frame_avg = Avg $ws { $_.phases.frame[0] }; frame_p50 = Avg $ws { $_.phases.frame[1] }
        frame_p95 = Avg $ws { $_.phases.frame[2] }; frame_p99 = Avg $ws { $_.phases.frame[3] }
        update = Avg $ws { $_.phases.update[0] }; prepare = Avg $ws { $_.phases.world_prepare[0] }
        world_draw = Avg $ws { $_.phases.world_draw[0] }; draw = Avg $ws { $_.phases.draw[0] }
        present = Avg $ws { $_.phases.present[0] }; present_p50 = Avg $ws { $_.phases.present[1] }; present_p99 = Avg $ws { $_.phases.present[3] }
        alloc = (Avg $ws { $_.alloc_per_frame }) / 1024
        gc = '{0}/{1}/{2}' -f (Sum $ws { $_.gc0 }), (Sum $ws { $_.gc1 }), (Sum $ws { $_.gc2 })
        gc_pause = (Sum $ws { $_.gc_pause_ms }) / $secs
        draw_calls = Avg $ws { $_.counts.draw_calls[0] }; render_list = Avg $ws { $_.counts.render_list[0] }
        gpu_instances = Avg $ws { $_.counts.gpu_instances[0] }
        windows = $ws.Count
    }
}

function Change($l, $g, [bool]$higherIsBetter) {
    if ($null -eq $l -or $null -eq $g -or $l -eq 0) { return '' }
    $pct = ($g - $l) / $l * 100
    $better = if ($higherIsBetter) { $pct -gt 0 } else { $pct -lt 0 }
    $txt = (Fmt $pct '+0;-0;0') + '%'
    if ([math]::Abs($pct) -lt 3) { "$txt (same)" } elseif ($better) { "$txt (better)" } else { "$txt (worse)" }
}

$metrics = @(
    @{ Name = 'FPS (avg)'; Key = 'fps'; F = '0'; Up = $true },
    @{ Name = 'Frame avg (ms)'; Key = 'frame_avg'; F = '0.000' },
    @{ Name = 'Frame p50 (ms)'; Key = 'frame_p50'; F = '0.000' },
    @{ Name = 'Frame p95 (ms)'; Key = 'frame_p95'; F = '0.000' },
    @{ Name = 'Frame p99 (ms)'; Key = 'frame_p99'; F = '0.000' },
    @{ Name = 'Update (ms avg)'; Key = 'update'; F = '0.000' },
    @{ Name = 'World prepare (ms avg)'; Key = 'prepare'; F = '0.000' },
    @{ Name = 'World draw (ms avg)'; Key = 'world_draw'; F = '0.000' },
    @{ Name = 'Draw total (ms avg)'; Key = 'draw'; F = '0.000' },
    @{ Name = 'Present (ms avg)'; Key = 'present'; F = '0.000' },
    @{ Name = 'Present p50 / p99 (ms)'; Key = 'present_pp'; F = '' },
    @{ Name = 'Allocated per frame (KB)'; Key = 'alloc'; F = '0.0' },
    @{ Name = 'GC count gen0/1/2'; Key = 'gc'; F = '' },
    @{ Name = 'GC pause (ms per s)'; Key = 'gc_pause'; F = '0.00' },
    @{ Name = 'FNA draw calls'; Key = 'draw_calls'; F = '0' }
)

$out = New-Object System.Collections.Generic.List[string]
$out.Add("# ClassicUO FPS test: Legacy vs Gpu world renderer")
$out.Add('')
$out.Add("Run $($started.ToString('yyyy-MM-dd HH:mm')), $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min. NativeAOT build: $buildInfo.")
$out.Add("Startup renderer decision: $rendererLine (the plan then switches per spot with world:legacy|gpu).")
$out.Add('')
$out.Add('## Machine')
$out.Add('')
$out.Add("- CPU: $($cpu.Name.Trim()) ($($cpu.NumberOfCores) cores, $($cpu.NumberOfLogicalProcessors) threads), load at start $($cpu.LoadPercentage)%")
$out.Add("- RAM: $ramGb GB; OS: $($os.Caption) build $($os.BuildNumber); power plan: $power")
foreach ($g in $gpus) {
    $mode = if ($g.CurrentHorizontalResolution) { ", display $($g.CurrentHorizontalResolution)x$($g.CurrentVerticalResolution) @ $($g.CurrentRefreshRate) Hz" } else { '' }
    $out.Add("- GPU: $($g.Name), driver $($g.DriverVersion)$mode")
}
$out.Add("- Client: device $(if ($driver) { $driver } else { 'not SDL_GPU (Gpu falls back to Legacy)' }), DPI scale $dpi; FPS uncapped, VSync off, inactive throttling off, window on top")
$out.Add("- Server ${Server}:$Port, pack $Pack, $Windows windows of 5 s per spot")
$out.Add('')
$out.Add('## Summary')
$out.Add('')
$out.Add('| Spot | Client area | Legacy FPS | Gpu FPS | FPS change | Legacy frame p50 (ms) | Gpu frame p50 (ms) | Legacy Draw (ms) | Gpu Draw (ms) |')
$out.Add('|---|---|---|---|---|---|---|---|---|')

$details = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

foreach ($s in $spots) {
    $rl = $runs | Where-Object { $_.Tag -eq "$($s.Key)_legacy" }
    $rg = $runs | Where-Object { $_.Tag -eq "$($s.Key)_gpu" }
    $l = Get-Stats $rl.Tag
    $g = Get-Stats $rg.Tag
    $area = if ($rg.Size) { $rg.Size } else { $rl.Size }

    if (-not $l -or -not $g) {
        $warnings.Add("$($s.Label): missing measurements (legacy $([bool]$l), gpu $([bool]$g)).")
        continue
    }

    if ($s.Scene) {
        foreach ($r in @($rl, $rg)) {
            if ($null -ne $r.Mobiles -and $r.Mobiles -lt 1900) {
                $warnings.Add("$($s.Label), $($modeName[$r.Mode]): only $($r.Mobiles) mobiles and $($r.Items) items in range; the test scene is missing (ModernUO [TestScene create) or [TestScene go failed (needs GameMaster).")
            }
        }
    }

    if ($g.gpu_instances -le 0) {
        $warnings.Add("$($s.Label): the Gpu run drew no GPU world instances, so it ran on Legacy.")
    }

    $out.Add("| $($s.Label) | $area | $(Fmt $l.fps '0') | $(Fmt $g.fps '0') | $(Change $l.fps $g.fps $true) | $(Fmt $l.frame_p50 '0.000') | $(Fmt $g.frame_p50 '0.000') | $(Fmt $l.draw '0.000') | $(Fmt $g.draw '0.000') |")

    $details.Add("### $($s.Label) ($area)")
    $details.Add('')
    $details.Add('| Metric | Legacy | Gpu | Gpu vs Legacy |')
    $details.Add('|---|---|---|---|')

    foreach ($m in $metrics) {
        if ($m.Key -eq 'present_pp') {
            $details.Add("| $($m.Name) | $(Fmt $l.present_p50 '0.000') / $(Fmt $l.present_p99 '0.000') | $(Fmt $g.present_p50 '0.000') / $(Fmt $g.present_p99 '0.000') | |")
        }
        elseif ($m.F -eq '') {
            $details.Add("| $($m.Name) | $($l[$m.Key]) | $($g[$m.Key]) | |")
        }
        else {
            $chg = if ($m.Key -eq 'draw_calls') { '' } else { Change $l[$m.Key] $g[$m.Key] ([bool]$m.Up) }
            $details.Add("| $($m.Name) | $(Fmt $l[$m.Key] $m.F) | $(Fmt $g[$m.Key] $m.F) | $chg |")
        }
    }

    $details.Add('')
}

$out.Add('')
if ($warnings.Count -gt 0) {
    $out.Add('## Warnings')
    $out.Add('')
    $warnings | ForEach-Object { $out.Add("- $_") }
    $out.Add('')
}
$out.Add('## Per spot')
$out.Add('')
$out.AddRange($details)
$out.Add('Notes: frame and phase times are averages of the 5-s windows (p50/p95/p99 are per-window percentiles, averaged). ' +
    'World draw in Gpu mode includes WorldRenderer.Prepare/Render plus the FNA overlay. Present is FNA3D SwapBuffers; ' +
    'above about 1,000 fps it is bimodal (back-pressure), so compare CPU phases and frame p50 as well as FPS. ' +
    'Allocations are for all threads; GC counts are totals over the spot.')

New-Item -ItemType Directory -Force $ResultsDir | Out-Null
$resultsPath = Join-Path $ResultsDir "results-$stamp.md"
$out | Set-Content -Encoding UTF8 $resultsPath

Write-Host ''
$out | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Results written to $resultsPath" -ForegroundColor Green
