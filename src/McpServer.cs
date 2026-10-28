using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DosboxEnhanced;

public sealed record McpSettings
{
    public bool Enabled { get; init; }
    public bool AllowRemote { get; init; }
    public bool AllowControl { get; init; } = true;
    public int Port { get; init; } = 8387;
    public string Token { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public string Url => $"http://127.0.0.1:{Port}/mcp";
    public void Validate()
    {
        if (Port is < 1024 or > 65535) throw new ArgumentException("Choose a port from 1024 to 65535.");
        if (Token.Length != 64 || !Token.All(Uri.IsHexDigit)) throw new ArgumentException("The connection key is invalid. Generate a new key in MCP settings.");
    }
}

public sealed class McpServer : IDisposable
{
    public static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    readonly MainWindow window;
    readonly SemaphoreSlim configurationGate = new(1);
    readonly SemaphoreSlim toolGate = new(1);
    readonly ConcurrentDictionary<string, Session> sessions = new();
    WebApplication? app;
    CancellationTokenSource? lifetime;
    public McpSettings Settings { get; private set; } = new();
    public string Status { get; private set; } = "MCP off";
    public string LastAction { get; private set; } = "No AI requests yet";
    public int ConnectedClients => sessions.Count;
    sealed class Session(string version) { public readonly string Version = version; public volatile bool Ready; public DateTime LastSeen = DateTime.UtcNow; }
    public McpServer(MainWindow owner) => window = owner;
    public async Task Configure(McpSettings settings)
    {
        settings.Validate(); await configurationGate.WaitAsync();
        try
        {
            lifetime?.Cancel(); sessions.Clear();
            if (app != null) { await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); await app.DisposeAsync(); app = null; }
            lifetime?.Dispose(); lifetime = new(); Settings = settings;
            if (!settings.Enabled) { Status = "MCP off"; return; }
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(McpServer).Assembly.FullName });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 262144; k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); k.Listen(settings.AllowRemote ? IPAddress.Any : IPAddress.Loopback, settings.Port); });
            app = builder.Build(); app.Run(HandleHttp); await app.StartAsync(lifetime.Token);
            Status = settings.AllowRemote ? $"MCP on · remote allowed · port {settings.Port}" : $"MCP on · this computer only · port {settings.Port}";
        }
        catch (Exception e) { Status = "MCP could not start: " + e.Message; if (app != null) { await app.DisposeAsync(); app = null; } throw; }
        finally { configurationGate.Release(); }
    }
    bool Authorized(HttpContext context)
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + Settings.Token;
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }
    bool ValidHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1") return true;
        return Settings.AllowRemote && (host.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase) || Dns.GetHostAddresses(Dns.GetHostName()).Any(ip => ip.ToString() == host));
    }
    async Task HandleHttp(HttpContext context)
    {
        try
        {
            if (!Settings.Enabled || context.Request.Path != "/mcp") { context.Response.StatusCode = 404; return; }
            if (!Settings.AllowRemote && (context.Connection.RemoteIpAddress == null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress))) { context.Response.StatusCode = 403; return; }
            if (!ValidHost(context.Request.Host.Host) || context.Request.Host.Port != Settings.Port) { context.Response.StatusCode = 403; return; }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !ValidHost(uri.Host) || uri.Port != Settings.Port || uri.Scheme != "http")) { context.Response.StatusCode = 403; return; }
            if (!Authorized(context)) { context.Response.StatusCode = 401; context.Response.Headers.WWWAuthenticate = "Bearer"; return; }
            var version = context.Request.Headers["MCP-Protocol-Version"].ToString();
            if (version.Length > 0 && !Versions.Contains(version)) { context.Response.StatusCode = 400; return; }
            var sessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
            if (context.Request.Method == "DELETE") { context.Response.StatusCode = sessions.TryRemove(sessionId, out _) ? 200 : 404; return; }
            if (context.Request.Method != "POST") { context.Response.StatusCode = 405; context.Response.Headers.Allow = "POST, DELETE"; return; }
            if (!(context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false)) { context.Response.StatusCode = 415; return; }
            if (!context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 406; return; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime!.Token);
            JsonObject request;
            try { request = (await JsonNode.ParseAsync(context.Request.Body, new JsonNodeOptions(), new JsonDocumentOptions { MaxDepth = 32 }, linked.Token)) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { await Write(context, Failure(null, -32700, "Expected one JSON-RPC object."), linked.Token); return; }
            bool validId=!request.ContainsKey("id") || request["id"] is JsonValue identifier && (identifier.TryGetValue<string>(out _) || identifier.TryGetValue<long>(out _));
            if (request["jsonrpc"] is not JsonValue rpc || !rpc.TryGetValue<string>(out var rpcVersion) || rpcVersion != "2.0" || !validId || request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method)) { await Write(context, Failure(null, -32600, "Invalid JSON-RPC request."), linked.Token); return; }
            if(request["params"]!=null && request["params"] is not JsonObject) { await Write(context,Failure(request["id"],-32602,"Parameters must be an object."),linked.Token);return; }
            var id = request["id"]; var parameters = request["params"] as JsonObject ?? new();
            if (method == "initialize" && id != null)
            {
                foreach (var entry in sessions.Where(p => DateTime.UtcNow - p.Value.LastSeen > TimeSpan.FromMinutes(30))) sessions.TryRemove(entry.Key, out _);
                if (sessions.Count >= 16) { await Write(context, Failure(id, -32000, "Too many connected clients."), linked.Token); return; }
                var requested = parameters["protocolVersion"]?.GetValue<string>() ?? Versions[0];
                var negotiated = Versions.Contains(requested) ? requested : Versions[0];
                sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); sessions[sessionId] = new(negotiated);
                context.Response.Headers["Mcp-Session-Id"] = sessionId;
                await Write(context, Success(id, new { protocolVersion = negotiated, capabilities = new { tools = new { listChanged = false }, prompts = new { listChanged = false } },
                    serverInfo = new { name = "dosbox-enhanced", version = "1.2.0" }, instructions = McpInstructions.Agent }), linked.Token); return;
            }
            if (method == "ping") { if (id != null) await Write(context, Success(id, new { }), linked.Token); else context.Response.StatusCode = 202; return; }
            if (!sessions.TryGetValue(sessionId, out var session)) { context.Response.StatusCode = 404; return; }
            session.LastSeen = DateTime.UtcNow;
            if (version.Length > 0 && version != session.Version) { context.Response.StatusCode = 400; return; }
            if (id == null) { if (method == "notifications/initialized") session.Ready = true; context.Response.StatusCode = 202; return; }
            if (!session.Ready) { await Write(context, Failure(id, -32000, "Send notifications/initialized first."), linked.Token); return; }
            JsonObject response;
            switch (method)
            {
                case "tools/list": response = Success(id, new { tools = McpTools.List(Settings.AllowControl) }); break;
                case "tools/call":
                    linked.CancelAfter(TimeSpan.FromSeconds(60));
                    var name = parameters["name"]?.GetValue<string>() ?? "";
                    if (!McpTools.Known(name)) { response = Failure(id, -32602, "Unknown tool: " + name); break; }
                    await toolGate.WaitAsync(linked.Token);
                    try
                    {
                        if (!Settings.Enabled || !Settings.AllowControl && !McpTools.ReadOnly(name)) throw new InvalidOperationException("AI control is disabled. This connection is observation only.");
                        LastAction = DateTime.Now.ToString("HH:mm:ss") + " · " + name;
                        var result = await McpTools.Call(window, name, parameters["arguments"] as JsonObject ?? new(), linked.Token);
                        response = Success(id, result);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { response = Success(id, McpTools.Text(new { error = e.Message }, true)); }
                    finally { toolGate.Release(); }
                    break;
                case "prompts/list": response = Success(id, new { prompts = new[] { new { name = "dosbox_getting_started", description = "How to observe and control this DOS workspace", arguments = Array.Empty<object>() } } }); break;
                case "prompts/get": response = parameters["name"]?.GetValue<string>() == "dosbox_getting_started" ? Success(id, new { messages = new[] { new { role = "user", content = new { type = "text", text = McpInstructions.Agent } } } }) : Failure(id, -32602, "Unknown prompt."); break;
                default: response = Failure(id, -32601, "Method not found: " + method); break;
            }
            await Write(context, response, linked.Token);
        }
        catch (OperationCanceledException) { }
        catch (BadHttpRequestException e) { context.Response.StatusCode = e.StatusCode; }
        catch (Exception) { if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsync("Invalid MCP request."); } }
    }
    static Task Write(HttpContext context, JsonObject message, CancellationToken token) { context.Response.ContentType = "application/json"; return context.Response.WriteAsync(message.ToJsonString(), token); }
    internal static JsonObject Success(JsonNode? id, object result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result is JsonNode node ? node : JsonSerializer.SerializeToNode(result) };
    internal static JsonObject Failure(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    public void Dispose() { Settings = Settings with { Enabled = false }; lifetime?.Cancel(); sessions.Clear(); var stopped = app; app = null; if (stopped != null) _ = Task.Run(async () => { try { await stopped.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token); } catch (OperationCanceledException) { } finally { await stopped.DisposeAsync(); } }); }
}

