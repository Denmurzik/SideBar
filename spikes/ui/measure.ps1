param([string]$Dir, [string]$Label, [int]$Cycles = 3)
$exe = Join-Path $Dir 'S7Panel.exe'
$log = Join-Path $Dir 's7.log'
Remove-Item $log -ErrorAction SilentlyContinue
function Sample($p, $tag) {
    $p.Refresh()
    $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)"
    '{0,-28} private={1,7:N1} MB  ws={2,7:N1} MB  privWS={3,7:N1} MB' -f $tag, ($p.PrivateMemorySize64/1MB), ($p.WorkingSet64/1MB), ($perf.WorkingSetPrivate/1MB)
}
function Cmd($n) { [System.Threading.EventWaitHandle]::OpenExisting("S7Panel_$n").Set() | Out-Null }
$p = Start-Process $exe -PassThru
Start-Sleep 6
"== $Label  size=$([math]::Round((Get-ChildItem $Dir -Recurse -File | Measure-Object Length -Sum).Sum/1MB,1)) MB"
Sample $p 'after start (6s)'
for ($i = 1; $i -le $Cycles; $i++) {
    Cmd 'hide'; Start-Sleep 2
    Sample $p "cycle$i hidden +2s"
    Start-Sleep 8;  Sample $p "cycle$i hidden +10s"
    if ($i -eq 1) { Start-Sleep 50; Sample $p "cycle$i hidden +60s" }
    Cmd 'show'; Start-Sleep 3
    Sample $p "cycle$i shown +3s"
}
Cmd 'exit'
Start-Sleep 1
Get-Content $log
if (!$p.HasExited) { $p.Kill() }
