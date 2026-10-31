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
        Assert.Throws<ArgumentException>(() => new Scene { Id = "ok", Name = "X", Steps = new List<RuleAction> { RuleAction.Parse("lamp:brightness=500") } }.Validate());
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
