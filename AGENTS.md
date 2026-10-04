# PataCoop — guide for AI agents (Claude Code, Codex, Cursor, …)

PataCoop is a BepInEx mod for **PATAPON 1+2 REPLAY** (Steam, Windows). It lets 2–4 friends play the
**Patapon 2 story missions** together over **Radmin VPN** (or a LAN). Every player brings their own
army from their own save and drums for it. One player hosts: the relay server runs inside their
game, and the host's game owns the shared state (enemies, hit points, deaths, gimmicks, weather,
mission end).

The players speak **Traditional Chinese**. Talk to them in Traditional Chinese unless they write
in another language. Code, comments and logs are in English. Player-facing text in the mod goes
through `Text.T(en, zh)`.

### Agent environment (Codex, Claude Code, others)

- **Which file:** Codex and most agents read this `AGENTS.md`; Claude Code reads `CLAUDE.md`,
  which imports it. Keep this file under 32 KiB, Codex's default read limit.
- **What setup needs:** network access (BepInEx, Radmin VPN and the release zip are downloaded),
  Windows PowerShell, and **UAC prompts that only the player can accept**. Codex's default
  sandbox blocks the network and Windows programs: ask the player to approve the command or
  allow full access for the install step. Otherwise, have them double-click `install.bat`.
- **Agent running in WSL:** call Windows PowerShell:
  `powershell.exe -ExecutionPolicy Bypass -File "$(wslpath -w install.ps1)" -Unattended -Name "<name>"`.
- **Download without `gh`** (PowerShell; the repository is public):
  ```powershell
  $r = Invoke-RestMethod https://api.github.com/repos/ChenLiangChong/patacoop/releases/latest
  $a = $r.assets | Where-Object { $_.name -like 'PataCoop-*.zip' -and $_.name -notlike '*friends*' } | Select-Object -First 1
  Invoke-WebRequest $a.browser_download_url -OutFile $a.name; Expand-Archive $a.name -DestinationPath . -Force
  ```
- **Development harness (section B):** it launches game copies and talks to them over
  `http://127.0.0.1:9100+`, so it also needs full access outside a sandbox.

There are two kinds of tasks; pick the section that matches.

---

## A. Setting up a player's PC (friends: start here)

The player wants to join the group's co-op games. You do not need to build anything.

1. **Prerequisites.** Windows with Steam, and PATAPON 1+2 REPLAY installed. A Steam Family
   shared copy works. The game must be **closed** before installing.
