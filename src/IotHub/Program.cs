using IotHub;

// IoT Home Automation Hub — control CLI (see HubCli for the commands).
// The store file defaults to ./iot-hub.json; override with IOT_HUB_STORE.
var storePath = Environment.GetEnvironmentVariable("IOT_HUB_STORE") ?? "iot-hub.json";
return HubCli.Run(args, Console.Out, storePath);
