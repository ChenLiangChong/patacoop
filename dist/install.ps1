# PataCoop installer: puts the co-op mod into PATAPON 1+2 REPLAY (Steam), sets your name, opens the
# firewall for hosting and gets Radmin VPN ready. Run through "install.bat" (double-click).
# Safe to run again. A package from a friend may carry preset.ini (their Radmin network and IP).
#
# For scripts and agents: powershell -ExecutionPolicy Bypass -File install.ps1 -Unattended
#   [-Name <name>] [-GameDir <dir>] [-RadminNetwork <name>] [-RadminPassword <pw>] [-HostAddress <ip>]
# -Unattended answers every question with its default (Steam name, install Radmin VPN, never
# overwrite another mod loader) and does not wait for Enter. Administrator prompts still appear.
param(
  [string]$GameDir = "",
  [string]$Name = "",
  [string]$HostAddress = "",
  [string]$RadminNetwork = "",
  [string]$RadminPassword = "",
  [switch]$Unattended
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bepinexUrl = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785%2B6abdba4.zip"
$bepinexSha256 = "2A7CBF74D26ABE4765C3E662DB1721B923BAC39849EBFEF2CA5DC7DE7E2D9B7F"
$radminSigner = "CN=Famatech Corp., O=Famatech Corp., L=Road Town, C=VG"
$radminSite = "https://www.radmin-vpn.com/"
$radminFallback = "https://download.radmin-vpn.com/download/files/Radmin_VPN_2.1.4951.1.exe"
$ruleName = "PataCoop (UDP 27015)"

function Find-Game {
  $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
  $libs = @()
  if ($steam) {
    $libs += $steam
    $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
    if (Test-Path -LiteralPath $vdf) { $libs += (Select-String -LiteralPath $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }) }
  }
  foreach ($lib in $libs | Select-Object -Unique) {
    $dir = Join-Path $lib "steamapps\common\PATAPON12_REPLAY"
    if (Test-Path -LiteralPath (Join-Path $dir "PATAPON12_REPLAY.exe")) { return $dir }
  }
  return $null
}

# A question for the player; unattended runs take the default.
function Ask([string]$prompt, [string]$default = "") {
  if ($Unattended) { Write-Host "$prompt -> $default"; return $default }
  return Read-Host $prompt
}

# The Steam display name, as the default player name.
function Steam-Name {
  $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
  if (-not $steam) { return $null }
  $vdf = Join-Path $steam "config\loginusers.vdf"
  if (-not (Test-Path -LiteralPath $vdf)) { return $null }
  $text = Get-Content -LiteralPath $vdf -Raw -Encoding UTF8
  $m = [regex]::Matches($text, '"PersonaName"\s+"([^"]+)"[^}]*?"MostRecent"\s+"1"')
  if ($m.Count -gt 0) { return $m[0].Groups[1].Value }
  $m = [regex]::Matches($text, '"PersonaName"\s+"([^"]+)"')
  if ($m.Count -gt 0) { return $m[0].Groups[1].Value }
  return $null
}

# A value for a single-quoted string inside a generated script.
function Quote([string]$text) { return "'" + $text.Replace("'", "''") + "'" }

