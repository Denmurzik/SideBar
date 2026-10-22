param([string]$Dir, [string]$AppArgs, [string]$Out, [string]$Cmd = "open")
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$p = Start-Process (Join-Path $Dir 'S10Spring.exe') -ArgumentList $AppArgs -PassThru
Start-Sleep 5
[System.Threading.EventWaitHandle]::OpenExisting("S10_$Cmd").Set() | Out-Null
Start-Sleep 2
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen(0,0,0,0,$bmp.Size)
$bmp.Save($Out)
[System.Threading.EventWaitHandle]::OpenExisting("S10_exit").Set() | Out-Null
Start-Sleep 1; if (!$p.HasExited) { $p.Kill() }
Get-Content (Join-Path $Dir 's10.log')
