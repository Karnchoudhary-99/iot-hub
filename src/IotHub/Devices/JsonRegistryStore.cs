using System.Text.Json;
using System.Text.Json.Serialization;

namespace IotHub.Devices;

/// <summary>
/// Persists a registry to a JSON file. Writes are atomic: the new content goes
/// to a temp file in the same directory and is moved over the old file, so a
/// crash mid-save never leaves a half-written registry behind. Loading a
/// missing file returns an empty registry; loading a corrupt file throws so
/// the caller notices instead of silently starting empty.
/// </summary>
public static class JsonRegistryStore
{
    private sealed record Snapshot(List<Device> Devices, List<StateChange> History);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Save(DeviceRegistry registry, string path)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var snapshot = new Snapshot(registry.List(), registry.AllHistory().Reverse().ToList());
        var json = JsonSerializer.Serialize(snapshot, Options);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        Directory.CreateDirectory(dir!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static DeviceRegistry Load(string path)
    {
        var registry = new DeviceRegistry();
        if (!File.Exists(path)) return registry;
        var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Options)
            ?? throw new JsonException($"Registry file '{path}' is empty or malformed.");
        foreach (var device in snapshot.Devices) device.Validate();
        registry.Restore(snapshot.Devices, snapshot.History);
        return registry;
    }
}
