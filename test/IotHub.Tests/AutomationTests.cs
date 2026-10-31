using Xunit;
using System.IO;
using IotHub.Automation;
using IotHub.Devices;

namespace IotHub.Tests;

public sealed class ConditionTests
{
    private static Device Thermo(double? temp = 26) =>
        new() { Id = "thermo", Name = "Thermostat", Kind = DeviceKind.Thermostat, State = new DeviceState { TemperatureC = temp } };

    [Fact]
    public void Parse_Normalises_Aliases_And_Symbol_Operators()
    {
        var c = RuleCondition.Parse("thermo:temperature:>:25");
        Assert.Equal("temperatureC", c.Field);
        Assert.Equal("gt", c.Op);
        Assert.Equal("25", c.Value);
    }

    [Fact]
    public void Parse_Rejects_Malformed_Conditions()
    {
        Assert.Throws<ArgumentException>(() => RuleCondition.Parse("thermo:temperatureC:gt"));
        Assert.Throws<ArgumentException>(() => RuleCondition.Parse("thermo:colour:eq:red"));
        Assert.Throws<ArgumentException>(() => RuleCondition.Parse("thermo:temperatureC:approx:25"));
        Assert.Throws<ArgumentException>(() => RuleCondition.Parse("thermo:temperatureC:contains:2"));
        Assert.Throws<ArgumentException>(() => RuleCondition.Parse("thermo:temperatureC:gt:hot"));
    }

    [Fact]
    public void Matches_Numeric_And_Null_Never_Matches()
    {
        var gt = RuleCondition.Parse("thermo:temperatureC:gt:25");
        Assert.True(gt.Matches(Thermo(26)));
        Assert.False(gt.Matches(Thermo(25)));
        Assert.False(gt.Matches(Thermo(null))); // unknown reading must not fire
        Assert.False(gt.Matches(new Device { Id = "other", Name = "Other", State = new DeviceState { TemperatureC = 99 } }));
    }

    [Fact]
    public void Matches_Reading_Contains_Is_Case_Insensitive()
    {
        var device = new Device { Id = "sensor", Name = "Sensor", State = new DeviceState { Reading = "Motion Detected" } };
        Assert.True(RuleCondition.Parse("sensor:reading:contains:motion").Matches(device));
        Assert.False(RuleCondition.Parse("sensor:reading:contains:door").Matches(device));
        Assert.True(RuleCondition.Parse("sensor:reading:eq:motion detected").Matches(device));
    }

    [Fact]
    public void Matches_Bool_Fields_Use_OnOff_Words()
    {
        var lamp = new Device { Id = "lamp", Name = "Lamp", State = new DeviceState { Power = true } };
        Assert.True(RuleCondition.Parse("lamp:power:eq:on").Matches(lamp));
        Assert.True(RuleCondition.Parse("lamp:power:ne:off").Matches(lamp));
        Assert.False(RuleCondition.Parse("lamp:power:eq:off").Matches(lamp));
        var unknown = new Device { Id = "lamp2", Name = "Lamp2" };
        Assert.False(RuleCondition.Parse("lamp2:power:eq:off").Matches(unknown));
    }
}

public sealed class EngineTests
{
    private static (DeviceRegistry Registry, AutomationRule Rule) Setup(double temp = 26, bool plugOnline = true)
    {
        var registry = new DeviceRegistry();
        registry.Register(new Device { Id = "thermo", Name = "Thermostat", Kind = DeviceKind.Thermostat, State = new DeviceState { TemperatureC = temp } });
        registry.Register(new Device { Id = "plug", Name = "Plug", Kind = DeviceKind.Plug, IsOnline = plugOnline, State = new DeviceState { Power = false } });
        var rule = new AutomationRule
        {
            Id = "hot-fan",
            Name = "Fan on when hot",
            When = RuleCondition.Parse("thermo:temperatureC:gt:25"),
            Actions = new List<RuleAction> { RuleAction.Parse("plug:power=on") },
            CooldownSeconds = 60,
        };
        return (registry, rule);
    }

