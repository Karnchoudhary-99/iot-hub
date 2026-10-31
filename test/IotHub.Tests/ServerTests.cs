using Xunit;
using System.Text.Json;
using IotHub.Automation;
using IotHub.Devices;
using IotHub.Server;

namespace IotHub.Tests;

/// <summary>Tests drive <see cref="HubApi"/> directly — the same routing the
/// socket server uses, without binding a port.</summary>
public sealed class ServerTests
{
    private static (HubApi Api, DeviceRegistry Registry, List<AutomationRule> Rules) Hub()
    {
        var registry = new DeviceRegistry();
        var rules = new List<AutomationRule>();
        return (new HubApi(registry, rules), registry, rules);
    }

    private static JsonElement Body(ApiResponse response) =>
        JsonDocument.Parse(response.Body).RootElement;

    private static void SeedLamp(HubApi api)
    {
        var created = api.Handle("POST", "/devices",
            """{"id":"lamp","name":"Lamp","kind":"light","group":"living-room"}""");
        Assert.Equal(201, created.Status);
    }

    [Fact]
    public void Health_Reports_Counts()
    {
        var (api, _, _) = Hub();
        SeedLamp(api);
        var response = api.Handle("GET", "/health", null);
        Assert.Equal(200, response.Status);
        Assert.Equal("ok", Body(response).GetProperty("status").GetString());
        Assert.Equal(1, Body(response).GetProperty("devices").GetInt32());
    }

    [Fact]
    public void Register_Get_List_And_Filters_Round_Trip()
    {
        var (api, _, _) = Hub();
        SeedLamp(api);
        api.Handle("POST", "/devices", """{"id":"thermo","name":"Thermostat","kind":"thermostat","group":"hall"}""");

        Assert.Equal(200, api.Handle("GET", "/devices/lamp", null).Status);
        Assert.Equal("Lamp", Body(api.Handle("GET", "/devices/lamp", null)).GetProperty("name").GetString());

        var all = Body(api.Handle("GET", "/devices", null));
        Assert.Equal(2, all.GetArrayLength());
        var hall = Body(api.Handle("GET", "/devices?group=hall", null));
        Assert.Equal(1, hall.GetArrayLength());
        Assert.Equal("thermo", hall[0].GetProperty("id").GetString());
        var lights = Body(api.Handle("GET", "/devices?kind=light", null));
        Assert.Equal(1, lights.GetArrayLength());
        var online = Body(api.Handle("GET", "/devices?online=true", null));
        Assert.Equal(2, online.GetArrayLength());
        Assert.Equal(400, api.Handle("GET", "/devices?kind=toaster", null).Status);
        Assert.Equal(400, api.Handle("GET", "/devices?online=maybe", null).Status);
    }

    [Fact]
    public void Duplicate_Unknown_And_Malformed_Return_409_404_400()
    {
        var (api, _, _) = Hub();
        SeedLamp(api);
        Assert.Equal(409, api.Handle("POST", "/devices",
            """{"id":"lamp","name":"Again","kind":"light"}""").Status);
        Assert.Equal(404, api.Handle("GET", "/devices/nope", null).Status);
        Assert.Equal(400, api.Handle("POST", "/devices", """{"id":"x"}""").Status);
        Assert.Equal(400, api.Handle("POST", "/devices", """not json""").Status);
        Assert.Equal(404, api.Handle("GET", "/nope", null).Status);
        Assert.Equal(405, api.Handle("DELETE", "/health", null).Status);
    }

    [Fact]
    public void Patch_State_Updates_History_And_Rejects_Out_Of_Range()
    {
        var (api, registry, _) = Hub();
        SeedLamp(api);

        var patched = api.Handle("PATCH", "/devices/lamp/state",
            """{"power":true,"brightness":70}""");
        Assert.Equal(200, patched.Status);
        var changes = Body(patched).GetProperty("changes");
        Assert.Equal(2, changes.GetArrayLength());

        var history = Body(api.Handle("GET", "/devices/lamp/history", null));
        Assert.Equal(2, history.GetArrayLength());
        var limited = Body(api.Handle("GET", "/devices/lamp/history?limit=1", null));
        Assert.Equal(1, limited.GetArrayLength());

        var bad = api.Handle("PATCH", "/devices/lamp/state", """{"brightness":140}""");
        Assert.Equal(400, bad.Status);
        Assert.Equal(70, registry.GetOrThrow("lamp").State.Brightness);
        Assert.Equal(404, api.Handle("PATCH", "/devices/nope/state", """{"power":true}""").Status);
        Assert.Equal(400, api.Handle("PATCH", "/devices/lamp/state", """{"sparkle":1}""").Status);
    }

