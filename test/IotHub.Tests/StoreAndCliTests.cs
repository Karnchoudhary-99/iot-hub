using System.IO;
using IotHub.Devices;

namespace IotHub.Tests;

public sealed class StoreAndCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iot-hub-tests-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(_dir, "hub.json");

    public StoreAndCliTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public void Load_Missing_File_Returns_Empty_Registry()
    {
        Assert.Equal(0, JsonRegistryStore.Load(Store).Count);
    }

    [Fact]
    public void Save_Load_Round_Trip_Preserves_Devices_And_History()
    {
        var registry = new DeviceRegistry();
        registry.Register(new Device { Id = "thermo", Name = "Thermostat", Kind = DeviceKind.Thermostat, Group = "hall" });
        registry.UpdateState("thermo", new DeviceState { TemperatureC = 21.5, Power = true });
        JsonRegistryStore.Save(registry, Store);
        Assert.False(File.Exists(Store + ".tmp"), "No temp file may be left behind after a save.");

        var loaded = JsonRegistryStore.Load(Store);
        var device = loaded.GetOrThrow("thermo");
        Assert.Equal("Thermostat", device.Name);
        Assert.Equal(DeviceKind.Thermostat, device.Kind);
        Assert.Equal(21.5, device.State.TemperatureC);
        Assert.True(device.State.Power);
        Assert.Equal(2, loaded.History("thermo").Count);
    }

    [Fact]
    public void Cli_Register_List_SetState_History_Round_Trip()
    {
        var output = new StringWriter();
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "lamp", "--name", "Lamp", "--kind", "light", "--group", "living-room" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "set-state", "lamp", "--power", "on", "--brightness", "70" }, output, Store));
        Assert.Equal(0, HubCli.Run(new[] { "offline", "lamp" }, output, Store));
        // A state update marks the device online again.
        Assert.Equal(0, HubCli.Run(new[] { "set-state", "lamp", "--brightness", "80" }, output, Store));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "show", "lamp" }, output, Store));
        Assert.Contains("online", output.ToString());
        Assert.Contains("brightness=80", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "history", "lamp" }, output, Store));
        Assert.Contains("brightness", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, HubCli.Run(new[] { "list", "--group", "living-room" }, output, Store));
        Assert.Contains("lamp", output.ToString());
    }

    [Fact]
    public void Cli_Errors_Use_Distinct_Exit_Codes()
    {
        var output = new StringWriter();
        Assert.Equal(2, HubCli.Run(new[] { "frobnicate" }, output, Store)); // unknown command: usage error
        Assert.Equal(2, HubCli.Run(new[] { "show" }, output, Store)); // missing id: usage error
        Assert.Equal(1, HubCli.Run(new[] { "show", "ghost" }, output, Store)); // unknown device: runtime error
        Assert.Equal(0, HubCli.Run(new[] { "register", "--id", "lamp", "--name", "Lamp" }, output, Store));
        Assert.Equal(1, HubCli.Run(new[] { "register", "--id", "lamp", "--name", "Lamp2" }, output, Store)); // duplicate
        Assert.Equal(1, HubCli.Run(new[] { "set-state", "lamp", "--brightness", "500" }, output, Store)); // invalid value
    }
}
