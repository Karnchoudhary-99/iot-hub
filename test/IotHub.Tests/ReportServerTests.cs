using Xunit;
using System.Text.Json;
using IotHub.Automation;
using IotHub.Devices;
using IotHub.Server;

namespace IotHub.Tests;

/// <summary>HTTP report coverage, driven through <see cref="HubApi"/>.</summary>
public sealed class ReportServerTests
{
    private static JsonElement Body(ApiResponse response) =>
        JsonDocument.Parse(response.Body).RootElement;

    [Fact]
    public void Report_Returns_Counts_And_Activity()
    {
        var registry = new DeviceRegistry();
        var api = new HubApi(registry, new List<AutomationRule>());
        Assert.Equal(201, api.Handle("POST", "/devices",
            """{"id":"lamp","name":"Lamp","kind":"light","group":"living-room"}""").Status);
        Assert.Equal(201, api.Handle("POST", "/devices",
            """{"id":"thermo","name":"Thermostat","kind":"thermostat","group":"hall"}""").Status);
        api.Handle("PATCH", "/devices/lamp/state", """{"power":true}""");
        api.Handle("POST", "/devices/thermo/offline", null);

        var response = api.Handle("GET", "/report", null);
        Assert.Equal(200, response.Status);
        var body = Body(response);
        Assert.Equal(2, body.GetProperty("totalDevices").GetInt32());
        Assert.Equal(1, body.GetProperty("onlineDevices").GetInt32());
        Assert.Equal(1, body.GetProperty("offlineDevices").GetInt32());
        Assert.Equal(1, body.GetProperty("totalStateChanges").GetInt32());
        Assert.Equal("lamp", body.GetProperty("mostChangedDevice").GetString());
        Assert.Equal(405, api.Handle("POST", "/report", null).Status);
    }
}