public static class McpInstructions
{
    public const string Agent = "You are connected to DOSBox Enhanced. Start with dosbox_status and dosbox_list_environments. Use returned environment IDs and mapped DOS paths. dosbox_list_programs reads the saved executable menu; dosbox_manage_programs scans, hides/shows, renames, reorders and sets automatic discovery. dosbox_environment creates, copies, edits, selects or removes profiles; copies share mapped game folders but have independent settings and menus. dosbox_drive maps approved drives/subfolders; new host roots must first be mapped by the user in the app. dosbox_quick_run launches a mapped program standalone. dosbox_display controls separate environment/effects panels and the fullscreen menu. dosbox_mouse automates DOS directly, including middle clicks, without Windows mouse capture. dosbox_mouse_capture releases capture or configures the app-wide shortcut, pointer visibility, sensitivity and click-to-capture. Capturing requires a running, unpaused foreground game and never activates a background window. The default human release key is Caps Lock; Ctrl+F10 is always an extra exit. dosbox_press_keys targets DOS, not the host release shortcut. Use dosbox_screenshot with pair=true for matching original/enhanced PNGs and settings, or without pair to inspect graphics and dosbox_read_text for screen text; OCR may misread words, so verify uncertain text against the image. dosbox_get_configuration returns the structured setting catalog and installed core options. dosbox_configure merges settings, resets overrides with null, replaces them, or imports INI; restart=true reloads a matching running environment. A simple reset does not reload settings. Mounted folders are writable: confirm the user's intent before deleting or overwriting files through DOS commands. Removing a profile or drive never deletes its folders. Tools do not automate the general Windows desktop or change MCP/network permissions. Memory reads depend on the core; Lua/debugger/port-I/O/video recording are not provided. Respect observation-only mode and stop when MCP is disabled.";
    public static string StdioConfiguration() => JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["dosbox-enhanced"] = new { command = Path.Combine(Paths.Root, "app", "DosboxEnhanced.exe"), args = new[] { "--mcp-stdio" } } } }, Paths.Json);
    public static string HttpConfiguration(McpSettings settings) => JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["dosbox-enhanced"] = new { type = "http", url = settings.Url, headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + settings.Token } } } }, Paths.Json);
}

