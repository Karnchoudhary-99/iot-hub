# IoT Home Automation Hub

Control and automate smart-home devices from one hub: a device registry,
automation rules that react to device state, and a control CLI — in C#.

## Roadmap

- [x] Project scaffold (console app layout, CI, README)
- [x] Device registry (register, group, state tracking)
- [x] Automation rules (triggers, conditions, actions)
- [x] Control CLI + hub server
- [x] Scenes (named multi-device actions, CLI + HTTP)
- [ ] Self-review checklist, then public

## Quick start

```sh
dotnet build src/IotHub
dotnet run --project src/IotHub -- register --id living-room-lamp --name "Living room lamp" --kind light --group living-room
dotnet run --project src/IotHub -- set-state living-room-lamp --power on --brightness 70
dotnet run --project src/IotHub -- list
dotnet run --project src/IotHub -- history living-room-lamp
```

Or run the full tour: `sh examples/demo.sh` (uses a throwaway store in
`/tmp/iot-hub-demo.json`).

## Device registry

Devices are registered with a stable id (`living-room-lamp`), a name, a kind
(light, thermostat, lock, sensor, plug, camera, speaker) and a room group.
State is range-checked at the edge (brightness 0–100, temperature −50–60 °C,
humidity 0–100%), and every change is appended to a timestamped history —
the audit trail automation rules will read next.

The registry persists to JSON (`./iot-hub.json` by default, override with
`$IOT_HUB_STORE`). Saves are atomic (temp file + move), so a crash mid-save
never corrupts the registry.

Commands: `register`, `list [--group G] [--kind K] [--online]`, `show <id>`,
`set-state <id> [--power on|off] [--brightness N] [--temperature X]
[--humidity X] [--locked locked|unlocked] [--reading TEXT]`, `remove <id>`,
`groups`, `history <id> [--limit N]`, `online <id>` / `offline <id>`,
`sweep [--max-age-minutes N]` (marks devices silent longer than N minutes
offline; default 60).
Exit codes: 0 ok, 1 runtime error (unknown device, duplicate id, bad value),
2 usage error.

## Automation rules

A rule watches one device's state and, when its condition holds, applies
state changes to target devices — "when the hall thermostat reads over
25 °C, switch the fan plug on":

```sh
dotnet run --project src/IotHub -- rule-add --id hot-fan --name "Fan on when hot" \
  --when hall-thermostat:temperatureC:gt:25 --action fan-plug:power=on
dotnet run --project src/IotHub -- set-state hall-thermostat --temperature 27
# Rule fired: hot-fan "Fan on when hot" — applied: fan-plug.power=on
```

Conditions are `DEVICE:FIELD:OP:VALUE`. Fields: `power`, `brightness`,
`temperatureC` (alias `temperature`), `humidityPct` (alias `humidity`),
`locked`, `reading`, `online`. Operators: `eq`, `ne`, `gt`, `ge`, `lt`,
`le`, `contains` (reading only); `=`, `!=`, `>`, `>=`, `<`, `<=` are
accepted as aliases. Boolean values accept `on`/`off`, `locked`/`unlocked`,
`online`/`offline`, `true`/`false`. A field the hub has never seen (null)
never matches, so a rule cannot fire on an unknown reading.

Actions are `TARGET:FIELD=VALUE` (repeat `--action` for several targets),
with the same fields as `set-state`. Rules are evaluated automatically
after every `set-state`, and on demand with `run-rules`.

Safety behaviour, in order: disabled rules never fire; a rule inside its
cooldown (default 60 s, `--cooldown N`, persisted across CLI invocations)
never fires; an action whose target is unknown or offline is skipped and
reported, and does not consume the cooldown — the rule retries on the next
evaluation instead of going quiet. Actions go through the same validated,
history-recorded state update as a manual `set-state`.

Rules live in a sidecar file next to the store (`iot-hub.json` →
`iot-hub.rules.json`), saved atomically like the registry. Rules are
validated on load, and `rule-add` rejects rules pointing at devices that
are not registered.

Commands: `rule-add`, `rules`, `rule-remove <id>`,
`rule-enable <id>` / `rule-disable <id>`, `run-rules`.

Try it: `sh examples/rules-demo.sh` (uses a throwaway store).

## Scenes

A scene bundles several state changes into one named action — apply
"goodnight" and the hub switches the lights off and locks the doors in a
single step. Steps use the same `TARGET:FIELD=VALUE` shape as rule
actions, and applying a scene skips unknown or offline targets instead of
failing the whole scene (an offline lamp does not stop the door locking).

```sh
dotnet run --project src/IotHub -- scene-add --id goodnight --name "Goodnight" \
  --step living-room-lamp:power=off --step front-door:locked=locked
dotnet run --project src/IotHub -- scene-apply goodnight
dotnet run --project src/IotHub -- scenes
```

Scenes live in a sidecar file next to the store (`iot-hub.json` →
`iot-hub.scenes.json`) and are also available over HTTP (`GET /scenes`,
`POST /scenes`, `DELETE /scenes/{id}`, `POST /scenes/{id}/apply`).

Try it: `sh examples/scenes-demo.sh`.

## Hub server

The same registry and rules are available over HTTP. Start the server
(it shares the CLI's store file, so CLI and HTTP views stay in sync):

```sh
dotnet run --project src/IotHub -- serve --port 8080
curl http://localhost:8080/health
curl -X POST http://localhost:8080/devices \
  -H 'Content-Type: application/json' \
  -d '{"id":"lamp","name":"Lamp","kind":"light","group":"living-room"}'
curl -X PATCH http://localhost:8080/devices/lamp/state \
  -H 'Content-Type: application/json' -d '{"power":true,"brightness":70}'
```

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/health` | Status plus device/rule counts |
| GET | `/devices[?group=&kind=&online=]` | List, with the CLI's filters |
| POST | `/devices` | `{id, name, kind, group}` → 201, 409 on duplicate |
| GET / DELETE | `/devices/{id}` | |
| PATCH | `/devices/{id}/state` | Partial state patch; fires rules like `set-state`, returns `{device, changes, firings}` |
| POST | `/devices/{id}/online`, `/offline` | |
| GET | `/devices/{id}/history[?limit=]` | Newest first |
| GET | `/groups` | |
| GET / POST | `/rules` | `{id, name, when, actions[], cooldownSeconds, enabled}` |
| DELETE | `/rules/{id}` | |
| POST | `/rules/{id}/enable`, `/disable` | |
| POST | `/run-rules` | Returns `{firings}` |
| GET / POST | `/scenes` | `{id, name, steps[]}` |
| DELETE | `/scenes/{id}` | |
| POST | `/scenes/{id}/apply` | Returns `{sceneId, applied, skipped}` |

Errors are JSON `{"error": "..."}` with 400 (bad input), 404
(unknown id or route), 405 (wrong method) or 409 (duplicate id).
Routing lives in the socket-free `HubApi` handler, so the endpoint
suite runs without binding a port; the server itself is exercised
live by CI via `sh examples/server-tour.sh` (uses a throwaway store).

Try it: `sh examples/server-tour.sh`.

## License

MIT — see [LICENSE](LICENSE).
