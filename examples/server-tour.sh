#!/bin/sh
# Live tour of the HTTP hub server: starts `serve` in the background,
# exercises the API with curl, then stops the server.
# Usage: sh examples/server-tour.sh   (builds first; uses a throwaway store)
set -e
cd "$(dirname "$0")/.."
dotnet build src/IotHub -v q --nologo
export IOT_HUB_STORE=/tmp/iot-hub-server-demo.json
rm -f "$IOT_HUB_STORE" /tmp/iot-hub-server-demo.rules.json
./src/IotHub/bin/Debug/net8.0/IotHub serve --port 8099 > /tmp/iot-hub-server.log 2>&1 &
SERVER_PID=$!
trap 'kill $SERVER_PID 2>/dev/null || true' EXIT
for i in $(seq 1 30); do
  if curl -fsS http://localhost:8099/health > /dev/null 2>&1; then break; fi
  sleep 1
done
echo "--- health ---"
curl -fsS http://localhost:8099/health; echo
echo "--- register + set state ---"
curl -fsS -X POST http://localhost:8099/devices \
  -H 'Content-Type: application/json' \
  -d '{"id":"living-room-lamp","name":"Living room lamp","kind":"light","group":"living-room"}'; echo
curl -fsS -X PATCH http://localhost:8099/devices/living-room-lamp/state \
  -H 'Content-Type: application/json' \
  -d '{"power":true,"brightness":70}'; echo
echo "--- devices + history ---"
curl -fsS http://localhost:8099/devices; echo
curl -fsS http://localhost:8099/devices/living-room-lamp/history; echo
echo "--- server request log ---"
cat /tmp/iot-hub-server.log
