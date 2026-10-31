#!/bin/sh
# Demo tour of automation rules: a hot-room rule switches a fan plug on,
# and disabling the rule stops it reacting.
# Usage: sh examples/rules-demo.sh   (builds first; uses a throwaway store)
set -e
cd "$(dirname "$0")/.."
dotnet build src/IotHub -v q --nologo
export IOT_HUB_STORE=/tmp/iot-hub-rules-demo.json
rm -f "$IOT_HUB_STORE" /tmp/iot-hub-rules-demo.rules.json
HUB="dotnet run --project src/IotHub --no-build --"
$HUB register --id hall-thermostat --name "Hall thermostat" --kind sensor --group hall
$HUB register --id fan-plug --name "Fan plug" --kind plug --group hall
$HUB rule-add --id hot-fan --name "Fan on when hot" \
  --when hall-thermostat:temperatureC:gt:25 --action fan-plug:power=on --cooldown 0
echo "--- thermostat to 27C (rule should fire) ---"
$HUB set-state hall-thermostat --temperature 27
$HUB show fan-plug
echo "--- disable the rule, thermostat to 28C (rule must NOT fire) ---"
$HUB rule-disable hot-fan
$HUB set-state hall-thermostat --temperature 28
echo "--- rules ---"
$HUB rules