    [Fact]
    public void Patch_State_Fires_Rules_Like_The_Cli()
    {
        var (api, registry, _) = Hub();
        api.Handle("POST", "/devices", """{"id":"thermo","name":"Thermostat","kind":"sensor","group":"hall"}""");
        api.Handle("POST", "/devices", """{"id":"fan","name":"Fan plug","kind":"plug","group":"hall"}""");
        var rule = api.Handle("POST", "/rules",
            """{"id":"hot-fan","name":"Fan on when hot","when":"thermo:temperatureC:gt:25","actions":["fan:power=on"],"cooldownSeconds":0}""");
        Assert.Equal(201, rule.Status);

        var patched = api.Handle("PATCH", "/devices/thermo/state", """{"temperatureC":27}""");
        var firings = Body(patched).GetProperty("firings");
        Assert.Equal(1, firings.GetArrayLength());
        Assert.Equal("hot-fan", firings[0].GetProperty("ruleId").GetString());
        Assert.True(registry.GetOrThrow("fan").State.Power);
    }

    [Fact]
    public void Rules_Create_List_Enable_Disable_Delete()
    {
        var (api, _, _) = Hub();
        SeedLamp(api);
        api.Handle("POST", "/devices", """{"id":"thermo","name":"Thermostat","kind":"sensor"}""");

        var created = api.Handle("POST", "/rules",
            """{"id":"hot","name":"Hot rule","when":"thermo:temperatureC:gt:25","actions":["lamp:power=on"]}""");
        Assert.Equal(201, created.Status);
        Assert.Equal(409, api.Handle("POST", "/rules",
            """{"id":"hot","name":"Again","when":"thermo:temperatureC:gt:25","actions":["lamp:power=on"]}""").Status);
        Assert.Equal(404, api.Handle("POST", "/rules",
            """{"id":"ghost","name":"Ghost","when":"nope:temperatureC:gt:25","actions":["lamp:power=on"]}""").Status);
        Assert.Equal(400, api.Handle("POST", "/rules",
            """{"id":"bad","name":"Bad","when":"thermo:temperatureC:gt:25","actions":[]}""").Status);

        Assert.Equal(1, Body(api.Handle("GET", "/rules", null)).GetArrayLength());
        var disabled = api.Handle("POST", "/rules/hot/disable", null);
        Assert.Equal(200, disabled.Status);
        Assert.False(Body(disabled).GetProperty("enabled").GetBoolean());
        Assert.Equal(200, api.Handle("POST", "/rules/hot/enable", null).Status);
        Assert.Equal(200, api.Handle("DELETE", "/rules/hot", null).Status);
        Assert.Equal(404, api.Handle("DELETE", "/rules/hot", null).Status);
    }

    [Fact]
    public void RunRules_Endpoint_And_Online_Offline_And_Groups()
    {
        var (api, registry, _) = Hub();
        SeedLamp(api);
        api.Handle("POST", "/devices", """{"id":"thermo","name":"Thermostat","kind":"sensor","group":"hall"}""");
        api.Handle("POST", "/rules",
            """{"id":"hot","name":"Hot rule","when":"thermo:temperatureC:gt:25","actions":["lamp:power=on"],"cooldownSeconds":0}""");
        api.Handle("PATCH", "/devices/thermo/state", """{"temperatureC":20}""");

        var run = api.Handle("POST", "/run-rules", null);
        Assert.Equal(200, run.Status);
        Assert.Equal(0, Body(run).GetProperty("firings").GetArrayLength());

        api.Handle("PATCH", "/devices/thermo/state", """{"temperatureC":26}""");
        Assert.True(registry.GetOrThrow("lamp").State.Power);

        var off = api.Handle("POST", "/devices/lamp/offline", null);
        Assert.False(Body(off).GetProperty("isOnline").GetBoolean());
        var on = api.Handle("POST", "/devices/lamp/online", null);
        Assert.True(Body(on).GetProperty("isOnline").GetBoolean());

        var groups = Body(api.Handle("GET", "/groups", null)).GetProperty("groups");
        Assert.Equal(2, groups.GetArrayLength()); // hall + living-room
        Assert.Equal(200, api.Handle("DELETE", "/devices/lamp", null).Status);
        Assert.Equal(404, api.Handle("GET", "/devices/lamp", null).Status);
    }

    [Fact]
    public void Persist_Callback_Runs_On_Mutation()
    {
        var registry = new DeviceRegistry();
        var persisted = 0;
        var api = new HubApi(registry, new List<AutomationRule>(), () => persisted++);
        api.Handle("POST", "/devices", """{"id":"lamp","name":"Lamp"}""");
        api.Handle("GET", "/devices", null);
        Assert.Equal(1, persisted);
        api.Handle("PATCH", "/devices/lamp/state", """{"power":true}""");
        Assert.Equal(2, persisted);
    }
}
