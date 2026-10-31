using Xunit;
using System.Text.Json;
using IotHub.Automation;
using IotHub.Devices;
using IotHub.Scenes;
using IotHub.Server;

namespace IotHub.Tests;

/// <summary>HTTP scene coverage, driven through <see cref="HubApi"/> directly
/// like the other server tests — no socket binding.</summary>
public sealed class SceneServerTests
{
    private static (HubApi Api, DeviceRegistry Registry, List<Scene> Scenes) Hub()
    {
        var registry = new DeviceRegistry();
        var scenes = new List<Scene>();
        return (new HubApi(registry, new List<AutomationRule>(), null, scenes), registry, scenes);
    }

    private static JsonElement Body(ApiResponse response) =>
        JsonDocument.Parse(response.Body).RootElement;

    private static void SeedLamp(HubApi api)
    {
        Assert.Equal(201, api.Handle("POST", "/devices",
            """{"id":"lamp","name":"Lamp","kind":"light"}""").Status);
    }

    [Fact]
    public void Scenes_Create_List_Apply_And_Delete_Round_Trip()
    {
        var (api, registry, _) = Hub();
        SeedLamp(api);
        var created = api.Handle("POST", "/scenes",
            """{"id":"goodnight","name":"Goodnight","steps":["lamp:power=off"]}""");
        Assert.Equal(201, created.Status);
        Assert.Equal("goodnight", Body(created).GetProperty("id").GetString());

        var all = Body(api.Handle("GET", "/scenes", null));
        Assert.Single(all.EnumerateArray());
        Assert.Equal("lamp:power=off", all[0].GetProperty("steps")[0].GetString());

        registry.GetOrThrow("lamp").State.Power = true;
        var applied = api.Handle("POST", "/scenes/goodnight/apply", null);
        Assert.Equal(200, applied.Status);
        Assert.Contains("lamp.power=off", Body(applied).GetProperty("applied").EnumerateArray().Select(e => e.GetString()));

        Assert.Equal(200, api.Handle("DELETE", "/scenes/goodnight", null).Status);
        Assert.Equal(404, api.Handle("POST", "/scenes/goodnight/apply", null).Status);
        Assert.Equal(404, api.Handle("DELETE", "/scenes/goodnight", null).Status);
    }

    [Fact]
    public void Scenes_Reject_Duplicates_Unknown_Targets_And_Bad_Steps()
    {
        var (api, _, _) = Hub();
        SeedLamp(api);
        Assert.Equal(201, api.Handle("POST", "/scenes",
            """{"id":"goodnight","name":"Goodnight","steps":["lamp:power=off"]}""").Status);
        Assert.Equal(409, api.Handle("POST", "/scenes",
            """{"id":"goodnight","name":"Again","steps":["lamp:power=on"]}""").Status);
        Assert.Equal(404, api.Handle("POST", "/scenes",
            """{"id":"ghost","name":"Ghost","steps":["ghost:power=on"]}""").Status);
        Assert.Equal(400, api.Handle("POST", "/scenes",
            """{"id":"bad","name":"Bad","steps":["lamp:colour=red"]}""").Status);
        Assert.Equal(400, api.Handle("POST", "/scenes",
            """{"id":"bad","name":"Bad"}""").Status);
        Assert.Equal(405, api.Handle("PUT", "/scenes", null).Status);
    }
}
