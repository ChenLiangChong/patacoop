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
| `plugin/Coop/` | Co-op logic. `Session` roster/hello. `Lobby` camp/HQ states, sortie gate. `Battle` battle lifecycle and packet filters. `Armies` per-player squads in one troop. `HitSync` host-owned HP/deaths/gimmicks. `Clock` beat alignment. `ArmyPositions` every army walks on its own drums (positions of armies, units and the host's enemies every frame; events and enemy arrivals follow the army furthest ahead). `EnemyRoster` guests field the host's enemy squads. `MarchRule` own march, last command per player. `StoryCompanions` one story NPC per player. `ActorPools` battle object stock. `Difficulty`, `KeyItems`, `WorldSync` (weather), `MissionScripts`, `StoryArmy`, `SaveBackup`, `NetErrors`, `Messages` (message ids). |
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
- `tools/redeploy N`: copy the fresh builds and launch N windowed, muted copies (about 25 s).
- Small steps (prefer these over long scripts with fixed sleeps; each returns as soon as the screen
  changes, and says where it is stuck):
  - `tools/state i`: where copy i is in one line (`launcher`, `title:<screen>`,
    `camp:<phase>:<facility>:m<mission>:hq<state>`, `battle:<phase>`, `down`).
  - `tools/press i <button> [frames]`, `tools/waitfor i <regex> [seconds]`.
  - `tools/go i camp|worldmap|hq|battle`: walks copy i there one step at a time, from any screen
    (including out of a battle). `MISSION=<id>` picks the mission.
  - `FULL=1 tools/coop N`: N copies into one co-op battle from wherever they are (about 35 s from
    the camp, 90 s from launch).
  - `tools/compare N`: every copy's battle state at the same moment, and what differs from the host:
    army positions, enemies (missing, position, hit points), gimmick hit points.
- `tools/tocamp i`: drive copy i from the launcher to the camp (older, fixed waits).
- `tools/ge i <<<'C# code'`: evaluate C# inside copy i. It returns a string; `Press(VirtualPad.X, frames)` presses pad buttons.
- `tools/gshot i`: screenshot.
- `tools/toworldmap i` opens the world map from anywhere in the camp. `tools/pickmission i <id>` moves its cursor to a mission; it reads the map's mission list, which also lists every unlocked mission with its type.
- `N=2 FULL=1 tools/cooptest4`: the older launcher (relaunches, fixed waits); `coop` replaces it.
  `FULL=1` fills everyone's army to four full squads (the hero kept); without it everyone brings the
  save's own formation, as players do (`botrun` does that by default). Armies without a hero (the
  old `FULL=1`) made the host's `UnitTroop.receiveCommand` throw null references in fever, which
  never happened with heroes. `ARM="a.cs b.cs"` arms hooks from `tools/evals`.
- `tools/evals/drumprobe.cs`: `DrumProbe.Play("0:A,2:A,4:A,6:D")` presses drums (A PATA,
  S DON, W CHAKA, D PON) at half beats after the next beat, each for exactly one battle step, the
  way a player's key arrives (fever, miracles and any command can be scripted this way).
  `tools/drumonce` prints the timeline it records.
- `tools/botrun <mission> [N]`: relaunch, play the mission with the Jev bot and run `compare`
  every 10 s (`tmp/bot<mission>.txt`, `tmp/cmp<mission>.txt`). `tools/botall <missions...>` runs
  several and prints one summary line each.
- Test hooks run inside the game's hottest native calls. Patch only what a test needs and give
  hooks typed parameters: `object[] __args` on a method called every step with `ref` arguments
  (`SubGame.Miracle.Command.update`) preceded a CoreCLR crash, and a patch on an instance method
  native code calls with a null `this` (`TroopCtrl.getSquadLineTopPosX`) throws in Il2CppInterop's
  wrapper on every call (40 000 errors in two seconds), and an `object __result` postfix on a method
  returning a `Nullable` of an Il2Cpp struct (`SharedRandom.Enter`) crashed the runtime at the next
  battle's setup. Remove probes (`UnpatchSelf`) when done.
