namespace IotHub.Devices;

/// <summary>One recorded state change: which field moved, from what to what, and when.
/// The history is the hub's audit trail — automation rules (next chunk) and
/// debugging both read it instead of guessing what a device did.</summary>
public sealed record StateChange(string DeviceId, string Field, string? OldValue, string? NewValue, DateTimeOffset At)
{
    public override string ToString() =>
        $"{At:yyyy-MM-dd HH:mm:ss} {DeviceId}.{Field}: {OldValue ?? "-"} -> {NewValue ?? "-"}";
}
