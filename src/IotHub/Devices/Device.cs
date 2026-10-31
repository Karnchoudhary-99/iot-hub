namespace IotHub.Devices;

/// <summary>A registered smart-home device: its identity, where it lives in
/// the house, whether it is reachable, and its last known state.</summary>
public sealed class Device
{
    /// <summary>Stable identifier, e.g. "living-room-lamp". Unique in the registry.
    /// Lowercase letters, digits, '-' and '_' only.</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable name, e.g. "Living room lamp".</summary>
    public string Name { get; set; } = "";

    public DeviceKind Kind { get; set; } = DeviceKind.Other;

    /// <summary>Room or zone, e.g. "living-room". Devices with no group use "".</summary>
    public string Group { get; set; } = "";

    /// <summary>True when the hub has heard from the device recently.</summary>
    public bool IsOnline { get; set; } = true;

    /// <summary>UTC time of the last state update or heartbeat.</summary>
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

    public DeviceState State { get; set; } = new();

    /// <summary>Throws <see cref="ArgumentException"/> for an invalid id, name or state.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Any(char.IsWhiteSpace) ||
            Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException($"Device id '{Id}' is invalid: use letters, digits, '-' and '_' only, no spaces.", nameof(Id));
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Device name must not be empty.", nameof(Name));
        State.Validate();
    }

    public override string ToString() =>
        $"{Id} ({Kind}) [{(string.IsNullOrEmpty(Group) ? "ungrouped" : Group)}] {(IsOnline ? "online" : "offline")}";
}
