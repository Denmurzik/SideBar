param([string]$Dir, [string]$AppArgs, [string]$Label)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
function Sample($p, $tag) {
    $p.Refresh()
    $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)"
    '{0,-22} private={1,6:N1} ws={2,6:N1} privWS={3,6:N1} MB' -f $tag, ($p.PrivateMemorySize64/1MB), ($p.WorkingSet64/1MB), ($perf.WorkingSetPrivate/1MB)
}
function Cmd($n) { [System.Threading.EventWaitHandle]::OpenExisting("S10_$n").Set() | Out-Null }
function Shot($f) { $b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; $bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height; [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen(0,0,0,0,$bmp.Size); $bmp.Save($f) }
$p = Start-Process (Join-Path $Dir 'S10Spring.exe') -ArgumentList "$AppArgs --trim" -PassThru
Start-Sleep 6
"== $Label ($AppArgs)"
Sample $p 'start (hidden,trimmed)'
Cmd open; Start-Sleep 2; Sample $p 'shown +2s'; Shot "$Dir\open1.png"
Cmd close; Start-Sleep 2; Sample $p 'hidden +2s'
Start-Sleep 8; Sample $p 'hidden +10s'
Start-Sleep 50; Sample $p 'hidden +60s'
Cmd open; Start-Sleep 2; Sample $p 'reshown +2s'; Shot "$Dir\open2.png"
Cmd exit; Start-Sleep 1; if(!$p.HasExited){$p.Kill()}
