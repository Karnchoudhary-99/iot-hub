using Xunit;
using System.IO;
using IotHub.Automation;
using IotHub.Devices;
using IotHub.Scenes;

namespace IotHub.Tests;

public sealed class SceneModelTests
{
    [Fact]
    public void Validate_Rejects_Bad_Id_Empty_Name_And_Empty_Steps()
    {
        Assert.Throws<ArgumentException>(() => new Scene { Id = "bad id", Name = "X", Steps = new List<RuleAction> { RuleAction.Parse("lamp:power=on") } }.Validate());
        Assert.Throws<ArgumentException>(() => new Scene { Id = "ok", Name = "", Steps = new List<RuleAction> { RuleAction.Parse("lamp:power=on") } }.Validate());
        Assert.Throws<ArgumentException>(() => new Scene { Id = "ok", Name = "X", Steps = new List<RuleAction>() }.Validate());
        Assert.ThrowsAny<ArgumentException>(() => new Scene { Id = "ok", Name = "X", Steps = new List<RuleAction> { RuleAction.Parse("lamp:brightness=500") } }.Validate());
    }

    [Fact]
    public void ToString_Lists_Steps_In_Order()
    {
        var scene = new Scene
        {
            Id = "goodnight",
            Name = "Goodnight",
            Steps = new List<RuleAction> { RuleAction.Parse("lamp:power=off"), RuleAction.Parse("door:locked=locked") },
        };
        Assert.Equal("goodnight \"Goodnight\" -> lamp:power=off; door:locked=locked", scene.ToString());
    }
}

public sealed class SceneApplierTests
{
    private static DeviceRegistry Setup(bool plugOnline = true)
    {
        var registry = new DeviceRegistry();
        registry.Register(new Device { Id = "lamp", Name = "Lamp", Kind = DeviceKind.Light, State = new DeviceState { Power = true, Brightness = 80 } });
        registry.Register(new Device { Id = "plug", Name = "Plug", Kind = DeviceKind.Plug, IsOnline = plugOnline, State = new DeviceState { Power = true } });
        return registry;
    }

    private static Scene Goodnight() => new()
    {
        Id = "goodnight",
        Name = "Goodnight",
        Steps = new List<RuleAction> { RuleAction.Parse("lamp:power=off"), RuleAction.Parse("plug:power=off") },
    };

    [Fact]
    public void Apply_Applies_Steps_In_Order_And_Records_History()
    {
        var registry = Setup();
        var result = SceneApplier.Apply(registry, Goodnight());
        Assert.Empty(result.Skipped);
        Assert.Contains("lamp.power=off", result.Applied);
        Assert.Contains("plug.power=off", result.Applied);
        Assert.False(registry.GetOrThrow("lamp").State.Power);
        Assert.Single(registry.History("lamp"));
    }

    [Fact]
    public void Apply_Skips_Offline_And_Unknown_Targets_Without_Marking_Online()
    {
        var registry = Setup(plugOnline: false);
        var result = SceneApplier.Apply(registry, Goodnight());
        Assert.Contains("plug: offline", result.Skipped);
        Assert.False(registry.GetOrThrow("plug").IsOnline);
        var withGhost = Goodnight();
        withGhost.Steps.Add(RuleAction.Parse("ghost:power=on"));
        var result2 = SceneApplier.Apply(registry, withGhost);
        Assert.Contains("ghost: unknown device", result2.Skipped);
    }

    [Fact]
    public void Apply_Reports_Already_Satisfied_Steps()
    {
        var registry = Setup();
        var scene = new Scene { Id = "noop", Name = "No-op", Steps = new List<RuleAction> { RuleAction.Parse("lamp:power=on") } };
        // Lamp starts on, so the step changes nothing — but the scene still
        // reports it instead of silently dropping it.
        registry.GetOrThrow("lamp").State.Power = true;
        var result = SceneApplier.Apply(registry, scene);
        Assert.Contains("lamp: already in target state", result.Applied);
    }
}

