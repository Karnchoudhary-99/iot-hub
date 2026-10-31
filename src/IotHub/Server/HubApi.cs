using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IotHub.Automation;
using IotHub.Devices;
using IotHub.Scenes;

namespace IotHub.Server;

/// <summary>One HTTP response from <see cref="HubApi"/>: a status code and a
/// JSON body (empty for 204).</summary>
public sealed record ApiResponse(int Status, string Body);

/// <summary>
/// The hub's HTTP API, deliberately kept socket-free: <see cref="Handle"/>
/// takes a method, a path and an optional JSON body and returns a status
/// plus a JSON body, so tests and CI exercise the exact routing and
/// validation without binding a port (localhost sockets are unreliable on
/// the dev VM — see the approval-resilience note in the program state).
/// <see cref="HubServer"/> is the thin <c>HttpListener</c> adapter that
/// serves this handler over a real socket.
/// Every mutation persists through the <paramref name="persist"/> callback
/// the server wires up, so the HTTP view and the CLI view share one store.
/// </summary>
public sealed class HubApi
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeviceRegistry _registry;
    private readonly List<AutomationRule> _rules;
    private readonly List<Scene> _scenes;
    private readonly Action? _persist;

    public HubApi(DeviceRegistry registry, List<AutomationRule> rules, Action? persist = null, List<Scene>? scenes = null)
    {
        _registry = registry;
        _rules = rules;
        _scenes = scenes ?? new List<Scene>();
        _persist = persist;
    }

    /// <summary>Routes one request. Unknown paths return 404, a known path
    /// with the wrong method returns 405, malformed input returns 400,
    /// unknown ids return 404, duplicates return 409.</summary>
    public ApiResponse Handle(string method, string rawPath, string? body)
    {
        try
        {
            var (path, query) = SplitPath(rawPath);
            var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            return (method.ToUpperInvariant(), segments) switch
            {
                ("GET", ["health"]) => Ok(new { status = "ok", devices = _registry.Count, rules = _rules.Count }),
                ("GET", ["devices"]) => Ok(Devices(query)),
                ("POST", ["devices"]) => CreateDevice(body),
                ("GET", ["devices", var id]) => Ok(DeviceDto(_registry.GetOrThrow(id))),
                ("DELETE", ["devices", var id]) => DeleteDevice(id),
                ("PATCH", ["devices", var id, "state"]) => PatchState(id, body),
                ("POST", ["devices", var id, "online"]) => SetOnline(id, true),
                ("POST", ["devices", var id, "offline"]) => SetOnline(id, false),
                ("GET", ["devices", var id, "history"]) => Ok(History(id, query)),
                ("GET", ["groups"]) => Ok(new { groups = _registry.Groups() }),
                ("PATCH", ["groups", var group, "state"]) => PatchGroupState(group, body),
                ("GET", ["rules"]) => Ok(_rules.Select(RuleDto).ToList()),
                ("POST", ["rules"]) => CreateRule(body),
                ("DELETE", ["rules", var id]) => DeleteRule(id),
                ("POST", ["rules", var id, "enable"]) => SetRuleEnabled(id, true),
                ("POST", ["rules", var id, "disable"]) => SetRuleEnabled(id, false),
                ("POST", ["run-rules"]) => RunRules(),
                ("GET", ["report"]) => Ok(Reporting.HubReport.Build(_registry, _rules, _scenes, TimeSpan.FromMinutes(60)).WithHistory(_registry)),
                ("GET", ["scenes"]) => Ok(_scenes.Select(SceneDto).ToList()),
                ("POST", ["scenes"]) => CreateScene(body),
                ("DELETE", ["scenes", var id]) => DeleteScene(id),
                ("POST", ["scenes", var id, "apply"]) => ApplyScene(id),
                (_, ["health"]) or (_, ["devices"]) or (_, ["devices", _, _])
                    or (_, ["groups"]) or (_, ["groups", _, _])
                    or (_, ["rules"]) or (_, ["rules", _, _])
                    or (_, ["scenes"]) or (_, ["scenes", _, _])
                    or (_, ["report"])
                    or (_, ["run-rules"]) => Status(405, new { error = $"Method {method} is not allowed on {path}." }),
                _ => Status(404, new { error = $"No route for {method} {path}." }),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or JsonException)
        {
            return Status(400, new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return Status(404, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Status(409, new { error = ex.Message });
        }
    }

    // -- devices ---------------------------------------------------------

    private object Devices(Dictionary<string, string> query)
    {
        DeviceKind? kind = null;
        if (query.TryGetValue("kind", out var kindText))
        {
            if (!Enum.TryParse<DeviceKind>(kindText, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Unknown kind '{kindText}'.");
            kind = parsed;
        }
        var onlineOnly = query.TryGetValue("online", out var onlineText)
            ? onlineText switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ArgumentException("Query parameter 'online' must be true or false."),
            }
            : false;
        query.TryGetValue("group", out var group);
        return _registry.List(group, kind, onlineOnly).Select(DeviceDto).ToList();
    }

    private ApiResponse CreateDevice(string? body)
    {
        var doc = RequireObject(body);
        var device = new Device
        {
            Id = RequireString(doc, "id"),
            Name = RequireString(doc, "name"),
            Kind = doc.TryGetProperty("kind", out var kindEl)
                ? Enum.TryParse<DeviceKind>(kindEl.GetString(), ignoreCase: true, out var k)
                    ? k
                    : throw new ArgumentException($"Unknown kind '{kindEl.GetString()}'.")
                : DeviceKind.Other,
            Group = doc.TryGetProperty("group", out var groupEl) ? groupEl.GetString() ?? "" : "",
        };
        _registry.Register(device);
        _persist?.Invoke();
        return Status(201, DeviceDto(device));
    }

    private ApiResponse DeleteDevice(string id)
    {
        if (!_registry.Remove(id))
            throw new KeyNotFoundException($"No device with id '{id}' is registered.");
        _persist?.Invoke();
        return Status(200, new { removed = id });
    }

    private ApiResponse PatchState(string id, string? body)
    {
        var patch = ParseStatePatch(RequireObject(body));
        var changes = _registry.UpdateState(id, patch);
        // A state change may satisfy a rule — same behaviour as the CLI's
        // set-state, which evaluates rules immediately.
        var firings = AutomationEngine.Evaluate(_registry, _rules);
        _persist?.Invoke();
        return Ok(new
        {
            device = DeviceDto(_registry.GetOrThrow(id)),
            changes = changes.Select(c => new { field = c.Field, oldValue = c.OldValue, newValue = c.NewValue }),
            firings = firings.Select(FiringDto).ToList(),
        });
    }

    private ApiResponse SetOnline(string id, bool online)
    {
        _registry.SetOnline(id, online);
        _persist?.Invoke();
        return Ok(DeviceDto(_registry.GetOrThrow(id)));
    }

    private ApiResponse PatchGroupState(string group, string? body)
    {
        var patch = ParseStatePatch(RequireObject(body));
        var result = GroupControl.ApplyToGroup(_registry, group, patch);
        _persist?.Invoke();
        return Ok(new { group = result.Group, applied = result.Applied, skipped = result.Skipped });
    }

    private object History(string id, Dictionary<string, string> query)
    {
        _ = _registry.GetOrThrow(id);
        var limit = query.TryGetValue("limit", out var limitText)
            ? int.Parse(limitText, CultureInfo.InvariantCulture)
            : 0;
        return _registry.History(id, limit)
            .Select(c => new { deviceId = c.DeviceId, field = c.Field, oldValue = c.OldValue, newValue = c.NewValue, at = c.At })
            .ToList();
    }

    // -- rules -----------------------------------------------------------

    private ApiResponse CreateRule(string? body)
    {
        var doc = RequireObject(body);
        var rule = new AutomationRule
        {
            Id = RequireString(doc, "id"),
            Name = RequireString(doc, "name"),
            // Enabled unless explicitly set to false.
            Enabled = !(doc.TryGetProperty("enabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.False),
            When = RuleCondition.Parse(RequireString(doc, "when")),
            CooldownSeconds = doc.TryGetProperty("cooldownSeconds", out var cdEl) ? cdEl.GetInt32() : 60,
        };
        if (!doc.TryGetProperty("actions", out var actionsEl) || actionsEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Field 'actions' must be an array of targetId:field=value strings.");
        foreach (var actionEl in actionsEl.EnumerateArray())
            rule.Actions.Add(RuleAction.Parse(actionEl.GetString()
                ?? throw new ArgumentException("Each action must be a string.")));
        rule.Validate();
        if (_rules.Any(r => r.Id == rule.Id))
            throw new InvalidOperationException($"A rule with id '{rule.Id}' already exists.");
        // Same guard as the CLI: a rule pointing at an unregistered device
        // can never fire, only confuse.
        _ = _registry.GetOrThrow(rule.When.DeviceId);
        foreach (var action in rule.Actions) _ = _registry.GetOrThrow(action.TargetDeviceId);
        _rules.Add(rule);
        _persist?.Invoke();
        return Status(201, RuleDto(rule));
    }

    private ApiResponse DeleteRule(string id)
    {
        if (_rules.RemoveAll(r => r.Id == id) == 0)
            throw new KeyNotFoundException($"No rule with id '{id}'.");
        _persist?.Invoke();
        return Status(200, new { removed = id });
    }

    private ApiResponse SetRuleEnabled(string id, bool enabled)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == id)
            ?? throw new KeyNotFoundException($"No rule with id '{id}'.");
        rule.Enabled = enabled;
        _persist?.Invoke();
        return Ok(RuleDto(rule));
    }

    private ApiResponse RunRules()
    {
        var firings = AutomationEngine.Evaluate(_registry, _rules);
        _persist?.Invoke();
        return Ok(new { firings = firings.Select(FiringDto).ToList() });
    }

    // -- scenes ----------------------------------------------------------

    private ApiResponse CreateScene(string? body)
    {
        var doc = RequireObject(body);
        var scene = new Scene
        {
            Id = RequireString(doc, "id"),
            Name = RequireString(doc, "name"),
        };
        if (!doc.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Field 'steps' must be an array of targetId:field=value strings.");
        foreach (var stepEl in stepsEl.EnumerateArray())
            scene.Steps.Add(RuleAction.Parse(stepEl.GetString()
                ?? throw new ArgumentException("Each step must be a string.")));
        scene.Validate();
        if (_scenes.Any(s => s.Id == scene.Id))
            throw new InvalidOperationException($"A scene with id '{scene.Id}' already exists.");
        foreach (var step in scene.Steps) _ = _registry.GetOrThrow(step.TargetDeviceId);
        _scenes.Add(scene);
        _persist?.Invoke();
        return Status(201, SceneDto(scene));
    }

    private ApiResponse DeleteScene(string id)
    {
        if (_scenes.RemoveAll(s => s.Id == id) == 0)
            throw new KeyNotFoundException($"No scene with id '{id}'.");
        _persist?.Invoke();
        return Status(200, new { removed = id });
    }

    private ApiResponse ApplyScene(string id)
    {
        var scene = _scenes.FirstOrDefault(s => s.Id == id)
            ?? throw new KeyNotFoundException($"No scene with id '{id}'.");
        var result = SceneApplier.Apply(_registry, scene);
        _persist?.Invoke();
        return Ok(new { sceneId = result.SceneId, applied = result.Applied, skipped = result.Skipped });
    }

    private static object SceneDto(Scene s) => new
    {
        s.Id,
        s.Name,
        steps = s.Steps.Select(a => a.ToString()).ToList(),
    };

    // -- DTOs and parsing ------------------------------------------------

    private static object DeviceDto(Device d) => new
    {
        d.Id,
        d.Name,
        kind = d.Kind.ToString().ToLowerInvariant(),
        d.Group,
        isOnline = d.IsOnline,
        lastSeenUtc = d.LastSeenUtc,
        state = new
        {
            power = d.State.Power,
            brightness = d.State.Brightness,
            temperatureC = d.State.TemperatureC,
            humidityPct = d.State.HumidityPct,
            locked = d.State.Locked,
            reading = d.State.Reading,
        },
    };

    private static object RuleDto(AutomationRule r) => new
    {
        r.Id,
        r.Name,
        r.Enabled,
        when = r.When.ToString(),
        actions = r.Actions.Select(a => a.ToString()).ToList(),
        r.CooldownSeconds,
        r.LastFiredUtc,
    };

    private static object FiringDto(RuleFiring f) => new
    {
        ruleId = f.RuleId,
        ruleName = f.RuleName,
        applied = f.Applied,
        skipped = f.Skipped,
    };

    private static DeviceState ParseStatePatch(JsonElement doc)
    {
        var patch = new DeviceState();
        foreach (var property in doc.EnumerateObject())
        {
            switch (property.Name)
            {
                case "power": patch.Power = RequireBool(property.Value, "power"); break;
                case "brightness": patch.Brightness = property.Value.GetInt32(); break;
                case "temperatureC": case "temperature": patch.TemperatureC = property.Value.GetDouble(); break;
                case "humidityPct": case "humidity": patch.HumidityPct = property.Value.GetDouble(); break;
                case "locked": patch.Locked = RequireLocked(property.Value); break;
                case "reading": patch.Reading = property.Value.GetString(); break;
                default: throw new ArgumentException($"Unknown state field '{property.Name}'.");
            }
        }
        patch.Validate();
        return patch;
    }

    private static bool RequireBool(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ArgumentException($"Field '{field}' must be a boolean."),
    };

    private static bool RequireLocked(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString() switch
        {
            "locked" => true,
            "unlocked" => false,
            _ => throw new ArgumentException("Field 'locked' must be locked/unlocked or a boolean."),
        },
        _ => throw new ArgumentException("Field 'locked' must be locked/unlocked or a boolean."),
    };

    private static JsonElement RequireObject(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("A JSON object body is required.");
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Request body must be a JSON object.");
        return doc.RootElement.Clone();
    }

    private static string RequireString(JsonElement doc, string field) =>
        doc.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())
            ? el.GetString()!
            : throw new ArgumentException($"Field '{field}' is required and must be a non-empty string.");

    private static (string Path, Dictionary<string, string> Query) SplitPath(string rawPath)
    {
        var q = rawPath.IndexOf('?');
        var path = q < 0 ? rawPath : rawPath[..q];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
        {
            foreach (var pair in rawPath[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                query[eq < 0 ? pair : pair[..eq]] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return (path, query);
    }

    private static ApiResponse Ok(object value) => Status(200, value);

    private static ApiResponse Status(int status, object value) =>
        new(status, JsonSerializer.Serialize(value, Json));
}