    [Fact]
    public void Evaluate_Fires_And_Applies_Action()
    {
        var (registry, rule) = Setup();
        var firings = AutomationEngine.Evaluate(registry, new[] { rule });
        Assert.Single(firings);
        Assert.Contains("plug.power=on", firings[0].Applied);
        Assert.True(registry.GetOrThrow("plug").State.Power);
        Assert.Single(registry.History("plug"));
    }

    [Fact]
    public void Evaluate_Skips_Disabled_And_Nonmatching_Rules()
    {
        var (registry, rule) = Setup(temp: 20);
        Assert.Empty(AutomationEngine.Evaluate(registry, new[] { rule }));
        var (_, hotRule) = Setup();
        hotRule.Enabled = false;
        var (registry2, _) = Setup();
        Assert.Empty(AutomationEngine.Evaluate(registry2, new[] { hotRule }));
    }

    [Fact]
    public void Evaluate_Respects_Cooldown_Then_Fires_Again()
    {
        var (registry, rule) = Setup();
        var now = DateTimeOffset.UtcNow;
        Assert.Single(AutomationEngine.Evaluate(registry, new[] { rule }, () => now));
        Assert.Empty(AutomationEngine.Evaluate(registry, new[] { rule }, () => now.AddSeconds(30)));
        Assert.Single(AutomationEngine.Evaluate(registry, new[] { rule }, () => now.AddSeconds(61)));
    }

    [Fact]
    public void Evaluate_Skips_Offline_Target_Without_Consuming_Cooldown()
    {
        var (registry, rule) = Setup(plugOnline: false);
        var now = DateTimeOffset.UtcNow;
        var firings = AutomationEngine.Evaluate(registry, new[] { rule }, () => now);
        Assert.Single(firings);
        Assert.Contains("plug: offline", firings[0].Skipped);
        Assert.Null(rule.LastFiredUtc);
        Assert.False(registry.GetOrThrow("plug").State.Power ?? false);

        registry.SetOnline("plug", true);
        var refired = AutomationEngine.Evaluate(registry, new[] { rule }, () => now.AddSeconds(5));
        Assert.Single(refired);
        Assert.True(registry.GetOrThrow("plug").State.Power);
    }

    [Fact]
    public void Evaluate_Ignores_Unknown_Trigger_And_Target()
    {
        var (registry, rule) = Setup();
        rule.When = RuleCondition.Parse("ghost:temperatureC:gt:25");
        Assert.Empty(AutomationEngine.Evaluate(registry, new[] { rule }));
        var (_, rule2) = Setup();
        rule2.Actions = new List<RuleAction> { RuleAction.Parse("ghost:power=on") };
        var firings = AutomationEngine.Evaluate(registry, new[] { rule2 });
        Assert.Single(firings);
        Assert.Contains("ghost: unknown device", firings[0].Skipped);
    }

    [Fact]
    public void RuleAction_Parse_Rejects_Empty_And_Out_Of_Range_Patches()
    {
        Assert.Throws<ArgumentException>(() => RuleAction.Parse("plug:"));
        Assert.ThrowsAny<ArgumentException>(() => RuleAction.Parse("plug:brightness=500")); // ArgumentOutOfRangeException from range validation
        Assert.Throws<ArgumentException>(() => RuleAction.Parse("plug:colour=red"));
    }
}

