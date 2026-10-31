namespace IotHub.Devices;

/// <summary>What one bulk group update produced: applied changes and skips.</summary>
public sealed record GroupApplyResult(
    string Group,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Applies one state patch to every device in a room group at once — "turn
/// off every living-room light" without naming each device. Safety rules
/// mirror the scene applier: offline devices are skipped and reported, never
/// acted on (acting would wrongly mark them online), and each device's
/// patch goes through <see cref="DeviceRegistry.UpdateState"/> so range
/// validation and history recording apply per device. A group with no
/// devices is an error, not a silent no-op — the caller almost certainly
/// mistyped the group name.
/// </summary>
public static class GroupControl
{
    /// <summary>Applies <paramref name="patch"/> to every device in
    /// <paramref name="group"/>. Throws <see cref="KeyNotFoundException"/>
    /// when the group has no devices.</summary>
    public static GroupApplyResult ApplyToGroup(DeviceRegistry registry, string group, DeviceState patch)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(patch);
        patch.Validate();
        var devices = registry.List(group);
        if (devices.Count == 0)
            throw new KeyNotFoundException($"No devices in group '{group}'.");
        var applied = new List<string>();
        var skipped = new List<string>();
        foreach (var device in devices)
        {
            if (!device.IsOnline)
            {
                skipped.Add($"{device.Id}: offline");
                continue;
            }
            var changes = registry.UpdateState(device.Id, patch);
            foreach (var change in changes)
                applied.Add($"{device.Id}.{change.Field}={change.NewValue}");
            if (changes.Count == 0)
                applied.Add($"{device.Id}: already in target state");
        }
        return new GroupApplyResult(group, applied, skipped);
    }
}
