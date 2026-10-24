# Stops ModernUO the way its console does: Ctrl+Break -> Console.CancelKeyPress -> Core.Kill() ->
# HandleClosed(), which waits for any in-flight world save to finish. Never force-kills.
# Ctrl+Break rather than Ctrl+C: the "ignore Ctrl+C" console flag is inherited from whatever launched
# the server (agent shells set it), and Ctrl+Break cannot be ignored that way.
param([int]$ChildOf = 0, [int]$TimeoutSeconds = 120)

$code = @'
using System;
using System.Runtime.InteropServices;
public static class ConsoleCtl {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint group);
}
'@

if ($ChildOf -gt 0) {
    # Runs in a throwaway process: detach from our console, join ModernUO's, send Ctrl+Break to it.
    # This helper shares the console, so the event ends it too; that is expected.
    Add-Type -TypeDefinition $code
    [ConsoleCtl]::FreeConsole() | Out-Null
    if (-not [ConsoleCtl]::AttachConsole([uint32]$ChildOf)) { exit 3 }
    [ConsoleCtl]::SetConsoleCtrlHandler([IntPtr]::Zero, $true) | Out-Null
    [ConsoleCtl]::GenerateConsoleCtrlEvent(1, 0) | Out-Null
    Start-Sleep -Milliseconds 500
    [ConsoleCtl]::FreeConsole() | Out-Null
    exit 0
}

$p = Get-Process -Name ModernUO -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Host 'ModernUO is not running.'; exit 0 }

Start-Process -FilePath powershell.exe -WindowStyle Hidden -Wait `
    -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-ChildOf', $p.Id

if ($p.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host "ModernUO exited."
    exit 0
}
Write-Host "ModernUO still running after $TimeoutSeconds s; not force-killing. Press Ctrl+C in its window or type 'shutdown'."
exit 1