# Run a script as administrator (one UAC prompt); $true when it ran.
function Run-Elevated([string]$script) {
  $file = Join-Path $env:TEMP ("patacoop-" + [System.IO.Path]::GetRandomFileName() + ".ps1")
  Set-Content -LiteralPath $file -Value $script -Encoding UTF8
  try { Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$file`""; return $true }
  catch { return $false }
  finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
}

# Set "key = value" in a section of a BepInEx .cfg, keeping everything else.
function Set-CfgValue([string]$path, [string]$section, [string]$key, [string]$value) {
  $lines = @()
  if (Test-Path -LiteralPath $path) { $lines = @(Get-Content -LiteralPath $path -Encoding UTF8) }
  $out = New-Object System.Collections.Generic.List[string]
  $inSection = $false; $done = $false
  foreach ($line in $lines) {
    if ($line -match '^\s*\[(.+)\]\s*$') {
      if ($inSection -and -not $done) { $out.Add("$key = $value"); $out.Add(""); $done = $true }
      $inSection = $Matches[1] -eq $section
    } elseif ($inSection -and $line -match "^\s*$([regex]::Escape($key))\s*=") {
      $out.Add("$key = $value"); $done = $true; continue
    }
    $out.Add($line)
  }
  if (-not $done) {
    if (-not $inSection) { $out.Add(""); $out.Add("[$section]") }
    $out.Add("$key = $value")
  }
  [System.IO.File]::WriteAllLines($path, $out, (New-Object System.Text.UTF8Encoding($false)))
}

# Listing firewall rules through the NetSecurity cmdlets needs administrator rights; netsh does not.
function Has-FirewallRule {
  & netsh advfirewall firewall show rule name="$ruleName" | Out-Null
  return $LASTEXITCODE -eq 0
}

function Find-Radmin {
  Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*", "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like "Radmin VPN*" } | Select-Object -First 1
}

# Download the official Radmin VPN installer (signature checked) and run it; $true when Radmin VPN is installed afterwards.
function Install-Radmin {
  $url = $radminFallback
  try {
    $page = (Invoke-WebRequest -Uri $radminSite -UseBasicParsing -TimeoutSec 20).Content
    $m = [regex]::Match($page, 'https://download\.radmin-vpn\.com/download/files/Radmin_VPN_[0-9.]+\.exe')
    if ($m.Success) { $url = $m.Value }
  } catch { }
  $exe = Join-Path $env:TEMP ([System.IO.Path]::GetFileName($url))
  Write-Host "下載 Radmin VPN：$url"
  Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing
  $sig = Get-AuthenticodeSignature -LiteralPath $exe
  if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -ne $radminSigner) {
    Remove-Item -LiteralPath $exe -ErrorAction SilentlyContinue
    Write-Host "下載的檔案簽章不對，為了安全不執行。請到 $radminSite 自己下載安裝。" -ForegroundColor Yellow
    return $false
  }
  Write-Host "安裝 Radmin VPN（會跳出系統管理員確認，請按「是」）..."
  $q = Quote $exe
  $ok = Run-Elevated @"
`$s = Get-AuthenticodeSignature -LiteralPath $q
if (`$s.Status -ne 'Valid' -or `$s.SignerCertificate.Subject -ne $(Quote $radminSigner)) { exit 1 }
Start-Process -FilePath $q -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait
"@
  Remove-Item -LiteralPath $exe -ErrorAction SilentlyContinue
  return $ok -and (Find-Radmin)
}

# The Radmin VPN networks this PC is in, read from the Radmin VPN window (empty when it cannot tell).
function Radmin-Networks($radmin) {
  $gui = Join-Path $radmin.InstallLocation "RvRvpnGui.exe"
  if (-not (Test-Path -LiteralPath $gui)) { return @() }
  Start-Process $gui
  Start-Sleep -Seconds 4
  try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $A = [System.Windows.Automation.AutomationElement]
    $proc = Get-Process RvRvpnGui -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { return @() }
    $byProc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$proc.Id)
    $names = @()
    foreach ($w in $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $byProc)) {
      foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.AutomationId -like "*NetworkWidget.NetworkName") { $names += $e.Current.Name }
      }
    }
    return $names
  } catch { return @() }
}

# ---------------------------------------------------------------------------------------------------

Write-Host "=== PataCoop 安裝 ===" -ForegroundColor Cyan
$preset = @{}
$presetFile = Join-Path $here "preset.ini"
if (Test-Path -LiteralPath $presetFile) {
  foreach ($line in Get-Content -LiteralPath $presetFile -Encoding UTF8) {
    if ($line -match '^\s*([A-Za-z]+)\s*=\s*(.*?)\s*$') { $preset[$Matches[1]] = $Matches[2] }
  }
}
if ($HostAddress) { $preset["HostAddress"] = $HostAddress }
if ($RadminNetwork) { $preset["RadminNetwork"] = $RadminNetwork }
if ($RadminPassword) { $preset["RadminPassword"] = $RadminPassword }

