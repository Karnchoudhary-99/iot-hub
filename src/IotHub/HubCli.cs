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
                _ => UsageError(output, $"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
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
    }
}
