#!/bin/sh
# Demo tour of group control: one group-set switches off every
# living-room lamp, leaves the hall lamp alone, and skips an offline lamp.
# Usage: sh examples/group-demo.sh   (builds first; uses a throwaway store)
set -e
cd "$(dirname "$0")/.."
dotnet build src/IotHub -v q --nologo
export IOT_HUB_STORE=/tmp/iot-hub-group-demo.json
rm -f "$IOT_HUB_STORE"
HUB="dotnet run --project src/IotHub --no-build --"
$HUB register --id lamp1 --name "Lamp 1" --kind light --group living-room
$HUB register --id lamp2 --name "Lamp 2" --kind light --group living-room
$HUB register --id hall-lamp --name "Hall lamp" --kind light --group hall
$HUB set-state lamp1 --power on
$HUB set-state lamp2 --power on
$HUB offline lamp2
echo "--- group-set living-room --power off ---"
$HUB group-set living-room --power off
$HUB show lamp1
$HUB show hall-lamp
