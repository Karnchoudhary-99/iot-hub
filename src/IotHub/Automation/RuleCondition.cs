using System.Globalization;
using IotHub.Devices;

namespace IotHub.Automation;

/// <summary>
/// One predicate over a single device's state, e.g. "hall-thermostat's
/// temperatureC is greater than 25" or "front-door's reading contains
/// 'motion'". A field that is unknown (null) on the device never matches —
/// a rule must not fire on a reading the hub has never seen.
/// </summary>
public sealed class RuleCondition
{
    /// <summary>Id of the device whose state is watched.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Watched field: power, brightness, temperatureC, humidityPct,
    /// locked, reading, or online. Aliases temperature/humidity are accepted
    /// by <see cref="Parse"/> and normalised here.</summary>
    public string Field { get; set; } = "";

    /// <summary>Comparison: eq, ne, gt, ge, lt, le, contains (reading only).</summary>
    public string Op { get; set; } = "eq";

    /// <summary>Right-hand side, as written: "25", "on", "locked", "motion".</summary>
    public string Value { get; set; } = "";

    private static readonly string[] NumericFields = { "brightness", "temperatureC", "humidityPct" };
    private static readonly string[] BoolFields = { "power", "locked", "online" };

    /// <summary>Parses "deviceId:field:op:value" (the value may contain ':').
    /// Throws <see cref="ArgumentException"/> on a malformed condition.</summary>
    public static RuleCondition Parse(string text)
    {
        var parts = text.Split(':', 4);
        if (parts.Length != 4 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Condition '{text}' must be deviceId:field:op:value, e.g. hall-thermostat:temperatureC:gt:25.");
        var condition = new RuleCondition
        {
            DeviceId = parts[0],
            Field = parts[1] switch { "temperature" => "temperatureC", "humidity" => "humidityPct", var f => f },
            Op = parts[2] switch { "=" => "eq", "!=" => "ne", ">" => "gt", ">=" => "ge", "<" => "lt", "<=" => "le", var o => o },
            Value = parts[3],
        };
        condition.Validate();
        return condition;
    }

    /// <summary>Throws <see cref="ArgumentException"/> for an unknown field/op
    /// or a value that cannot be interpreted for the field.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
            throw new ArgumentException("Condition device id must not be empty.", nameof(DeviceId));
        if (!NumericFields.Contains(Field) && !BoolFields.Contains(Field) && Field != "reading")
            throw new ArgumentException($"Unknown condition field '{Field}'. Valid: power, brightness, temperatureC, humidityPct, locked, reading, online.", nameof(Field));
        if (!new[] { "eq", "ne", "gt", "ge", "lt", "le", "contains" }.Contains(Op))
            throw new ArgumentException($"Unknown condition operator '{Op}'. Valid: eq, ne, gt, ge, lt, le, contains.", nameof(Op));
        if (Op == "contains" && Field != "reading")
            throw new ArgumentException("Operator 'contains' only applies to the reading field.", nameof(Op));
        if (NumericFields.Contains(Field) && (Op is "eq" or "ne" or "gt" or "ge" or "lt" or "le") &&
            !double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException($"Condition value '{Value}' is not a number for numeric field '{Field}'.", nameof(Value));
        if (BoolFields.Contains(Field))
            _ = ParseBool(Field, Value); // throws on an unrecognised word
    }

    /// <summary>True when the device's current state satisfies this predicate.</summary>
    public bool Matches(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(device.Id, DeviceId, StringComparison.Ordinal)) return false;

        if (NumericFields.Contains(Field))
        {
            var actual = Field switch
            {
                "brightness" => device.State.Brightness is { } b ? (double)b : null,
                "temperatureC" => device.State.TemperatureC,
                "humidityPct" => device.State.HumidityPct,
                _ => null,
            };
            if (actual is null) return false;
            var expected = double.Parse(Value, CultureInfo.InvariantCulture);
            return Op switch
            {
                "eq" => actual.Value == expected,
                "ne" => actual.Value != expected,
                "gt" => actual.Value > expected,
                "ge" => actual.Value >= expected,
                "lt" => actual.Value < expected,
                "le" => actual.Value <= expected,
                _ => false,
            };
        }

        if (BoolFields.Contains(Field))
        {
            var actual = Field switch
            {
                "power" => device.State.Power,
                "locked" => device.State.Locked,
                "online" => device.IsOnline,
                _ => null,
            };
            if (actual is null) return false;
            var expected = ParseBool(Field, Value);
            return Op switch
            {
                "eq" => actual.Value == expected,
                "ne" => actual.Value != expected,
                _ => false,
            };
        }

        // reading
        if (device.State.Reading is null) return false;
        return Op switch
        {
            "eq" => string.Equals(device.State.Reading, Value, StringComparison.OrdinalIgnoreCase),
            "ne" => !string.Equals(device.State.Reading, Value, StringComparison.OrdinalIgnoreCase),
            "contains" => device.State.Reading.Contains(Value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    internal static bool ParseBool(string field, string value) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "locked" or "online" => true,
        "off" or "false" or "unlocked" or "offline" => false,
        _ => throw new ArgumentException($"Value '{value}' is not a valid boolean for field '{field}' (use on/off, locked/unlocked, online/offline, or true/false)."),
    };

    public override string ToString() => $"{DeviceId}:{Field}:{Op}:{Value}";
}