public sealed class CliSceneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iot-hub-cli-scenes-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(_dir, "hub.json");

    public CliSceneTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private void RegisterDevices(StringWriter output)
    {
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "lamp", "--name", "Lamp", "--kind", "light" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "door", "--name", "Door", "--kind", "lock" }, output, Store));
    }

    [Fact]
    public void Cli_Scene_Add_Apply_And_Remove()
    {
        var output = new StringWriter();
        RegisterDevices(output);
        Assert.Equal(0, HubCli.Run(new[] { "set-state", "lamp", "--power", "on" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "scene-add", "--id", "goodnight", "--name", "Goodnight",
            "--step", "lamp:power=off", "--step", "door:locked=locked" }, output, Store));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "scenes" }, output, Store));
        Assert.Contains("goodnight", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "scene-apply", "goodnight" }, output, Store));
        Assert.Contains("Scene applied: goodnight", output.ToString());
        Assert.Contains("applied: lamp.power=off", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "show", "lamp" }, output, Store));
        Assert.Contains("power=off", output.ToString());

        Assert.Equal(0, HubCli.Run(new[] { "scene-remove", "goodnight" }, output, Store));
        Assert.Equal(1, HubCli.Run(new[] { "scene-apply", "goodnight" }, output, Store));
    }

    [Fact]
    public void Cli_Scene_Add_Rejects_Unknown_Devices_And_Duplicates()
    {
        var output = new StringWriter();
        RegisterDevices(output);
        Assert.Equal(1, HubCli.Run(new[] { "scene-add", "--id", "bad", "--name", "Bad",
            "--step", "ghost:power=on" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "scene-add", "--id", "goodnight", "--name", "Goodnight",
            "--step", "lamp:power=off" }, output, Store));
        Assert.Equal(1, HubCli.Run(new[] { "scene-add", "--id", "goodnight", "--name", "Again",
            "--step", "lamp:power=on" }, output, Store));
    }
}

public sealed class SceneStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iot-hub-scenes-" + Guid.NewGuid().ToString("N"));
    private string ScenesPath => Path.Combine(_dir, "hub.scenes.json");

    public SceneStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public void Load_Missing_File_Returns_Empty_And_Round_Trip_Preserves_Scenes()
    {
        Assert.Empty(JsonSceneStore.Load(ScenesPath));
        var scene = new Scene
        {
            Id = "goodnight",
            Name = "Goodnight",
            Steps = new List<RuleAction> { RuleAction.Parse("lamp:power=off"), RuleAction.Parse("lamp:brightness=0") },
        };
        JsonSceneStore.Save(new[] { scene }, ScenesPath);
        Assert.False(File.Exists(ScenesPath + ".tmp"), "No temp file may be left behind after a save.");
        var loaded = JsonSceneStore.Load(ScenesPath);
        Assert.Single(loaded);
        Assert.Equal("goodnight", loaded[0].Id);
        Assert.Equal(2, loaded[0].Steps.Count);
        Assert.Equal("lamp:power=off", loaded[0].Steps[0].ToString());
    }

    [Fact]
    public void Load_Rejects_Duplicate_Ids()
    {
        var scene = new Scene { Id = "dup", Name = "One", Steps = new List<RuleAction> { RuleAction.Parse("lamp:power=on") } };
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        File.WriteAllText(ScenesPath, System.Text.Json.JsonSerializer.Serialize(new[] { scene, scene }, options));
        Assert.Throws<InvalidOperationException>(() => JsonSceneStore.Load(ScenesPath));
    }

    [Fact]
    public void ScenesPathFor_Derives_Sidecar_Name()
    {
        Assert.Equal(Path.Combine(_dir, "hub.scenes.json"), JsonSceneStore.ScenesPathFor(Path.Combine(_dir, "hub.json")));
    }
}