if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir -or -not (Test-Path -LiteralPath (Join-Path $GameDir "PATAPON12_REPLAY.exe"))) {
  if ($Unattended) { throw "找不到遊戲資料夾：請用 -GameDir 指定 PATAPON12_REPLAY 資料夾。" }
  $GameDir = Read-Host "找不到遊戲資料夾，請貼上 PATAPON12_REPLAY 資料夾的路徑"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir "PATAPON12_REPLAY.exe"))) { throw "這個資料夾裡沒有 PATAPON12_REPLAY.exe：$GameDir" }
Write-Host "遊戲資料夾：$GameDir"
if (Get-Process PATAPON12_REPLAY -ErrorAction SilentlyContinue) { throw "請先關閉遊戲再安裝。" }

# 1. BepInEx (the mod loader), the exact build PataCoop is made for
$core = Join-Path $GameDir "BepInEx\core\BepInEx.Core.dll"
if (Test-Path -LiteralPath $core) {
  $have = (Get-Item -LiteralPath $core).VersionInfo.ProductVersion
  if ($have -like "*be.785*") { Write-Host "已經有 BepInEx。" }
  else { Write-Host "這個遊戲已經裝了另一版 BepInEx（$have）。PataCoop 是用 be.785 做的，可能無法運作；不會動到你現有的 BepInEx。" -ForegroundColor Yellow }
} else {
  if (Test-Path -LiteralPath (Join-Path $GameDir "winhttp.dll")) {
    Write-Host "遊戲資料夾裡已經有 winhttp.dll（可能是別的模組載入器）。安裝 BepInEx 會覆蓋它。" -ForegroundColor Yellow
    if ((Ask "要繼續嗎？(y/N)" "n") -notmatch '^[Yy]') { throw "已取消：沒有覆蓋現有的 winhttp.dll。" }
  }
  Write-Host "下載模組載入器 BepInEx..."
  $zip = Join-Path $env:TEMP ("bepinex-be785-" + [System.IO.Path]::GetRandomFileName() + ".zip")
  Invoke-WebRequest -Uri $bepinexUrl -OutFile $zip -UseBasicParsing
  if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $bepinexSha256) {
    Remove-Item -LiteralPath $zip -ErrorAction SilentlyContinue
    throw "下載的 BepInEx 檔案跟預期的不一樣（雜湊值不符），為了安全停止安裝。"
  }
  Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force
  Remove-Item -LiteralPath $zip
  Write-Host "BepInEx 安裝完成（第一次開遊戲會比較久，正常現象）。"
}

# 2. the mod itself
$plugins = Join-Path $GameDir "BepInEx\plugins"
$target = Join-Path $plugins "PataCoop"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem -LiteralPath (Join-Path $here "plugin") -File | Copy-Item -Destination $target -Force
Write-Host "PataCoop 已放到：$target"
# PataNet patches the same game code; the two must not run together.
foreach ($f in @("PataNet.dll", "LiteNetLib.dll")) {
  $p = Join-Path $plugins $f
  if (Test-Path -LiteralPath $p) {
    $off = "$f.disabled-by-patacoop"; $n = 2
    while (Test-Path -LiteralPath (Join-Path $plugins $off)) { $off = "$f.disabled-by-patacoop-$n"; $n++ }
    Rename-Item -LiteralPath $p $off; Write-Host "已停用 $f（跟 PataCoop 衝突，要用回 PataNet 時把副檔名改回 .dll）" -ForegroundColor Yellow
  }
}

# 3. settings: your name, and the host your friend gave (the panel finds rooms by itself; this one is asked directly too)
$cfgDir = Join-Path $GameDir "BepInEx\config"
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfg = Join-Path $cfgDir "com.patacoop.coop.cfg"
$defaultName = Steam-Name
if (-not $defaultName) { $defaultName = $env:USERNAME }
$name = $Name
if (-not $name) { $name = Ask "你的名字（顯示給隊友看；直接按 Enter 用「$defaultName」，之後在遊戲裡也能改）" $defaultName }
if (-not $name) { $name = $defaultName }
Set-CfgValue $cfg "Coop" "PlayerName" $name
if ($preset["HostAddress"]) { Set-CfgValue $cfg "Coop" "HostAddress" $preset["HostAddress"] }
Write-Host "設定已寫入：$cfg"