public static class McpStdio
{
    public static async Task<int> Run()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(65) };
        string session = "", version = "";
        try
        {
            while (await input.ReadLineAsync() is string line)
            {
                JsonObject request; try { if (line.Length > 262144) throw new JsonException(); request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException(); }
                catch (JsonException) { await output.WriteLineAsync(McpServer.Failure(null, -32700, "Invalid JSON-RPC line.").ToJsonString()); continue; }
                var id = request["id"];
                try
                {
                    var settings = ApplicationPreferences.Load().Mcp; settings.Validate();
                    if (!settings.Enabled) throw new InvalidOperationException("Enable MCP in DOSBox Enhanced and leave the app open.");
                    using var message = new HttpRequestMessage(HttpMethod.Post, settings.Url) { Content = new StringContent(line, Encoding.UTF8, "application/json") };
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token); message.Headers.Accept.ParseAdd("application/json, text/event-stream");
                    if (session.Length > 0) message.Headers.Add("Mcp-Session-Id", session);
                    if (version.Length > 0) message.Headers.Add("MCP-Protocol-Version", version);
                    using var reply = await client.SendAsync(message);
                    reply.EnsureSuccessStatusCode();
                    if (reply.Headers.TryGetValues("Mcp-Session-Id", out var headers)) session = headers.First();
                    var text = await reply.Content.ReadAsStringAsync();
                    if (text.Length > 0)
                    {
                        var decoded = JsonNode.Parse(text)!; if (request["method"]?.GetValue<string>() == "initialize") version = decoded["result"]?["protocolVersion"]?.GetValue<string>() ?? "";
                        await output.WriteLineAsync(text);
                    }
                }
                catch (Exception e) { if (id != null) await output.WriteLineAsync(McpServer.Failure(id, -32000, e.Message).ToJsonString()); }
            }
            if(session.Length>0)
            {
                try { var settings=ApplicationPreferences.Load().Mcp;using var end=new HttpRequestMessage(HttpMethod.Delete,settings.Url);end.Headers.Authorization=new AuthenticationHeaderValue("Bearer",settings.Token);end.Headers.Add("Mcp-Session-Id",session);if(version.Length>0)end.Headers.Add("MCP-Protocol-Version",version);using var response=await client.SendAsync(end); }
                catch(HttpRequestException) { }
            }
            return 0;
        }
        catch (Exception e) { await Console.Error.WriteLineAsync("DOSBox MCP bridge: " + e.Message); return 1; }
    }
}
