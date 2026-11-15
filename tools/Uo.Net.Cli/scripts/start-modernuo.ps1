# Starts the owner's already-built ModernUO (no rebuild, no config changes) in its own minimized
# console so it can be stopped cleanly with Ctrl+C (stop-modernuo.ps1). Console output goes to $Log.
param(
    [string]$Distribution = 'C:\Repositories\ModernUO\Distribution',
    [string]$Log = (Join-Path $env:TEMP 'modernuo-poc.log'),
    [int]$Port = 2593,
    [int]$TimeoutSeconds = 180
)

if (Get-Process -Name ModernUO -ErrorAction SilentlyContinue) {
    Write-Host 'ModernUO is already running.'
    exit 0
}

Remove-Item $Log -ErrorAction SilentlyContinue
# cmd redirects stdout only; stdin stays the new console, so ModernUO is not "headless" and Ctrl+C works.
$inner = "`"$Distribution\ModernUO.exe`" > `"$Log`" 2>&1"
Start-Process -FilePath cmd.exe -ArgumentList '/c', "`"$inner`"" -WorkingDirectory $Distribution -WindowStyle Minimized | Out-Null

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $listening = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
    if ($listening -and (Get-Process -Name ModernUO -ErrorAction SilentlyContinue)) {
        Write-Host "ModernUO listening on port $Port (log: $Log)"
        exit 0
    }
}
Write-Host "ModernUO did not start listening within $TimeoutSeconds s; see $Log"
exit 1
