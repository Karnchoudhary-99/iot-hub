namespace IotHub.Devices;

/// <summary>
/// The observable state of one device. Every field is optional: a sensor may
/// report only temperature, a lock only its locked flag. Set fields are
/// range-checked on construction and on every update, so invalid readings
/// (brightness 140, humidity -5) are rejected at the edge, never stored.
/// </summary>
public sealed class DeviceState
{
    /// <summary>True when the device is switched on (lights, plugs, speakers).</summary>
    public bool? Power { get; set; }

    /// <summary>Brightness percentage, 0-100 (lights).</summary>
    public int? Brightness { get; set; }

    /// <summary>Target or measured temperature in °C, -50 to 60.</summary>
    public double? TemperatureC { get; set; }

    /// <summary>Relative humidity percentage, 0-100 (sensors).</summary>
    public double? HumidityPct { get; set; }

    /// <summary>True when locked (locks). Null means "not a lock / unknown".</summary>
    public bool? Locked { get; set; }

    /// <summary>Free-form reading for sensors that do not fit the fields above
    /// (e.g. "motion detected", "door open").</summary>
    public string? Reading { get; set; }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for any field outside its valid range.</summary>
    public void Validate()
    {
        if (Brightness is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(Brightness), Brightness, "Brightness must be 0-100.");
        if (TemperatureC is < -50 or > 60)
            throw new ArgumentOutOfRangeException(nameof(TemperatureC), TemperatureC, "TemperatureC must be -50 to 60.");
        if (HumidityPct is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(HumidityPct), HumidityPct, "HumidityPct must be 0-100.");
    }

    /// <summary>Returns a copy with every non-null field of <paramref name="patch"/> applied, validated.</summary>
    public DeviceState WithPatch(DeviceState patch)
    {
        var merged = new DeviceState
        {
            Power = patch.Power ?? Power,
            Brightness = patch.Brightness ?? Brightness,
            TemperatureC = patch.TemperatureC ?? TemperatureC,
            HumidityPct = patch.HumidityPct ?? HumidityPct,
            Locked = patch.Locked ?? Locked,
            Reading = patch.Reading ?? Reading,
        };
        merged.Validate();
        return merged;
    }

    /// <summary>Field-by-field differences between this state and <paramref name="other"/>.</summary>
    public IEnumerable<(string Field, string? Old, string? New)> Diff(DeviceState other)
    {
        if (Power != other.Power) yield return ("power", F(Power), F(other.Power));
        if (Brightness != other.Brightness) yield return ("brightness", F(Brightness), F(other.Brightness));
        if (TemperatureC != other.TemperatureC) yield return ("temperatureC", F(TemperatureC), F(other.TemperatureC));
        if (HumidityPct != other.HumidityPct) yield return ("humidityPct", F(HumidityPct), F(other.HumidityPct));
        if (Locked != other.Locked) yield return ("locked", L(Locked), L(other.Locked));
        if (Reading != other.Reading) yield return ("reading", Reading, other.Reading);
    }

    private static string? F(object? value) => value switch
    {
        null => null,
        bool b => b ? "on" : "off",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string? L(bool? value) => value switch
    {
        null => null,
        true => "locked",
        false => "unlocked",
    };
}