- A slower network on one PC: in a copy, `PataCoop.Net.CoopNet.SimLatencyMs = 60;
  SimJitterMs = 20; SimLossPercent = 2;` delays every incoming room message (order kept) and
  drops that share of the unreliable ones. Off (0) by default; it never reaches players.
- `AUTO=1 tools/drumonce` (in a running battle): every copy plays one PATA PATA PATA PON with
  the game's own auto drum (`LAG=0.5` starts each next copy a beat later) and prints, per half
  beat from the first drum, the hits, when the command was fixed and executed, and how far the
  army and its units moved. Measure march timing and distance at normal speed: `SPEED` distorts
  them. `VirtualPad` presses are frame-based (fine for menus); script drums with `DrumProbe.Play`.
  The auto drum (`Director.setAutoCommand`) cannot play miracles.
- `tools/evals/cycle.cs`: plays drum commands in blocks with the auto drum (`Vars["cycle"] =
  "3x3,2x3"`: attack three times, defend three times, again), e.g. to build fever.
- `tools/evals/miracle.cs` (with `cycle.cs` running): at the first command in fever it stops the
  auto drum and the Jev bot's drums, drums DON - DONDON - DONDON, plays the rhythm game as its
  script asks and logs every half beat (mode, beat timer, judgments, score).
  `tools/evals/coopclock.cs`: one line of a copy's battle clock, catch-up steps and weather.
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
- **Battle clock:** the beat timer (`BeatTimer.tick_`, 80 ticks a step) is not a battle clock. A
  miracle's rhythm game restarts it from 0, and so does the return to the battle music (the step
  after that restart does not even advance it), on the drummer's machine only. Comparing raw ticks
  made the drummer "behind" by the whole battle, so `Clock` fast-forwarded the rhythm game and
  the rest of the battle. `BattleClock` (the beat timer at the battle's first step plus 80 ticks a
  battle step) is what `Clock`, `SharedRandom` and `MarchRule` use.
- **Own armies:** the engine has one player troop (one base position, one flag bearer). Each
  machine's base is its own player's army: `OwnMarchPatch` gives `moveSquadLine` the local player's
  instructions, and PD_TroopBasePos (which overwrites the base) is dropped everywhere. The engine
  walks the troop only while the LOCAL `BeatCommander` is in `State_Kaesi` (or player 0's command
  is Miss, or `UnitMng.info_.isKaeshiOnlyOff`). Other players' squads: while `UnitSquad.update` runs
  for one of them, the base and the flag bearer's model stand at that player's army (squads measure
  places and ranges from both), and afterwards its units are put where their owner reported them
  (`Msg.ArmyPos`, every frame). Unit layout ids match on every machine.
- **Position-driven world events:** mission scripts poll `CommandGame.getFlagUnitPosX` and
  `getTroopTopPos`; enemy squads come on in `UnitTroop.squadAddingCheck` as the player troop's base
  passes them. Both are shown the army furthest ahead, so every machine spawns the same squads in
  the same order. Enemy squad `uniqueId`s must match: `HitSync` matches units by unit and squad id.
  A guest's own clear (its army at the goal) is sent to the host (`Msg.Goal`), which clears for all.
- **Which enemies are on the field:** a mission lists its enemy squads as candidates
  (`UnitTroop.addSquadToAddingList`) and brings them in with `squadAddingCheck(checkX, id)`, by
  place or by id from its script (then `addSquad(param, false)` and the candidate leaves the list).
  A hunting ground's script picks its herds at the battle's first step after a burst of 100-odd
  script rolls whose count differs between machines, so the picks differed. `EnemyRoster` makes
  each guest field the host's squads: it brings in, by `squadAddingCheck(far away, id)`, any squad
  the host reports (`Msg.EnemyPos`) and it lacks, takes away (`UnitBase.deleteUnit`, no death or
  drops) any squad of its own the host has not reported for 1.5 s, and lets no squad come twice.
  Only in the battle's first 10 s: later waves (fortresses) come in on every machine a moment apart,
  and one brought in late from the host's report came whole while the host's had lost units.
