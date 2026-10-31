# IoT Home Automation Hub

Control and automate smart-home devices from one hub: a device registry,
automation rules that react to device state, and a control CLI — in C#.

## Roadmap

- [x] Project scaffold (console app layout, CI, README)
- [x] Device registry (register, group, state tracking)
- [ ] Automation rules (triggers, conditions, actions)
- [ ] Control CLI + hub server
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

## License

MIT — see [LICENSE](LICENSE).
