using IotHub.Devices;

namespace IotHub.Automation;

/// <summary>One state change an automation rule applies to a target device:
/// a partial <see cref="DeviceState"/> patch, exactly like a CLI set-state.</summary>
public sealed class RuleAction
{
    /// <summary>Id of the device the action acts on.</summary>
    public string TargetDeviceId { get; set; } = "";

    /// <summary>Partial state patch; at least one field must be set.</summary>
    public DeviceState SetState { get; set; } = new();

    /// <summary>Parses "targetId:field=value,field=value". Throws
    /// <see cref="ArgumentException"/> on a malformed action.</summary>
    public static RuleAction Parse(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0)
            throw new ArgumentException($"Action '{text}' must be targetId:field=value[,field=value], e.g. fan-plug:power=on.");
        var action = new RuleAction { TargetDeviceId = text[..colon] };
        foreach (var assignment in text[(colon + 1)..].Split(','))
        {
            var eq = assignment.IndexOf('=');
            if (eq <= 0)
                throw new ArgumentException($"Action field '{assignment}' must be field=value.");
            var field = assignment[..eq];
            var value = assignment[(eq + 1)..];
            switch (field)
            {
                case "power": action.SetState.Power = ParseOnOff(value); break;
                case "brightness": action.SetState.Brightness = ParseInt(value, field); break;
                case "temperature": case "temperatureC": action.SetState.TemperatureC = ParseDouble(value, field); break;
                case "humidity": case "humidityPct": action.SetState.HumidityPct = ParseDouble(value, field); break;
                case "locked": action.SetState.Locked = value switch { "locked" => true, "unlocked" => false, _ => throw new ArgumentException("Use locked=locked|unlocked.") }; break;
                case "reading": action.SetState.Reading = value; break;
                default: throw new ArgumentException($"Unknown action field '{field}'. Valid: power, brightness, temperature, humidity, locked, reading.");
            }
        }
        action.Validate();
        return action;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when the action has no
    /// target or no state change, or its patch fails range validation.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetDeviceId))
            throw new ArgumentException("Action target device id must not be empty.", nameof(TargetDeviceId));
        var s = SetState;
        if (s.Power is null && s.Brightness is null && s.TemperatureC is null &&
            s.HumidityPct is null && s.Locked is null && s.Reading is null)
            throw new ArgumentException($"Action for '{TargetDeviceId}' sets no state field.", nameof(SetState));
        s.Validate();
    }

    private static bool ParseOnOff(string value) => value switch
    {
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException("Use power=on|off."),
    };

    private static int ParseInt(string value, string field) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Value '{value}' is not an integer for field '{field}'.");

    private static double ParseDouble(string value, string field) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Value '{value}' is not a number for field '{field}'.");

    public override string ToString()
    {
        var s = SetState;
        var parts = new List<string>();
        if (s.Power is not null) parts.Add($"power={(s.Power.Value ? "on" : "off")}");
        if (s.Brightness is not null) parts.Add($"brightness={s.Brightness}");
        if (s.TemperatureC is not null) parts.Add($"temperature={s.TemperatureC}");
        if (s.HumidityPct is not null) parts.Add($"humidity={s.HumidityPct}");
        if (s.Locked is not null) parts.Add($"locked={(s.Locked.Value ? "locked" : "unlocked")}");
        if (s.Reading is not null) parts.Add($"reading={s.Reading}");
        return $"{TargetDeviceId}:{string.Join(",", parts)}";
    }
}

/// <summary>
/// An automation rule: when a watched device's state matches a condition,
/// apply actions to target devices. Rules are evaluated in list order; a rule
/// that fired recently (within its cooldown) is skipped so a persistently
/// true condition does not spam the history on every evaluation.
/// </summary>
public sealed class AutomationRule
{
    /// <summary>Stable identifier, e.g. "hot-room-fan". Unique per rule set.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Disabled rules are listed but never evaluated.</summary>
    public bool Enabled { get; set; } = true;

    public RuleCondition When { get; set; } = new();

    public List<RuleAction> Actions { get; set; } = new();

    /// <summary>Minimum seconds between firings. 0 means every evaluation may fire.</summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>UTC time this rule last fired (persisted, so the cooldown
    /// survives separate CLI invocations).</summary>
    public DateTimeOffset? LastFiredUtc { get; set; }

    /// <summary>Throws <see cref="ArgumentException"/> for an invalid rule.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Any(char.IsWhiteSpace) ||
            Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException($"Rule id '{Id}' is invalid: use letters, digits, '-' and '_' only, no spaces.", nameof(Id));
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Rule name must not be empty.", nameof(Name));
        if (CooldownSeconds < 0)
            throw new ArgumentException("CooldownSeconds must not be negative.", nameof(CooldownSeconds));
        ArgumentNullException.ThrowIfNull(When);
        When.Validate();
        if (Actions.Count == 0)
            throw new ArgumentException($"Rule '{Id}' has no actions.", nameof(Actions));
        foreach (var action in Actions) action.Validate();
    }

    public override string ToString() =>
        $"{Id} \"{Name}\" [{(Enabled ? "enabled" : "disabled")}] when {When} -> {string.Join("; ", Actions)}";
}
