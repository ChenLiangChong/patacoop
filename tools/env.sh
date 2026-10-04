# Paths for the dev tools (sourced by them). Override any of them in the environment or in
# tools/env.local.sh (not committed), for example:
#   PATACOOP_GAME=/mnt/d/Steam/steamapps/common/PATAPON12_REPLAY
#   PATACOOP_WORK=/mnt/c/Users/me/PataCoop
T=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)   # this tools folder
REPO=$(dirname "$T")                              # the repository
[ -f "$T/env.local.sh" ] && . "$T/env.local.sh"
# the game as WSL sees it (BepInEx be.785 installed, started once)
: "${PATACOOP_GAME:=/mnt/e/SteamLibrary/steamapps/common/PATAPON12_REPLAY}"
# a Windows-side work folder: sandboxed test saves, screenshots, eval snippets, release zips
: "${PATACOOP_WORK:=$(wslpath "$(cd /mnt/c && cmd.exe /c 'echo %USERPROFILE%' </dev/null 2>/dev/null | tr -d '\r')")/PataCoop}"
export PATACOOP_GAME PATACOOP_WORK
G=$PATACOOP_GAME
W=$PATACOOP_WORK
WW=$(wslpath -w "$W")   # the work folder as Windows sees it
S=$T/evals              # in-game eval scripts
mkdir -p "$W/tmp" "$W/shots"
