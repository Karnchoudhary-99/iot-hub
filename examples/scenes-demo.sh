#!/bin/sh
# Demo tour of scenes: one "goodnight" scene switches the lamp off and
# locks the door; an offline device is skipped, not fatal.
# Usage: sh examples/scenes-demo.sh   (builds first; uses a throwaway store)
set -e
cd "$(dirname "$0")/.."
dotnet build src/IotHub -v q --nologo
export IOT_HUB_STORE=/tmp/iot-hub-scenes-demo.json
rm -f "$IOT_HUB_STORE" /tmp/iot-hub-scenes-demo.scenes.json
HUB="dotnet run --project src/IotHub --no-build --"
$HUB register --id living-room-lamp --name "Living room lamp" --kind light --group living-room
$HUB register --id front-door --name "Front door" --kind lock --group hall
$HUB set-state living-room-lamp --power on --brightness 70
$HUB scene-add --id goodnight --name "Goodnight" \
  --step living-room-lamp:power=off --step front-door:locked=locked
echo "--- apply goodnight ---"
$HUB scene-apply goodnight
$HUB show living-room-lamp
$HUB show front-door
echo "--- offline the lamp, apply again (lamp skipped, door still applied) ---"
$HUB offline living-room-lamp
$HUB scene-apply goodnight
echo "--- scenes ---"
$HUB scenes
