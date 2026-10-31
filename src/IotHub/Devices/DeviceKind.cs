namespace IotHub.Devices;

/// <summary>The kind of a smart-home device. The kind documents what a device
/// is for; state fields are range-checked regardless of kind, so a new kind
/// never needs a code change to be registered.</summary>
public enum DeviceKind
{
    Light,
    Thermostat,
    Lock,
    Sensor,
    Plug,
    Camera,
    Speaker,
    Other,
}
