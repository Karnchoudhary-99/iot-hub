using Xunit;
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
