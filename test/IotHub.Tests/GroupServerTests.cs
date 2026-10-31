using Xunit;
using System.Text.Json;
using IotHub.Automation;
using IotHub.Devices;
using IotHub.Server;

namespace IotHub.Tests;

/// <summary>HTTP group-control coverage, driven through <see cref="HubApi"/>.</summary>
public sealed class GroupServerTests
{
    private static JsonElement Body(ApiResponse response) =>
        JsonDocument.Parse(response.Body).RootElement;

    [Fact]
    public void Group_State_Patch_Updates_Group_And_Reports_Skips()
    {
        var registry = new DeviceRegistry();
        var api = new HubApi(registry, new List<AutomationRule>());
        Assert.Equal(201, api.Handle("POST", "/devices",
            """{"id":"lamp1","name":"Lamp 1","kind":"light","group":"living-room"}""").Status);
        Assert.Equal(201, api.Handle("POST", "/devices",
            """{"id":"lamp2","name":"Lamp 2","kind":"light","group":"living-room"}""").Status);
        Assert.Equal(201, api.Handle("POST", "/devices",
            """{"id":"hall-lamp","name":"Hall lamp","kind":"light","group":"hall"}""").Status);
        api.Handle("PATCH", "/devices/lamp1/state", """{"power":true}""");
        api.Handle("PATCH", "/devices/lamp2/state", """{"power":true}""");
        api.Handle("POST", "/devices/lamp2/offline", null);

        var response = api.Handle("PATCH", "/groups/living-room/state", """{"power":false}""");
        Assert.Equal(200, response.Status);
        var body = Body(response);
        Assert.Contains("lamp1.power=off", body.GetProperty("applied").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("lamp2: offline", body.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()));
        Assert.False(registry.GetOrThrow("lamp1").State.Power);
        Assert.True(registry.GetOrThrow("lamp2").State.Power ?? false);

        Assert.Equal(404, api.Handle("PATCH", "/groups/attic/state", """{"power":false}""").Status);
        Assert.Equal(400, api.Handle("PATCH", "/groups/living-room/state", """{"brightness":500}""").Status);
        Assert.Equal(405, api.Handle("GET", "/groups/living-room/state", null).Status);
    }
}
