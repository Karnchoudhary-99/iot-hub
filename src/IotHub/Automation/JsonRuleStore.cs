using System.Text.Json;
using System.Text.Json.Serialization;

namespace IotHub.Automation;

/// <summary>
/// Persists automation rules to a JSON file, mirroring
/// <see cref="Devices.JsonRegistryStore"/>: atomic temp-file-plus-move writes,
/// a missing file means "no rules", and a corrupt or invalid file throws so
/// the caller notices instead of silently running with no automations.
/// The rules file lives next to the registry store
/// (e.g. iot-hub.json -> iot-hub.rules.json).
/// </summary>
public static class JsonRuleStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Derives the rules file path from a registry store path.</summary>
    public static string RulesPathFor(string storePath) =>
        Path.ChangeExtension(storePath, ".rules.json");

    public static void Save(IReadOnlyList<AutomationRule> rules, string rulesPath)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules) rule.Validate();
        var json = JsonSerializer.Serialize(rules.ToList(), Options);
        var dir = Path.GetDirectoryName(Path.GetFullPath(rulesPath));
        Directory.CreateDirectory(dir!);
        var tmp = rulesPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, rulesPath, overwrite: true);
    }

    public static List<AutomationRule> Load(string rulesPath)
    {
        if (!File.Exists(rulesPath)) return new List<AutomationRule>();
        var rules = JsonSerializer.Deserialize<List<AutomationRule>>(File.ReadAllText(rulesPath), Options)
            ?? throw new JsonException($"Rules file '{rulesPath}' is empty or malformed.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            rule.Validate();
            if (!ids.Add(rule.Id))
                throw new InvalidOperationException($"Duplicate rule id '{rule.Id}' in '{rulesPath}'.");
        }
        return rules;
    }
}
