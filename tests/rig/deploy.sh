#!/usr/bin/env bash
# Builds Quasar (with Quasar.Agent) and this plugin here, copies them, this
# repository and the Grid Backups working copy to the test host, and starts
# rig.py there in the background. Its summary lands in
# ~/.se-test/qsr4e2e-runs/<UTC time>/summary.txt on the host, and its output in
# ~/.se-test/qsr4e2e-last.out.
#
# usage: deploy.sh [user@host] [rig.py arguments, e.g. --keep]   (default factory@nas)
# QSR4_WORKAROUNDS=1 is passed on to rig.py (see its docstring).
#
# QSR4_QUASAR_REPO and QSR4_GRID_BACKUPS_REPO name the working copies (default:
# quasar-agent/ and grid-backups/ next to this repository), QSR4_BUILD the local
# build folder (~/.se-test/qsr4e2e-build).
set -euo pipefail

HOST=${1:-factory@nas}
shift || true
HERE=$(cd "$(dirname "$0")" && pwd)
PLUGIN=$(cd "$HERE/../.." && pwd)
QUASAR_REPO=${QSR4_QUASAR_REPO:-$PLUGIN/../quasar-agent}
GB_REPO=${QSR4_GRID_BACKUPS_REPO:-$PLUGIN/../grid-backups}
BUILD=${QSR4_BUILD:-$HOME/.se-test/qsr4e2e-build}
REMOTE=.se-test/qsr4e2e-src

dotnet publish "$QUASAR_REPO/Quasar/Quasar.csproj" -c Release -o "$BUILD/quasar" -v minimal
QuasarPluginAbstractionsProject=$(cd "$QUASAR_REPO" && pwd)/Quasar.Plugin.Abstractions/Quasar.Plugin.Abstractions.csproj \
    dotnet build "$PLUGIN/QuasarAssistant.slnx" -c Release -v minimal

ssh "$HOST" "rm -rf ~/$REMOTE/grid-backups ~/$REMOTE/quasar-assistant && mkdir -p ~/$REMOTE"
rsync -a --delete "$BUILD/quasar/" "$HOST:$REMOTE/quasar-build/"
# Tracked and new files only: Magnetar compiles Grid Backups from source
(cd "$GB_REPO" && git ls-files -co --exclude-standard) |
    rsync -a --files-from=- "$GB_REPO/" "$HOST:$REMOTE/grid-backups/"
(cd "$PLUGIN" && { git ls-files -co --exclude-standard; find src -path '*/bin/Release/*' -type f; }) |
    rsync -a --files-from=- "$PLUGIN/" "$HOST:$REMOTE/quasar-assistant/"

# shellcheck disable=SC2029 -- the arguments are meant to expand here
ssh -n "$HOST" "cd ~/$REMOTE/quasar-assistant/tests/rig && \
    eval \"\$(grep '^export PULSAR_GITHUB_TOKEN=' ~/.bashrc)\" && \
    QSR4_WORKAROUNDS=${QSR4_WORKAROUNDS:-} setsid nohup ~/.local/bin/uv run --no-project --with playwright --with httpx python rig.py run $* \
    > ~/.se-test/qsr4e2e-last.out 2>&1 < /dev/null &"
echo "rig started on $HOST: tail ~/.se-test/qsr4e2e-last.out there"
