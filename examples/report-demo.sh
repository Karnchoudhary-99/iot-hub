#!/bin/sh
# Demo tour of reporting: a small hub with two rooms, a rule, and a
# scene, summarised by the report command in text and JSON form.
# Usage: sh examples/report-demo.sh   (builds first; uses a throwaway store)
set -e
cd "$(dirname "$0")/.."
dotnet build src/IotHub -v q --nologo
export IOT_HUB_STORE=/tmp/iot-hub-report-demo.json
rm -f "$IOT_HUB_STORE" /tmp/iot-hub-report-demo.rules.json /tmp/iot-hub-report-demo.scenes.json
HUB="dotnet run --project src/IotHub --no-build --"
$HUB register --id living-room-lamp --name "Living room lamp" --kind light --group living-room
$HUB register --id hall-thermostat --name "Hall thermostat" --kind sensor --group hall
$HUB register --id fan-plug --name "Fan plug" --kind plug --group hall
$HUB set-state living-room-lamp --power on --brightness 70
$HUB set-state hall-thermostat --temperature 27
$HUB rule-add --id hot-fan --name "Fan on when hot" \
  --when hall-thermostat:temperatureC:gt:25 --action fan-plug:power=on --cooldown 0
$HUB scene-add --id goodnight --name "Goodnight" \
  --step living-room-lamp:power=off
echo "--- report ---"
$HUB report
echo "--- report --json (first lines) ---"
$HUB report --json | head -8
