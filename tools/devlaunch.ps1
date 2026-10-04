param([int]$Count = 2, [string]$Exe = 'E:\SteamLibrary\steamapps\common\PATAPON12_REPLAY\PATAPON12_REPLAY.exe')
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags); }
"@
Get-Process PATAPON12_REPLAY -EA SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Start-Sleep -Seconds 5
Get-Process PATAPON12_REPLAY -EA SilentlyContinue | Stop-Process -Force
# the co-op relay now runs inside the hosting game (F7); the standalone server would hold its port
Get-Process PataCoop.Server -EA SilentlyContinue | Stop-Process -Force
$exe = $Exe
$procs = @()
for ($i = 0; $i -lt $Count; $i++) {
  $procs += Start-Process $exe -WorkingDirectory (Split-Path $exe) -ArgumentList '-screen-fullscreen','0','-screen-width','960','-screen-height','540' -PassThru
  # the copies share one BepInEx folder: start the next only once this one has loaded its plugins
  # (two copies reading and writing the same .cfg at once makes BepInEx skip the plugin)
  $deadline = (Get-Date).AddSeconds(90)
  while ((Get-Date) -lt $deadline) {
    try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 "http://127.0.0.1:$(9100 + $i)/ping" | Out-Null; break } catch { Start-Sleep -Seconds 2 }
  }
  Start-Sleep -Seconds 3
}
Start-Sleep -Seconds 20
# stack the dev windows on the right-hand monitor (x = 1920..3840)
for ($i = 0; $i -lt $procs.Count; $i++) {
  $p = Get-Process -Id $procs[$i].Id -EA SilentlyContinue
  if ($p -and $p.MainWindowHandle -ne 0) {
    [W]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 1920 + 960 * [math]::Floor($i / 2), 540 * ($i % 2), 0, 0, 0x0001 -bor 0x0004) | Out-Null
  }
  "game {0}: pid {1}" -f $i, $procs[$i].Id
}
