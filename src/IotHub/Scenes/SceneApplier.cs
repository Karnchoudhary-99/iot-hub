using IotHub.Devices;

namespace IotHub.Scenes;

/// <summary>What one scene application produced: applied changes and skips.</summary>
public sealed record SceneResult(
    string SceneId,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Applies a scene to a registry, step by step in list order. Safety rules
/// mirror the automation engine: a step whose target is unknown or offline
/// is skipped and reported, never applied — the hub cannot act on a device
/// it cannot reach, and must not mark it online by "acting" on it. Steps go
/// through <see cref="DeviceRegistry.UpdateState"/> so range validation and
/// history recording apply exactly as they do for a manual set-state.
/// </summary>
public static class SceneApplier
{
    /// <summary>Applies every step of <paramref name="scene"/> to
    /// <paramref name="registry"/>. Callers persist the registry afterwards.</summary>
    public static SceneResult Apply(DeviceRegistry registry, Scene scene)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scene);
        scene.Validate();
        var applied = new List<string>();
        var skipped = new List<string>();
        foreach (var step in scene.Steps)
        {
            var target = registry.Get(step.TargetDeviceId);
            if (target is null)
            {
                skipped.Add($"{step.TargetDeviceId}: unknown device");
                continue;
            }
            if (!target.IsOnline)
            {
                skipped.Add($"{step.TargetDeviceId}: offline");
                continue;
            }
            var changes = registry.UpdateState(step.TargetDeviceId, step.SetState);
            foreach (var change in changes)
                applied.Add($"{step.TargetDeviceId}.{change.Field}={change.NewValue}");
            if (changes.Count == 0)
                applied.Add($"{step.TargetDeviceId}: already in target state");
        }
        return new SceneResult(scene.Id, applied, skipped);
    }
}
