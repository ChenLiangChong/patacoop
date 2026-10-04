# restart.ps1 (old standalone-server setup): restart the standalone relay and two game copies
param([string]$Exe = 'E:\SteamLibrary\steamapps\common\PATAPON12_REPLAY\PATAPON12_REPLAY.exe')
Get-Process PATAPON12_REPLAY -EA SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Start-Sleep -Seconds 6
Get-Process PATAPON12_REPLAY -EA SilentlyContinue | Stop-Process -Force
if (-not (Get-Process PataCoop.Server -EA SilentlyContinue)) {
  $server = Join-Path $env:USERPROFILE 'PataCoop\server'
  Start-Process (Join-Path $server 'PataCoop.Server.exe') -WorkingDirectory $server -WindowStyle Minimized
  Start-Sleep -Seconds 2
}
"server: " + ((Get-Process PataCoop.Server -EA SilentlyContinue | ForEach-Object Id) -join ',')
$exe = $Exe
Start-Process $exe -WorkingDirectory (Split-Path $exe)
Start-Sleep -Seconds 8
Start-Process $exe -WorkingDirectory (Split-Path $exe)
Start-Sleep -Seconds 40
"games: " + ((Get-Process PATAPON12_REPLAY -EA SilentlyContinue | ForEach-Object Id) -join ',')
