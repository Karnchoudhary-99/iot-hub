using Xunit;
using System.IO;
using IotHub.Devices;

namespace IotHub.Tests;

public sealed class GroupControlTests
{
    private static DeviceRegistry Setup(bool secondOnline = true)
    {
        var registry = new DeviceRegistry();
        registry.Register(new Device { Id = "lamp1", Name = "Lamp 1", Kind = DeviceKind.Light, Group = "living-room", State = new DeviceState { Power = true } });
        registry.Register(new Device { Id = "lamp2", Name = "Lamp 2", Kind = DeviceKind.Light, Group = "living-room", IsOnline = secondOnline, State = new DeviceState { Power = true } });
        registry.Register(new Device { Id = "hall-lamp", Name = "Hall lamp", Kind = DeviceKind.Light, Group = "hall", State = new DeviceState { Power = true } });
        return registry;
    }

    [Fact]
    public void ApplyToGroup_Updates_Only_That_Group()
    {
        var registry = Setup();
        var result = GroupControl.ApplyToGroup(registry, "living-room", new DeviceState { Power = false });
        Assert.Equal(2, result.Applied.Count);
        Assert.Empty(result.Skipped);
        Assert.False(registry.GetOrThrow("lamp1").State.Power);
        Assert.False(registry.GetOrThrow("lamp2").State.Power);
        Assert.True(registry.GetOrThrow("hall-lamp").State.Power);
    }

    [Fact]
    public void ApplyToGroup_Skips_Offline_Without_Marking_Online()
    {
        var registry = Setup(secondOnline: false);
        var result = GroupControl.ApplyToGroup(registry, "living-room", new DeviceState { Power = false });
        Assert.Contains("lamp2: offline", result.Skipped);
        Assert.False(registry.GetOrThrow("lamp2").IsOnline);
        Assert.True(registry.GetOrThrow("lamp2").State.Power ?? false);
    }

    [Fact]
    public void ApplyToGroup_Rejects_Unknown_Group_And_Invalid_Patch()
    {
        var registry = Setup();
        Assert.Throws<KeyNotFoundException>(() => GroupControl.ApplyToGroup(registry, "attic", new DeviceState { Power = false }));
        Assert.ThrowsAny<ArgumentException>(() => GroupControl.ApplyToGroup(registry, "living-room", new DeviceState { Brightness = 500 }));
    }
}

public sealed class CliGroupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iot-hub-cli-groups-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(_dir, "hub.json");

    public CliGroupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public void Cli_Group_Set_Updates_Group_And_Reports_Skips()
    {
        var output = new StringWriter();
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "lamp1", "--name", "Lamp 1", "--kind", "light", "--group", "living-room" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "lamp2", "--name", "Lamp 2", "--kind", "light", "--group", "living-room" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "hall-lamp", "--name", "Hall lamp", "--kind", "light", "--group", "hall" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "set-state", "lamp1", "--power", "on" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "set-state", "lamp2", "--power", "on" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "offline", "lamp2" }, output, Store));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "group-set", "living-room", "--power", "off" }, output, Store));
        Assert.Contains("Group updated: living-room", output.ToString());
        Assert.Contains("applied: lamp1.power=off", output.ToString());
        Assert.Contains("skipped: lamp2: offline", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "show", "hall-lamp" }, output, Store));
        Assert.DoesNotContain("power=off", output.ToString());

        Assert.Equal(1, HubCli.Run(new[] { "group-set", "attic", "--power", "off" }, output, Store));
    }
}