public sealed class RuleStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iot-hub-rules-" + Guid.NewGuid().ToString("N"));
    private string RulesPath => Path.Combine(_dir, "hub.rules.json");

    public RuleStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public void Load_Missing_File_Returns_Empty_And_Round_Trip_Preserves_Rules()
    {
        Assert.Empty(JsonRuleStore.Load(RulesPath));
        var rule = new AutomationRule
        {
            Id = "hot-fan",
            Name = "Fan on when hot",
            When = RuleCondition.Parse("thermo:temperatureC:gt:25"),
            Actions = new List<RuleAction> { RuleAction.Parse("plug:power=on") },
            CooldownSeconds = 30,
            LastFiredUtc = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero),
        };
        JsonRuleStore.Save(new[] { rule }, RulesPath);
        Assert.False(File.Exists(RulesPath + ".tmp"), "No temp file may be left behind after a save.");
        var loaded = JsonRuleStore.Load(RulesPath);
        Assert.Single(loaded);
        Assert.Equal("hot-fan", loaded[0].Id);
        Assert.Equal(30, loaded[0].CooldownSeconds);
        Assert.Equal(rule.LastFiredUtc, loaded[0].LastFiredUtc);
        Assert.Equal("plug:power=on", loaded[0].Actions[0].ToString());
    }

    [Fact]
    public void Load_Rejects_Duplicate_Ids()
    {
        var rule = new AutomationRule
        {
            Id = "dup",
            Name = "One",
            When = RuleCondition.Parse("thermo:temperatureC:gt:25"),
            Actions = new List<RuleAction> { RuleAction.Parse("plug:power=on") },
        };
        // Save bypasses the duplicate check (it validates single rules), so
        // write the duplicated file directly to test the load path, using
        // the store's own camelCase naming so the file round-trips.
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        File.WriteAllText(RulesPath, System.Text.Json.JsonSerializer.Serialize(new[] { rule, rule }, options));
        Assert.Throws<InvalidOperationException>(() => JsonRuleStore.Load(RulesPath));
    }

    [Fact]
    public void RulesPathFor_Derives_Sidecar_Name()
    {
        Assert.Equal(Path.Combine(_dir, "hub.rules.json"), JsonRuleStore.RulesPathFor(Path.Combine(_dir, "hub.json")));
    }
}

public sealed class CliRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iot-hub-cli-rules-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(_dir, "hub.json");

    public CliRuleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private void RegisterDevices(StringWriter output)
    {
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "thermo", "--name", "Thermostat", "--kind", "sensor" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "plug", "--name", "Plug", "--kind", "plug" }, output, Store));
    }

    [Fact]
    public void Cli_Rule_Add_And_Auto_Fire_On_SetState()
    {
        var output = new StringWriter();
        RegisterDevices(output);
        Assert.Equal(0, HubCli.Run(new[] { "rule-add", "--id", "hot-fan", "--name", "Fan on when hot",
            "--when", "thermo:temperatureC:gt:25", "--action", "plug:power=on", "--cooldown", "0" }, output, Store));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "set-state", "thermo", "--temperature", "26" }, output, Store));
        Assert.Contains("Rule fired: hot-fan", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "show", "plug" }, output, Store));
        Assert.Contains("power=on", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "rules" }, output, Store));
        Assert.Contains("hot-fan", output.ToString());
    }

    [Fact]
    public void Cli_Rule_Disable_Enable_And_Remove()
    {
        var output = new StringWriter();
        RegisterDevices(output);
        Assert.Equal(0, HubCli.Run(new[] { "rule-add", "--id", "hot-fan", "--name", "Fan on when hot",
            "--when", "thermo:temperatureC:gt:25", "--action", "plug:power=on", "--cooldown", "0" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "rule-disable", "hot-fan" }, output, Store));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "set-state", "thermo", "--temperature", "27" }, output, Store));
        Assert.DoesNotContain("Rule fired", output.ToString());

        Assert.Equal(0, HubCli.Run(new[] { "rule-enable", "hot-fan" }, output, Store));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "set-state", "thermo", "--temperature", "28" }, output, Store));
        Assert.Contains("Rule fired: hot-fan", output.ToString());

        Assert.Equal(0, HubCli.Run(new[] { "rule-remove", "hot-fan" }, output, Store));
        Assert.Equal(1, HubCli.Run(new[] { "rule-remove", "hot-fan" }, output, Store));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "run-rules" }, output, Store));
        Assert.Contains("No rules fired.", output.ToString());
    }

    [Fact]
    public void Cli_Rule_Add_Rejects_Unknown_Devices()
    {
        var output = new StringWriter();
        RegisterDevices(output);
        Assert.Equal(1, HubCli.Run(new[] { "rule-add", "--id", "ghost-rule", "--name", "Ghost",
            "--when", "ghost:temperatureC:gt:25", "--action", "plug:power=on" }, output, Store));
        Assert.Equal(1, HubCli.Run(new[] { "rule-add", "--id", "ghost-rule", "--name", "Ghost",
            "--when", "thermo:temperatureC:gt:25", "--action", "ghost:power=on" }, output, Store));
    }
}
