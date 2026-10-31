using Xunit;
using IotHub.Devices;

namespace IotHub.Tests;

public sealed class DeviceStateTests
{
    [Theory]
    [InlineData(101, null, null)]
    [InlineData(-1, null, null)]
    [InlineData(null, 61.0, null)]
    [InlineData(null, -51.0, null)]
    [InlineData(null, null, 100.5)]
    [InlineData(null, null, -0.5)]
    public void Validate_Rejects_Out_Of_Range_Values(int? brightness, double? temperature, double? humidity)
    {
        var state = new DeviceState { Brightness = brightness, TemperatureC = temperature, HumidityPct = humidity };
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Validate());
    }

    [Fact]
    public void Validate_Accepts_Boundary_Values()
    {
        new DeviceState { Brightness = 0, TemperatureC = -50, HumidityPct = 100 }.Validate();
        new DeviceState { Brightness = 100, TemperatureC = 60, HumidityPct = 0 }.Validate();
    }

    [Fact]
    public void WithPatch_Merges_Only_Non_Null_Fields()
    {
        var base_ = new DeviceState { Power = true, Brightness = 40, TemperatureC = 21.5 };
        var merged = base_.WithPatch(new DeviceState { Brightness = 80 });
        Assert.True(merged.Power);
        Assert.Equal(80, merged.Brightness);
        Assert.Equal(21.5, merged.TemperatureC);
        Assert.Equal(40, base_.Brightness); // the original is untouched
    }

    [Fact]
    public void Diff_Reports_Changed_Fields_With_Readable_Values()
    {
        var before = new DeviceState { Power = true, Locked = true };
        var after = new DeviceState { Power = false, Locked = false };
        var diff = before.Diff(after).ToList();
        Assert.Contains(diff, d => d.Field == "power" && d.Old == "on" && d.New == "off");
        Assert.Contains(diff, d => d.Field == "locked" && d.Old == "locked" && d.New == "unlocked");
    }

    [Fact]
    public void Device_Validate_Rejects_Bad_Ids_And_Empty_Names()
    {
        Assert.Throws<ArgumentException>(() => new Device { Id = "bad id", Name = "Lamp" }.Validate());
        Assert.Throws<ArgumentException>(() => new Device { Id = "lamp", Name = "  " }.Validate());
        new Device { Id = "living-room_lamp-1", Name = "Lamp" }.Validate();
    }
}
