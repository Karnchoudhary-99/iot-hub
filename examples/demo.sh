#!/bin/sh
# Demo tour of the device registry: register devices, change state, inspect history.
# Usage: sh examples/demo.sh   (builds first; uses a throwaway store file)
set -e
cd "$(dirname "$0")/.."
dotnet build src/IotHub -v q --nologo
export IOT_HUB_STORE=/tmp/iot-hub-demo.json
rm -f "$IOT_HUB_STORE"
HUB="dotnet run --project src/IotHub --no-build --"
$HUB register --id living-room-lamp --name "Living room lamp" --kind light --group living-room
$HUB register --id hall-thermostat --name "Hall thermostat" --kind thermostat --group hall
$HUB register --id front-door --name "Front door lock" --kind lock --group entrance
$HUB set-state living-room-lamp --power on --brightness 70
$HUB set-state hall-thermostat --temperature 21.5 --humidity 45
$HUB set-state front-door --locked locked
echo "--- all devices ---"
$HUB list
echo "--- living-room only ---"
$HUB list --group living-room
echo "--- lamp history ---"
$HUB history living-room-lamp
echo "--- groups ---"
$HUB groups
