using IotHub.Devices;

namespace IotHub.Tests;

public sealed class RegistryTests
{
    private static Device Lamp(string id = "lamp", string group = "living-room") =>
        new() { Id = id, Name = "Lamp", Kind = DeviceKind.Light, Group = group };

    [Fact]
    public void Register_Rejects_Duplicate_Ids()
    {
        var registry = new DeviceRegistry();
        registry.Register(Lamp());
        Assert.Throws<InvalidOperationException>(() => registry.Register(Lamp()));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Get_Returns_Null_For_Unknown_And_GetOrThrow_Throws()
    {
        var registry = new DeviceRegistry();
        Assert.Null(registry.Get("nope"));
        Assert.Throws<KeyNotFoundException>(() => registry.GetOrThrow("nope"));
    }

    [Fact]
    public void List_Filters_And_Orders_By_Group_Then_Name()
    {
        var registry = new DeviceRegistry();
        registry.Register(new Device { Id = "b", Name = "Beta", Kind = DeviceKind.Sensor, Group = "kitchen" });
        registry.Register(Lamp("a", "living-room"));
        registry.Register(new Device { Id = "c", Name = "Alpha", Kind = DeviceKind.Light, Group = "living-room" });
        Assert.Equal(new[] { "b", "c", "a" }, registry.List().Select(d => d.Id));
        Assert.Equal(new[] { "c", "a" }, registry.List(group: "living-room").Select(d => d.Id));
        Assert.Equal(new[] { "a", "c" }, registry.List(kind: DeviceKind.Light).Select(d => d.Id).OrderBy(x => x));
        registry.SetOnline("a", false);
        Assert.Equal(new[] { "c" }, registry.List(group: "living-room", onlineOnly: true).Select(d => d.Id));
    }

    [Fact]
    public void Groups_Are_Distinct_And_Sorted()
    {
        var registry = new DeviceRegistry();
        registry.Register(Lamp("a", "bedroom"));
        registry.Register(Lamp("b", "living-room"));
        registry.Register(Lamp("c", "bedroom"));
        registry.Register(Lamp("d", ""));
        Assert.Equal(new[] { "bedroom", "living-room" }, registry.Groups());
    }

    [Fact]
    public void UpdateState_Records_Only_Changed_Fields_And_Marks_Online()
    {
        var registry = new DeviceRegistry();
        registry.Register(Lamp());
        registry.SetOnline("lamp", false);
        var changes = registry.UpdateState("lamp", new DeviceState { Power = true, Brightness = 70 });
        Assert.Equal(2, changes.Count);
        Assert.True(registry.GetOrThrow("lamp").IsOnline);
        Assert.Empty(registry.UpdateState("lamp", new DeviceState { Power = true, Brightness = 70 }));
        Assert.Equal(2, registry.History("lamp").Count);
    }

    [Fact]
    public void UpdateState_With_Invalid_Patch_Leaves_State_Untouched()
    {
        var registry = new DeviceRegistry();
        registry.Register(Lamp());
        registry.UpdateState("lamp", new DeviceState { Brightness = 50 });
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.UpdateState("lamp", new DeviceState { Brightness = 500 }));
        Assert.Equal(50, registry.GetOrThrow("lamp").State.Brightness);
        Assert.Single(registry.History("lamp"));
    }

    [Fact]
    public void Remove_Drops_The_Device_But_Keeps_Its_History()
    {
        var registry = new DeviceRegistry();
        registry.Register(Lamp());
        registry.UpdateState("lamp", new DeviceState { Power = true });
        Assert.True(registry.Remove("lamp"));
        Assert.False(registry.Remove("lamp"));
        Assert.Null(registry.Get("lamp"));
        Assert.Single(registry.History("lamp"));
    }
}