- **No fixed rhythm:** the engine's multiplayer path sets `BeatCommander.isFixedRhythm_`. With it,
  the army answers only on every other bar line: a command whose last drum lands anywhere else
  waits one to three beats, the player's next command cuts that answer short, and every player
  who does not drum gets a Miss each cycle. Co-op keeps it off (`Battle.Commit`), so the army
  answers right after each player's own last drum, as in single-player.
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
  deterministic. Which squads a mission brings on (a hunting ground's herds) is rolled with
  `UnityEngine.Random` in the mission scripts' `cmd_rand`, `UnitSquad.addUnitSet` and
  `CommandGame.variousProcForAllUnit`: `SharedRandom` seeds each of those rolls from the host's
  battle seed (sent with the sortie), the roll's script or squad, the battle step (`BattleClock`)
  and its count in that step, so every machine rolls the same. Scripts roll every step, so one
  shared stream is not enough. Everything else (result chests, damage spread, weather, particles)
  keeps its own randomness.
- **Unit identity:** a unit's `info_.uniqueId` is a slot in the battle stock taken in creation
  order and differs between machines for enemies. `UnitIds` names a unit by troop, squad
  `uniqueId` (the mission's own squad ids, or ours) and its place in the squad (sticky when units
  before it fall), as the game's own multiplayer did (squad id and line index). Hit points and
  enemy positions are matched by it.
- **Miracles:** in fever, DON - DONDON - DONDON (half beats 0 2 3 5 6 of a drum bar) enters
  `Mode_Miracl`, a rhythm sub-game (`SubGame.Miracle`) on its own beat timer from 0: 160 half
  beats in rounds of 16 (`cursor.commandGrup.commandList[hb / 16]`, one command per half beat). In
  each round the first 8 half beats are the call and the last 8 (`act2 == 1`) list the drums to
  answer in `hit[0]` (bit 1 PON, 2 DON, 4 PATA, 8 CHAKA; the high bits are display). The Rain
  rounds answer 3, 3, 4, 4, 4, 4, 5 and 7 PONs (one hit in the last round is `19`, which neither
  PON nor DON alone satisfies; 7 perfect rounds of 8 still work). The game reads the drums with `Pad.stand` and judges
  each in `Script.Analyzer.hitCheck` (OK / NG). Then `P2.Game.Miracle.Controller.activate(score)`
  starts the mission's miracle (`Rain`, `Storm`, `Wind`... change the weather over time with
  `ParamId_Miracle`). A guest's activation is sent to the host, which activates it too; guests
  take the host's weather (`WorldSync`). Tested: a guest's Rain in mission 80 rains on both.
- **Battle input:** drums arrive through two paths: the pad bits read with
  `P2.System.Pad.Pad.stand` (0x8000 PATA, 0x2000 PON, 0x4000 DON, 0x1000 CHAKA) and
  `MultiPlatformInputManager.isActionThisFrame<InGameControls>`, whose native code is shared with
  the MenuControls instantiation. A real key reaches one of them; a scripted press must too, and a
  key held for more than one battle step counts as several hits (the second one a miss). Machines
  catching up run two steps in a frame, so frame-based presses land late or twice: press for
  exactly one step, on the step that reaches the half-beat line (`drumprobe.cs` does).
- **Weather:** the host broadcasts the weather.
- **Camera:** `TrackingCamera.update` runs by `mode_`. In mode 0 (tracking), co-op (`isMultiMode`)
  takes `updateMultiTracking`: the camera stands at the troop's rearmost unit
  (`UnitTroop.getUnitPosX(true, false, true)[0]`) plus the view offset, clamped to 250 from our own
  flag bearer, so our army stays in view. In mode 1 the camera stands where `setX` put it:
  `WatchGameMarch.mainNormal` puts it at our flag bearer, and boss missions' scripts
  (`CommandGame.setCameraX`) put it from the flag bearer they read, which is the leading army's
  (`ScriptsSeeTheLeadPatch`). `ScriptCameraPatch` moves such a place back by the same lead, so each
  player's camera follows their own army there too.
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