2. **Get the release zip** `PataCoop-<version>.zip`. Take it from this repository's GitHub
   Releases (`gh release download --repo ChenLiangChong/patacoop --pattern "PataCoop-*.zip"`, or https://github.com/ChenLiangChong/patacoop/releases/latest) or from the
   friend who hosts. Never use a zip from an unknown source. Everyone in a game must run the
   **same PataCoop version**: different versions cannot connect, or cannot sortie together.
3. **Install.** Unzip it and run the installer from PowerShell:
   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1 -Unattended -Name "<player name>" -RadminNetwork "<group's Radmin network>"
   ```
   Optional: `-HostAddress <host's 26.x.x.x>`, `-RadminPassword <pw>` (copied to the clipboard,
   never written to disk) and `-GameDir <game folder>` (if Steam's libraries are not found).
   The installer does the following:
   - finds the game;
   - installs BepInEx be.785 (hash-checked), and refuses to overwrite another mod loader;
   - copies the plugin and writes `BepInEx\config\com.patacoop.coop.cfg`;
   - adds the firewall rule "PataCoop (UDP 27015)" (only the LAN and 26.0.0.0/8);
   - installs Radmin VPN if it is missing (official download, signature checked);
   - checks whether this PC is already in the group's Radmin network.

   Windows shows **UAC prompts**, and the player must click "是/Yes". Without `-Name`, the
   name defaults to the Steam display name.
4. **Radmin VPN network.** Radmin has no command line for joining. If the installer says the PC
   is not in the network, walk the player through it in the Radmin window: 網路 → 加入網路 →
   network name and password → 加入. Ask the player (or the host) for the password; it is never
   in this repository. To check membership afterwards, read the Radmin window through UI
   Automation: elements whose AutomationId ends in `NetworkWidget.NetworkName` (see
   `Radmin-Networks` in `dist/install.ps1`).
5. **First start.** Steam → PATAPON 1+2 REPLAY. The first start with BepInEx takes a few minutes
   (it generates `BepInEx\interop`); that is normal. Then pick Patapon 2 and load a save; the
   camp shows the **PataCoop panel** in the top-right corner. Moving the mouse shows the cursor.
   - Host: click **開房間**.
   - Everyone else: their room appears under **同一個網路上的房間** → click **加入**. If it does
     not appear, type the host's Radmin IP (26.x.x.x) under **用 IP 加入**.
6. **Playing:**
   - The host picks a mission on the world map and goes to the headquarters (HQ).
   - The others pick the same mission and sortie from the HQ. Sortie means "ready"; the host
     leaves when everyone is ready.
   - Hero-world (啪啪門, egg/carnival) missions are not supported online.
   - Missions that teach a new drum should be done solo first.

**Troubleshooting**

| Symptom | Check |
|---|---|
| No panel in the camp | Look in `BepInEx\LogOutput.log` for `PataCoop 0.x.y loaded`. Missing → the plugin is not in `BepInEx\plugins\PataCoop`, or BepInEx is not installed. Antivirus sometimes quarantines `winhttp.dll`. |
| Room never shows up | Both PCs online in the same Radmin network (Radmin window shows 線上). Try 用 IP 加入 with the host's 26.x IP. On the host, check the firewall rule (`netsh advfirewall firewall show rule name="PataCoop (UDP 27015)"`). |
| "房主拒絕連線（PataCoop 版本不同？）" / "版本不同" | Different PataCoop versions: install the same release on every PC. |
| PataNet was installed | The installer renames `PataNet.dll` → `*.disabled-by-patacoop`; the two mods cannot run together. |
| Anything else | Send `BepInEx\LogOutput.log` (and `%USERPROFILE%\AppData\LocalLow\BANDAI NAMCO Entertainment\PATAPON 1+2 REPLAY\Player.log`) to the developers. |

Saves are backed up before every co-op battle to `BepInEx\PataCoop\save-backups\`.

---

## B. Working on the mod

### Repository map
| Path | What |
|---|---|
| `plugin/` | The mod (`PataCoop.dll`). `Plugin.cs` entry/driver; `Overlay.cs` panel; `Ui.cs` home-made IMGUI widgets; `NameLabels.cs` player labels over armies; `Net/CoopNet.cs` client; `Net/RoomFinder.cs` LAN/Radmin room search. |
| `plugin/Coop/` | Co-op logic. `Session` roster/hello. `Lobby` camp/HQ states, sortie gate. `Battle` battle lifecycle and packet filters. `Armies` per-player squads in one troop. `HitSync` host-owned HP/deaths/gimmicks. `Clock` beat alignment. `MarchRule` "everyone marches together". `StoryCompanions` one story NPC per player. `ActorPools` battle object stock. `Difficulty`, `KeyItems`, `WorldSync` (weather), `MissionScripts`, `StoryArmy`, `SaveBackup`, `NetErrors`, `Messages` (message ids). |
| `server/` | LiteNetLib relay (`RelayServer.cs`, `Wire.cs`), also compiled into the plugin (the host runs it in-game) and as a standalone server. |
| `servertest/` | Relay test suite: start `server` on a port, then `dotnet servertest.dll <port>`. |
| `dev/` | Dev-only plugin `PataCoop.Dev`: HTTP eval server (port 9100+instance), per-instance save sandbox, auto-mute, a native disassembler/indexer (`Native.Find/Dis/Index`). Never ship it. |
| `dist/` | What players get: `install.bat`, `install.ps1`, `README.txt` (Chinese). `dist/plugin/` is build output. |
| `tools/` | WSL bash harness for testing with several game copies on one PC (see below). `tools/evals/*.cs` are in-game hooks run through the eval server. |

### Building
- .NET 8 SDK. The plugin targets net6.0 (BepInEx's runtime).
- The game with **BepInEx be.785** installed and started once: `BepInEx/interop` must exist.
- Tell MSBuild where the game is: env `PATACOOP_GAME`, `Directory.Build.props.user` (not
  committed), or `-p:GameDir=...`. See `Directory.Build.props`.
- `cd plugin && dotnet build -c Release` → `plugin/bin/Release/net6.0/PataCoop.dll` (+ `LiteNetLib.dll`).
- Release: `tools/release` builds the plugin and zips `dist/` into
  `%USERPROFILE%\PataCoop\release\PataCoop-<ver>.zip`. If `friend-preset.ini` exists (see
  `friend-preset.example.ini`) it also builds `-friends.zip`. Upload only the plain zip to GitHub
  Releases.

### Test harness (WSL, one PC, several game copies)
- Paths come from `tools/env.sh`. Override them in `tools/env.local.sh` with
  `PATACOOP_GAME=/mnt/<drive>/.../PATAPON12_REPLAY` and `PATACOOP_WORK=/mnt/c/Users/<you>/PataCoop`.
- `tools/devmode` puts the game folder in dev mode: dev plugin on, `single-instance` removed from
  `boot.config`, and `steam_appid.txt` added so several copies can run. `tools/playmode` undoes
  all of it. **Always return to play mode** when done; it also restores the player's display
  settings.
- `tools/redeploy N`: copy the fresh builds and launch N windowed, muted copies.
- `tools/tocamp i`: drive copy i from the launcher to the camp.
- `tools/ge i <<<'C# code'`: evaluate C# inside copy i. It returns a string; `Press(VirtualPad.X, frames)` presses pad buttons.
- `tools/gshot i`: screenshot.
- `N=2 FULL=1 tools/cooptest4`: host on copy 0, the others join, everyone sorties into the same mission.
  `FULL=1` gives everyone a full army. `ARM="a.cs b.cs"` arms hooks from `tools/evals`.
- `N=2 FULL=1 SPEED=2 tools/jevcoop`: the same, plus an automatic drummer
  (`tools/jevbot.py` + `tools/evals/jevdriver.cs`). It needs a TypeSafe Jev API key in
  `~/.config/typesafe/jev.key`; never commit it. Without a key, use `tools/coopbot2`.
- Test saves are sandboxed per copy in `%USERPROFILE%\PataCoop\saves\i<n>`; tests never touch real
  saves. Progress made in tests is not written back.
- After a test, read both copies' logs. `BepInEx/LogOutput.log` is copy 0 and `LogOutput.1.log`
  copy 1. Look for exceptions, `[coop]` lines and the hooks' trace lines.
- UI checks without touching the player's mouse: post window messages
  (`WM_LBUTTONDOWN`/`WM_CHAR`) to a copy's window. Capture the window from the screen; the eval
  screenshot does not include IMGUI.

### Engine facts and pitfalls (read before patching)
- **ICF folding:** MSVC folded identical functions in `GameAssembly.dll`, so many tiny methods share
  one native body. Hooking one hooks all of them and crashes the game. Check
  `Native.Find("Ns.Class::method")` for "shared by" first.
- **IL2CPP GC:** objects created from C# that only C# holds (`GUIStyle`, `Texture2D`, …) get
  collected ("Object was garbage collected in IL2CPP domain"). Pin them with
  `IL2CPP.il2cpp_gchandle_new(obj.Pointer, false)`.
- **IMGUI is stripped:** only `GUI.Label`/`GUI.Box` work. `GUI.Button`/`TextField` are gone, and
  `GUI.DrawTexture*` is a broken rebuilt copy. Use `Ui.cs`.
- **Input:** the game hides and locks the cursor. Keys go through Unity Input System `PlayerInput`
  maps `MenuControls`/`InGameControls` (WASD, Enter, Esc, Space…); gamepads go through Steam
  Input. `GameKeys` pauses the maps while typing in the panel.
- **Fixed timestep:** the game advances one fixed step (`Game.update(80)`) per frame, so any
  hitch is a permanent beat lag. `Clock` runs extra steps to catch up. Never skip `Game.update`;
  the scene then drops the mission.
- **Borrowed multiplayer engine:** co-op battles run with `isMultiMode`, set in the
  `Game.initialize` postfix. The engine assumes player-troop squad uids 0–3 are players 1–4 (one
  hero squad each), so:
  - it marks other players' squads with `isNoDamageOC`, which we clear;
  - its packets PID 4–10 address squads/units/gimmicks by uid, and our uids collide. Co-op drops
    them; `HitSync` owns hit points, deaths and gimmick breaks instead.
- **Battle stock:** `GameActorPool` holds 64 units, 40 squads and 160 equips, shared with the
  enemies. `ActorPools` grows it to a fixed target per player count.
- **Story missions:** add their own squads to the player troop (`StoryCompanions`, `MissionScripts`).
  The multiplayer "egg carrier" squad functions 7–9 are remapped (`StoryArmy`).
- **Event scripts:** `Script.execute` runs Init/EBox/Gimmick events. Only `EventType_Gimmick` means
  a gimmick broke.
- **Randomness:** `Macros.INTRAND`/`REALRAND` are stubs returning 0, so field drops are
  deterministic. `UnityEngine.Random` (result chests etc.) is per player.
- **Weather:** the host broadcasts the weather. Whether guests' miracles work is still untested.
- **Out of scope:** hero-world egg/carnival missions are not supported yet.

### Rules for changes
- **Single-player must stay untouched.** Gate every patch on `Battle.Active` (co-op battle) or
  `Lobby.InCamp` (Patapon 2 camp/world map/HQ, for UI). Patapon 1 and solo missions must never
  see the panel, the cursor change or blocked keys.
- **Never commit:**
  - game files, `BepInEx/interop` DLLs, decompiled game code;
  - saves or logs;
  - personal data (`friend-preset.ini`, Radmin passwords, IPs, API keys).

  Do not copy code from other mods, such as PataNet; write it ourselves.
- **Versions:** bump `CoopPlugin.Version` (`plugin/Plugin.cs`) for every release. Bump
  `RelayServer.ProtocolVersion` whenever the wire format or any battle rule changes, so mixed
  versions cannot meet; `Lobby.CanSortie` also refuses mixed plugin versions. Update
  `servertest` for protocol changes.
- **Testing:**
  - Run `servertest` after relay changes.
  - Run a 2-player battle (`jevcoop` or `cooptest4` + `coopbot2`) after battle changes, then
    check both logs for exceptions.
  - For UI changes, check the panel in the camp and that a mission is unaffected.
- **Evidence:** before telling the players something works, show it (log lines, screenshots).
- **Style:** match the surrounding code. Comments say why. Keep player text short, in both
  languages, through `Text.T`.
- Keep `dist/README.txt` (players) and this file up to date when behaviour changes. `CLAUDE.md` only imports this file.
- Contributions: fork + pull request, or push access from the owner. One topic per pull request; say how it was tested.