# 4. firewall: whoever opens a room must let friends reach UDP 27015 (Radmin VPN counts as a public network)
if (Has-FirewallRule) { Write-Host "防火牆規則已存在。" }
else {
  Write-Host ""
  Write-Host "要讓朋友連進你開的房間，需要開防火牆（只開給這個遊戲的 UDP 27015）。" -ForegroundColor Cyan
  Write-Host "接下來會跳出系統管理員確認，請按「是」。（只加入別人的房間的話，按「否」也可以）"
  $exe = Join-Path $GameDir "PATAPON12_REPLAY.exe"
  # open to the LAN and to Radmin VPN's 26.x.x.x addresses only, not to public Wi-Fi strangers
  $ok = Run-Elevated "New-NetFirewallRule -DisplayName $(Quote $ruleName) -Direction Inbound -Protocol UDP -LocalPort 27015 -Program $(Quote $exe) -RemoteAddress LocalSubnet,26.0.0.0/8 -Action Allow -Profile Any | Out-Null"
  if ($ok -and (Has-FirewallRule)) { Write-Host "防火牆已開好（UDP 27015）。" -ForegroundColor Green }
  else { Write-Host "防火牆沒有開：你還是可以加入別人的房間；要自己開房時再執行一次安裝。" -ForegroundColor Yellow }
}

# 5. Radmin VPN: installed, and in the friends' network
$radmin = Find-Radmin
if (-not $radmin) {
  Write-Host ""
  Write-Host "還沒有安裝 Radmin VPN（免費的虛擬區網，用來跟朋友連線）。" -ForegroundColor Yellow
  if ((Ask "要幫你下載並安裝官方的 Radmin VPN 嗎？(Y/n)" "y") -notmatch '^[Nn]') {
    try { if (Install-Radmin) { Write-Host "Radmin VPN 安裝完成。" -ForegroundColor Green } } catch { Write-Host "自動安裝失敗：$($_.Exception.Message)" -ForegroundColor Yellow }
  }
  $radmin = Find-Radmin
  if (-not $radmin) {
    Write-Host "請到 $radminSite 下載安裝 Radmin VPN（一路按「下一步」即可）。"
    if (-not $Unattended) {
      Start-Process $radminSite
      Read-Host "裝好之後按 Enter 繼續"
      $radmin = Find-Radmin
    }
  }
}
$network = $preset["RadminNetwork"]
if ($radmin -and $network) {
  $joined = Radmin-Networks $radmin
  if ($joined -contains $network) {
    Write-Host "Radmin VPN 已經在「$network」網路裡。" -ForegroundColor Green
  } else {
    Write-Host ""
    Write-Host "=== 加入朋友的 Radmin VPN 網路 ===" -ForegroundColor Cyan
    Write-Host "在 Radmin VPN 視窗：選單「網路」→「加入網路」，然後："
    Write-Host "  網路名稱：$network" -ForegroundColor Green
    if ($preset["RadminPassword"]) {
      Set-Clipboard -Value $preset["RadminPassword"]
      Write-Host "  密碼：已經複製好了，在密碼欄按 Ctrl+V 貼上" -ForegroundColor Green
    } else { Write-Host "  密碼：問開網路的朋友" }
    Write-Host "  再按「加入」。"
    if (-not $Unattended) { Read-Host "加入好了按 Enter" }
  }
}

Write-Host ""
Write-Host "安裝完成！怎麼玩：" -ForegroundColor Green
Write-Host "  1. 確認 Radmin VPN 開著（顯示「線上」）。"
Write-Host "  2. 開遊戲 → Patapon 2 → 進到營地。右上角有 PataCoop 面板（動一下滑鼠就會出現游標）。"
Write-Host "  3. 開房的人按「開房間」；其他人在「同一個網路上的房間」按「加入」。"
Write-Host "     找不到房間時，在「用 IP 加入」填開房者的 Radmin IP（26 開頭）再按「加入」。"
if (-not $Unattended) { Read-Host "按 Enter 結束" }
