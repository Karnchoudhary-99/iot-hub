using IotHub.Automation;

namespace IotHub.Scenes;

/// <summary>
/// A scene: a named bundle of state changes applied to several devices at
/// once — "goodnight" switches the lights off and locks the doors, "movie
/// night" dims the living-room lamp and switches the speaker on. Steps are
/// <see cref="RuleAction"/> values, the same targetId:field=value shape the
/// automation rules use, so one parser and one validator cover both.
/// Scenes are applied on demand (CLI or HTTP), never automatically.
/// </summary>
public sealed class Scene
{
    /// <summary>Stable identifier, e.g. "goodnight". Unique per scene set.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>State changes applied in list order when the scene runs.</summary>
    public List<RuleAction> Steps { get; set; } = new();

    /// <summary>Throws <see cref="ArgumentException"/> for an invalid scene.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Any(char.IsWhiteSpace) ||
            Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException($"Scene id '{Id}' is invalid: use letters, digits, '-' and '_' only, no spaces.", nameof(Id));
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Scene name must not be empty.", nameof(Name));
        if (Steps.Count == 0)
            throw new ArgumentException($"Scene '{Id}' has no steps.", nameof(Steps));
        foreach (var step in Steps) step.Validate();
    }

    public override string ToString() =>
        $"{Id} \"{Name}\" -> {string.Join("; ", Steps)}";
}
