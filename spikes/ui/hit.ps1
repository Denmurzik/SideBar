# Usage: hit.ps1 -Dir <S10Spring publish dir> -AppArgs "--mode A --ct ex"   -> prints which process owns the window under the cursor point in each state
param([string]$Dir, [string]$AppArgs = "--mode A --ct ex")
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class H {
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(P p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  public static uint Owner(int x, int y) { var h = WindowFromPoint(new P { X = x, Y = y }); uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
}
"@
Add-Type -AssemblyName System.Windows.Forms
$w = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
$p = Start-Process (Join-Path $Dir 'S10Spring.exe') -ArgumentList $AppArgs -PassThru
Start-Sleep 5
function Cmd($n) { [System.Threading.EventWaitHandle]::OpenExisting("S10_$n").Set() | Out-Null }
function Check($tag) {
  $mid = [int]([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height / 2)
  foreach ($dx in 4, 100, 330) {
    $o = [H]::Owner($w - $dx, $mid)
    '{0,-8} x=right-{1,-4} owner={2}' -f $tag, $dx, $(if ($o -eq $p.Id) { 'PANEL' } else { 'other' })
  }
}
Check 'hidden'
Cmd 'peek'; Start-Sleep 1.5; Check 'peek'
Cmd 'open'; Start-Sleep 1.5; Check 'open'
Cmd 'close'; Start-Sleep 1.5; Check 'hidden'
Cmd 'exit'; Start-Sleep 1; if (!$p.HasExited) { $p.Kill() }
