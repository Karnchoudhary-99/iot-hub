using Xunit;
using IotHub.Automation;
using IotHub.Devices;
using IotHub.Reporting;
using IotHub.Scenes;

namespace IotHub.Tests;

public sealed class ReportTests
{
    private static DeviceRegistry Setup()
    {
        var registry = new DeviceRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Register(new Device { Id = "lamp1", Name = "Lamp 1", Kind = DeviceKind.Light, Group = "living-room", LastSeenUtc = now });
        registry.Register(new Device { Id = "lamp2", Name = "Lamp 2", Kind = DeviceKind.Light, Group = "living-room", IsOnline = false, LastSeenUtc = now.AddHours(-5) });
        registry.Register(new Device { Id = "thermo", Name = "Thermostat", Kind = DeviceKind.Thermostat, Group = "hall", LastSeenUtc = now.AddHours(-3) });
        registry.UpdateState("lamp1", new DeviceState { Power = true });
        registry.UpdateState("lamp1", new DeviceState { Brightness = 50 });
        registry.UpdateState("thermo", new DeviceState { TemperatureC = 22 });
        // UpdateState refreshes last-seen, so rewind thermo afterwards: it
        // reported once, then went silent for 3 hours.
        registry.GetOrThrow("thermo").LastSeenUtc = now.AddHours(-3);
        return registry;
    }

    [Fact]
    public void Build_Counts_Groups_Kinds_Online_And_Stale()
    {
        var registry = Setup();
        var now = DateTimeOffset.UtcNow;
        var report = HubReport.Build(registry, new List<AutomationRule>(), new List<Scene>(), TimeSpan.FromHours(1), () => now);
        Assert.Equal(3, report.TotalDevices);
        Assert.Equal(2, report.OnlineDevices);
        Assert.Equal(1, report.OfflineDevices);
        Assert.Equal(2, report.DevicesByGroup["living-room"]);
        Assert.Equal(1, report.DevicesByGroup["hall"]);
        Assert.Equal(2, report.DevicesByKind["Light"]);
        Assert.Equal(1, report.DevicesByKind["Thermostat"]);
        // thermo is online but silent for 3h > 1h window; lamp2 is offline,
        // so it is not "stale" — it is already known to be down.
        Assert.Equal(new[] { "thermo" }, report.StaleDevices);
    }

    [Fact]
    public void WithHistory_Fills_Activity_And_Most_Changed_Device()
    {
        var registry = Setup();
        var report = HubReport.Build(registry, new List<AutomationRule>(), new List<Scene>(), TimeSpan.FromHours(1))
            .WithHistory(registry);
        Assert.Equal(3, report.TotalStateChanges);
        Assert.Equal(2, report.ChangesByDevice["lamp1"]);
        Assert.Equal(1, report.ChangesByDevice["thermo"]);
        Assert.Equal("lamp1", report.MostChangedDevice);
    }

    [Fact]
    public void Formatter_Omits_Empty_Sections_And_Lists_Stale()
    {
        var registry = Setup();
        var now = DateTimeOffset.UtcNow;
        var report = HubReport.Build(registry, new List<AutomationRule>(), new List<Scene>(), TimeSpan.FromHours(1), () => now)
            .WithHistory(registry);
        var text = HubReportFormatter.ToText(report);
        Assert.Contains("Devices: 3 total, 2 online, 1 offline", text);
        Assert.Contains("Group living-room: 2", text);
        Assert.Contains("Most active device: lamp1 (2 changes)", text);
        Assert.Contains("Stale devices (silent over the window): thermo", text);
        Assert.DoesNotContain("Automation:", text); // no rules, no scenes
    }

    [Fact]
    public void Empty_Hub_Reports_Zeroes_Without_Crashing()
    {
        var registry = new DeviceRegistry();
        var report = HubReport.Build(registry, new List<AutomationRule>(), new List<Scene>(), TimeSpan.FromHours(1))
            .WithHistory(registry);
        Assert.Equal(0, report.TotalDevices);
        Assert.Null(report.MostChangedDevice);
        Assert.Contains("Devices: 0 total", HubReportFormatter.ToText(report));
    }
}
