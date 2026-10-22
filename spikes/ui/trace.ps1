# Usage: trace.ps1 -Dir <publish dir> -AppArgs "--mode A --solid" -Schedule "300:open,1200:close" -Total 2500 -Out trace.csv
param([string]$Dir, [string]$AppArgs = "--mode A --solid", [string]$Schedule = "300:open,1200:close", [int]$Total = 2500, [string]$Out = "trace.csv", [switch]$Keep)
Add-Type -ReferencedAssemblies System.Drawing,System.Windows.Forms @"
using System; using System.Collections.Generic; using System.Diagnostics; using System.Drawing; using System.Drawing.Imaging; using System.Threading; using System.Runtime.InteropServices;
public static class Tr {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public static List<string> Run(string sched, int total, int y) {
    SetProcessDPIAware();
    var ev = new List<KeyValuePair<int,string>>();
    foreach (var s in sched.Split(',')) { var p = s.Split(':'); ev.Add(new KeyValuePair<int,string>(int.Parse(p[0]), p[1])); }
    int w = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
    var bmp = new Bitmap(w, 1, PixelFormat.Format32bppArgb);
    var g = Graphics.FromImage(bmp);
    var res = new List<string>(); var sw = Stopwatch.StartNew(); int next = 0;
    while (sw.ElapsedMilliseconds < total) {
      while (next < ev.Count && sw.ElapsedMilliseconds >= ev[next].Key) { EventWaitHandle.OpenExisting("S10_" + ev[next].Value).Set(); res.Add(sw.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ",cmd," + ev[next].Value); next++; }
      double t = sw.Elapsed.TotalMilliseconds;
      g.CopyFromScreen(0, y, 0, 0, new Size(w, 1));
      var d = bmp.LockBits(new Rectangle(0,0,w,1), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      int left = -1; byte[] row = new byte[w*4]; Marshal.Copy(d.Scan0, row, 0, w*4); bmp.UnlockBits(d);
      for (int x = 0; x < w; x++) { int i = x*4; if (row[i+2] > 200 && row[i+1] < 70 && row[i] > 200) { left = x; break; } }
      res.Add(t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ",x," + left + "," + row[(w-50)*4+2] + "-" + row[(w-50)*4+1] + "-" + row[(w-50)*4]);
    }
    return res;
  }
}
"@ -ErrorAction Stop
Add-Type -AssemblyName System.Windows.Forms
$exe = Join-Path $Dir 'S10Spring.exe'
Remove-Item (Join-Path $Dir 's10.log') -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList $AppArgs -PassThru
Start-Sleep 5
$h = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
$rows = [Tr]::Run($Schedule, $Total, [int]($h/2))
$rows | Set-Content $Out
[System.Threading.EventWaitHandle]::OpenExisting("S10_exit").Set() | Out-Null
Start-Sleep 1; if (!$p.HasExited) { $p.Kill() }
Get-Content (Join-Path $Dir 's10.log')
