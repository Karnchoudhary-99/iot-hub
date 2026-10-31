using IotHub.Automation;
using IotHub.Devices;

namespace IotHub;

/// <summary>
/// The control CLI, kept separate from <c>Program</c> so tests can drive it
/// without spawning a process. Every command loads the registry from the store
/// file, runs, and saves after a mutation — the file is the hub's memory.
/// Exit codes: 0 ok, 1 runtime error (unknown device, duplicate id, bad value),
/// 2 usage error.
/// </summary>
public static class HubCli
{
    public static int Run(string[] args, TextWriter output, string storePath)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(output);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "register" => Register(args, output, storePath),
                "list" => List(args, output, storePath),
                "show" => Show(args, output, storePath),
                "set-state" => SetState(args, output, storePath),
                "remove" => Remove(args, output, storePath),
                "groups" => Groups(output, storePath),
                "history" => History(args, output, storePath),
                "online" => SetOnline(args, output, storePath, true),
                "offline" => SetOnline(args, output, storePath, false),
                "sweep" => Sweep(args, output, storePath),
                "rule-add" => RuleAdd(args, output, storePath),
                "rules" => Rules(output, storePath),
                "rule-remove" => RuleRemove(args, output, storePath),
                "rule-enable" => RuleSetEnabled(args, output, storePath, true),
                "rule-disable" => RuleSetEnabled(args, output, storePath, false),
                "run-rules" => RunRules(output, storePath),
                "serve" => Server.HubServer.Run(args, output, storePath),
                _ => UsageError(output, $"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or System.Text.Json.JsonException)
        {
            output.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Register(string[] args, TextWriter output, string storePath)
    {
        var (pos, flags) = Parse(args, 1);
        _ = pos;
        var id = Required(flags, "id");
        var name = Required(flags, "name");
        if (!Enum.TryParse<DeviceKind>(flags.GetValueOrDefault("kind", "Other"), ignoreCase: true, out var kind))
            return UsageError(output, $"Unknown kind '{flags["kind"]}'. Valid: {string.Join(", ", Enum.GetNames<DeviceKind>())}.");
        var registry = JsonRegistryStore.Load(storePath);
        var device = new Device { Id = id, Name = name, Kind = kind, Group = flags.GetValueOrDefault("group", "") };
        registry.Register(device);
        JsonRegistryStore.Save(registry, storePath);
        output.WriteLine($"Registered {device}");
        return 0;
    }

    private static int List(string[] args, TextWriter output, string storePath)
    {
        var (_, flags) = Parse(args, 1);
        DeviceKind? kind = null;
        if (flags.TryGetValue("kind", out var kindText))
        {
            if (!Enum.TryParse<DeviceKind>(kindText, ignoreCase: true, out var parsed))
                return UsageError(output, $"Unknown kind '{kindText}'.");
            kind = parsed;
        }
        var registry = JsonRegistryStore.Load(storePath);
        var devices = registry.List(flags.GetValueOrDefault("group"), kind, flags.ContainsKey("online"));
        if (devices.Count == 0) output.WriteLine("No devices registered.");
        foreach (var d in devices) output.WriteLine(d);
        return 0;
    }

    private static int Show(string[] args, TextWriter output, string storePath)
    {
        var (pos, _) = Parse(args, 1);
        if (pos.Count != 1) return UsageError(output, "Usage: show <device-id>");
        var d = JsonRegistryStore.Load(storePath).GetOrThrow(pos[0]);
        output.WriteLine(d);
        output.WriteLine($"  Name: {d.Name}");
        output.WriteLine($"  Last seen (UTC): {d.LastSeenUtc:yyyy-MM-dd HH:mm:ss}");
        var s = d.State;
        output.WriteLine($"  State: power={OnOff(s.Power)} brightness={s.Brightness?.ToString() ?? "-"} " +
                         $"temperatureC={s.TemperatureC?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} " +
                         $"humidityPct={s.HumidityPct?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} " +
                         $"locked={Locked(s.Locked)} reading={s.Reading ?? "-"}");
        return 0;
    }

    private static int SetState(string[] args, TextWriter output, string storePath)
    {
        var (pos, flags) = Parse(args, 1);
        if (pos.Count != 1) return UsageError(output, "Usage: set-state <device-id> [--power on|off] [--brightness n] [--temperature x] [--humidity x] [--locked locked|unlocked] [--reading text]");
        var patch = new DeviceState();
        if (flags.TryGetValue("power", out var power)) patch.Power = ParseOnOff(power, "power");
        if (flags.TryGetValue("brightness", out var b)) patch.Brightness = int.Parse(b, System.Globalization.CultureInfo.InvariantCulture);
        if (flags.TryGetValue("temperature", out var t)) patch.TemperatureC = double.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
        if (flags.TryGetValue("humidity", out var h)) patch.HumidityPct = double.Parse(h, System.Globalization.CultureInfo.InvariantCulture);
        if (flags.TryGetValue("locked", out var l)) patch.Locked = l switch { "locked" => true, "unlocked" => false, _ => throw new ArgumentException("Use --locked locked|unlocked.") };
        if (flags.TryGetValue("reading", out var r)) patch.Reading = r;
        var registry = JsonRegistryStore.Load(storePath);
        var changes = registry.UpdateState(pos[0], patch);
        JsonRegistryStore.Save(registry, storePath);
        if (changes.Count == 0) output.WriteLine("No state changed.");
        foreach (var c in changes) output.WriteLine(c);
        // A state change may satisfy a rule: evaluate immediately so the hub
        // reacts without waiting for an explicit run-rules. A broken rules
        // file must not turn a successful set-state into a failure — the
        // explicit run-rules command surfaces that error instead.
        try { EvaluateRules(registry, storePath, output, quietWhenNone: true); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { output.WriteLine($"Warning: automation evaluation skipped: {ex.Message}"); }
        return 0;
    }

    private static int Remove(string[] args, TextWriter output, string storePath)
    {
        var (pos, _) = Parse(args, 1);
        if (pos.Count != 1) return UsageError(output, "Usage: remove <device-id>");
        var registry = JsonRegistryStore.Load(storePath);
        if (!registry.Remove(pos[0])) { output.WriteLine($"Error: No device with id '{pos[0]}' is registered."); return 1; }
        JsonRegistryStore.Save(registry, storePath);
        output.WriteLine($"Removed {pos[0]}.");
        return 0;
    }

    private static int Groups(TextWriter output, string storePath)
    {
        var groups = JsonRegistryStore.Load(storePath).Groups();
        if (groups.Count == 0) output.WriteLine("No groups yet.");
        foreach (var g in groups) output.WriteLine(g);
        return 0;
    }

    private static int History(string[] args, TextWriter output, string storePath)
    {
        var (pos, flags) = Parse(args, 1);
        if (pos.Count != 1) return UsageError(output, "Usage: history <device-id> [--limit n]");
        var limit = flags.TryGetValue("limit", out var l) ? int.Parse(l, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var registry = JsonRegistryStore.Load(storePath);
        _ = registry.GetOrThrow(pos[0]);
        var entries = registry.History(pos[0], limit);
        if (entries.Count == 0) output.WriteLine("No recorded state changes.");
        foreach (var e in entries) output.WriteLine(e);
        return 0;
    }

    private static int SetOnline(string[] args, TextWriter output, string storePath, bool online)
    {
        var (pos, _) = Parse(args, 1);
        if (pos.Count != 1) return UsageError(output, $"Usage: {(online ? "online" : "offline")} <device-id>");
        var registry = JsonRegistryStore.Load(storePath);
        registry.SetOnline(pos[0], online);
        JsonRegistryStore.Save(registry, storePath);
        output.WriteLine($"{pos[0]} is now {(online ? "online" : "offline")}.");
        return 0;
    }

    private static int Sweep(string[] args, TextWriter output, string storePath)
    {
        var (_, flags) = Parse(args, 1);
        var minutes = flags.TryGetValue("max-age-minutes", out var m)
            ? double.Parse(m, System.Globalization.CultureInfo.InvariantCulture)
            : 60;
        var registry = JsonRegistryStore.Load(storePath);
        var flipped = registry.MarkStale(TimeSpan.FromMinutes(minutes));
        JsonRegistryStore.Save(registry, storePath);
        if (flipped.Count == 0) output.WriteLine("No stale devices.");
        foreach (var d in flipped) output.WriteLine($"Marked offline (stale): {d.Id}");
        return 0;
    }

    // -- automation rules ------------------------------------------------

    private static int RuleAdd(string[] args, TextWriter output, string storePath)
    {
        // --action may repeat, so scan here instead of using Parse (which keeps
        // only the last value per flag). --disabled is a value-less flag.
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var actions = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--disabled") { flags["disabled"] = "true"; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                if (args[i] == "--action") actions.Add(args[++i]);
                else flags[args[i][2..]] = args[++i];
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Flag '{args[i]}' needs a value.");
            else
                return UsageError(output, "Usage: rule-add --id ID --name NAME --when DEVICE:FIELD:OP:VALUE --action TARGET:FIELD=VALUE [--action ...] [--cooldown N] [--disabled]");
        }
        var id = Required(flags, "id");
        var name = Required(flags, "name");
        var when = RuleCondition.Parse(Required(flags, "when"));
        if (actions.Count == 0)
            throw new ArgumentException("At least one --action is required.");
        var rule = new AutomationRule
        {
            Id = id,
            Name = name,
            Enabled = !flags.ContainsKey("disabled"),
            When = when,
            Actions = actions.Select(RuleAction.Parse).ToList(),
            CooldownSeconds = flags.TryGetValue("cooldown", out var c) ? int.Parse(c, System.Globalization.CultureInfo.InvariantCulture) : 60,
        };
        rule.Validate();
        var rulesPath = JsonRuleStore.RulesPathFor(storePath);
        var rules = JsonRuleStore.Load(rulesPath);
        if (rules.Any(r => r.Id == id))
            throw new InvalidOperationException($"A rule with id '{id}' already exists.");
        // The trigger and action targets must exist: a rule pointing at a
        // device that was never registered can never fire, only confuse.
        var registry = JsonRegistryStore.Load(storePath);
        _ = registry.GetOrThrow(when.DeviceId);
        foreach (var action in rule.Actions) _ = registry.GetOrThrow(action.TargetDeviceId);
        rules.Add(rule);
        JsonRuleStore.Save(rules, rulesPath);
        output.WriteLine($"Added rule {rule}");
        return 0;
    }

    private static int Rules(TextWriter output, string storePath)
    {
        var rules = JsonRuleStore.Load(JsonRuleStore.RulesPathFor(storePath));
        if (rules.Count == 0) output.WriteLine("No automation rules.");
        foreach (var r in rules) output.WriteLine(r);
        return 0;
    }

    private static int RuleRemove(string[] args, TextWriter output, string storePath)
    {
        var (pos, _) = Parse(args, 1);
        if (pos.Count != 1) return UsageError(output, "Usage: rule-remove <rule-id>");
        var rulesPath = JsonRuleStore.RulesPathFor(storePath);
        var rules = JsonRuleStore.Load(rulesPath);
        if (rules.RemoveAll(r => r.Id == pos[0]) == 0) { output.WriteLine($"Error: No rule with id '{pos[0]}'."); return 1; }
        JsonRuleStore.Save(rules, rulesPath);
        output.WriteLine($"Removed rule {pos[0]}.");
        return 0;
    }

    private static int RuleSetEnabled(string[] args, TextWriter output, string storePath, bool enabled)
    {
        var (pos, _) = Parse(args, 1);
        if (pos.Count != 1) return UsageError(output, $"Usage: rule-{(enabled ? "enable" : "disable")} <rule-id>");
        var rulesPath = JsonRuleStore.RulesPathFor(storePath);
        var rules = JsonRuleStore.Load(rulesPath);
        var rule = rules.FirstOrDefault(r => r.Id == pos[0]);
        if (rule is null) { output.WriteLine($"Error: No rule with id '{pos[0]}'."); return 1; }
        rule.Enabled = enabled;
        JsonRuleStore.Save(rules, rulesPath);
        output.WriteLine($"Rule {pos[0]} is now {(enabled ? "enabled" : "disabled")}.");
        return 0;
    }

    private static int RunRules(TextWriter output, string storePath)
    {
        var registry = JsonRegistryStore.Load(storePath);
        var firings = EvaluateRules(registry, storePath, output, quietWhenNone: false);
        _ = firings;
        return 0;
    }

    /// <summary>Evaluates the stored rules, persists any state changes and
    /// cooldown updates, and prints firings. Returns the firings.</summary>
    private static IReadOnlyList<RuleFiring> EvaluateRules(DeviceRegistry registry, string storePath, TextWriter output, bool quietWhenNone)
    {
        var rulesPath = JsonRuleStore.RulesPathFor(storePath);
        var rules = JsonRuleStore.Load(rulesPath);
        var firings = AutomationEngine.Evaluate(registry, rules);
        if (firings.Count == 0)
        {
            if (!quietWhenNone) output.WriteLine("No rules fired.");
            return firings;
        }
        JsonRegistryStore.Save(registry, storePath);
        JsonRuleStore.Save(rules, rulesPath);
        foreach (var firing in firings)
        {
            output.WriteLine($"Rule fired: {firing.RuleId} \"{firing.RuleName}\"");
            foreach (var a in firing.Applied) output.WriteLine($"  applied: {a}");
            foreach (var s in firing.Skipped) output.WriteLine($"  skipped: {s}");
        }
        return firings;
    }

    // -- parsing helpers -------------------------------------------------

    private static (List<string> Positional, Dictionary<string, string> Flags) Parse(string[] args, int start)
    {
        var pos = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = start; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                flags[args[i][2..]] = args[++i];
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Flag '{args[i]}' needs a value.");
            else
                pos.Add(args[i]);
        }
        return (pos, flags);
    }

    private static string Required(Dictionary<string, string> flags, string name) =>
        flags.TryGetValue(name, out var v) ? v : throw new ArgumentException($"Missing required flag --{name}.");

    private static bool ParseOnOff(string value, string flag) => value switch
    {
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException($"Use --{flag} on|off."),
    };

    private static string OnOff(bool? v) => v switch { null => "-", true => "on", false => "off" };
    private static string Locked(bool? v) => v switch { null => "-", true => "locked", false => "unlocked" };

    private static int UsageError(TextWriter output, string message)
    {
        output.WriteLine($"Error: {message}");
        PrintUsage(output);
        return 2;
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("IoT Home Automation Hub");
        output.WriteLine("Usage: hub <command> [options]   (store file: $IOT_HUB_STORE or ./iot-hub.json)");
        output.WriteLine("  register --id ID --name NAME [--kind Kind] [--group GROUP]");
        output.WriteLine("  list [--group GROUP] [--kind Kind] [--online]");
        output.WriteLine("  show <id>");
        output.WriteLine("  set-state <id> [--power on|off] [--brightness N] [--temperature X] [--humidity X] [--locked locked|unlocked] [--reading TEXT]");
        output.WriteLine("  remove <id>");
        output.WriteLine("  groups");
        output.WriteLine("  history <id> [--limit N]");
        output.WriteLine("  online <id> | offline <id>");
        output.WriteLine("  sweep [--max-age-minutes N]  (default 60)");
        output.WriteLine("  rule-add --id ID --name NAME --when DEVICE:FIELD:OP:VALUE --action TARGET:FIELD=VALUE [--action ...] [--cooldown N] [--disabled]");
        output.WriteLine("  rules | rule-remove <rule-id> | rule-enable <rule-id> | rule-disable <rule-id>");
        output.WriteLine("  run-rules  (rules also evaluate automatically after every set-state)");
        output.WriteLine("  serve [--port N]  (HTTP API on http://localhost:N/, default 8080)");
    }
}
