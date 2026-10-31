using System.Net;
using System.Text;
using IotHub.Automation;
using IotHub.Devices;

namespace IotHub.Server;

/// <summary>
/// The thin socket adapter over <see cref="HubApi"/>: binds an
/// <c>HttpListener</c> on localhost, loads the registry and rules from the
/// store files once, and persists after every mutation so the HTTP view and
/// the CLI view share one store. All routing, validation and rule firing
/// live in <see cref="HubApi"/> and are tested without a socket.
/// Stops cleanly on Ctrl-C.
/// </summary>
public static class HubServer
{
    public static int Run(string[] args, TextWriter output, string storePath)
    {
        var port = 8080;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length)
                port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else
            {
                output.WriteLine("Usage: serve [--port N]  (default port 8080)");
                return 2;
            }
        }

        var registry = JsonRegistryStore.Load(storePath);
        var rulesPath = JsonRuleStore.RulesPathFor(storePath);
        var rules = JsonRuleStore.Load(rulesPath);
        var api = new HubApi(registry, rules, () =>
        {
            JsonRegistryStore.Save(registry, storePath);
            JsonRuleStore.Save(rules, rulesPath);
        });

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            output.WriteLine($"Error: could not listen on localhost:{port}: {ex.Message}");
            return 1;
        }
        output.WriteLine($"IoT hub server listening on http://localhost:{port}/ (Ctrl-C to stop)");

        var stopping = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping = true;
            listener.Stop();
        };

        try
        {
            while (!stopping)
            {
                HttpListenerContext context;
                try { context = listener.GetContext(); }
                catch (HttpListenerException) { break; } // stopped via Ctrl-C
                var request = context.Request;
                string? body = null;
                if (request.HasEntityBody)
                {
                    using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                    body = reader.ReadToEnd();
                }
                var response = api.Handle(request.HttpMethod, request.RawUrl ?? "/", body);
                var bytes = Encoding.UTF8.GetBytes(response.Body);
                context.Response.StatusCode = response.Status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.Close();
                output.WriteLine($"{request.HttpMethod} {request.RawUrl} -> {response.Status}");
            }
        }
        finally
        {
            listener.Close();
        }
        output.WriteLine("Hub server stopped.");
        return 0;
    }
}
