using System.Text.Json;
using System.Text.Json.Serialization;

namespace IotHub.Scenes;

/// <summary>
/// Persists scenes to a JSON file, mirroring
/// <see cref="Automation.JsonRuleStore"/>: atomic temp-file-plus-move writes,
/// a missing file means "no scenes", and a corrupt or invalid file throws so
/// the caller notices instead of silently running with no scenes.
/// The scenes file lives next to the registry store
/// (e.g. iot-hub.json -> iot-hub.scenes.json).
/// </summary>
public static class JsonSceneStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Derives the scenes file path from a registry store path.</summary>
    public static string ScenesPathFor(string storePath) =>
        Path.ChangeExtension(storePath, ".scenes.json");

    public static void Save(IReadOnlyList<Scene> scenes, string scenesPath)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        foreach (var scene in scenes) scene.Validate();
        var json = JsonSerializer.Serialize(scenes.ToList(), Options);
        var dir = Path.GetDirectoryName(Path.GetFullPath(scenesPath));
        Directory.CreateDirectory(dir!);
        var tmp = scenesPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, scenesPath, overwrite: true);
    }

    public static List<Scene> Load(string scenesPath)
    {
        if (!File.Exists(scenesPath)) return new List<Scene>();
        var scenes = JsonSerializer.Deserialize<List<Scene>>(File.ReadAllText(scenesPath), Options)
            ?? throw new JsonException($"Scenes file '{scenesPath}' is empty or malformed.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in scenes)
        {
            scene.Validate();
            if (!ids.Add(scene.Id))
                throw new InvalidOperationException($"Duplicate scene id '{scene.Id}' in '{scenesPath}'.");
        }
        return scenes;
    }
}
