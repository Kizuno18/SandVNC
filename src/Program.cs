using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SandVnc;

Native.SetProcessDpiAwarenessContext(-4);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (args.Contains("--inspect"))
{
    Console.WriteLine(JsonSerializer.Serialize(Native.Inventory(), new JsonSerializerOptions { WriteIndented = true }));
    return;
}
string root = Path.GetFullPath(Environment.GetEnvironmentVariable("SANDVNC_ROOT") ?? Directory.GetCurrentDirectory());
if (args.Length == 1 && args[0] == "--sandbox-preflight")
{
    string result = JsonSerializer.Serialize(Native.ConfigurationPreflight(), new JsonSerializerOptions(json) { WriteIndented = true });
    await File.WriteAllTextAsync(Path.Combine(root, "evidence", "sandbox-preflight.json"), result + "\n");
    Console.WriteLine(result);
    return;
}
if (args.Length == 1 && args[0] == "--capture-client")
{
    using var inventory = JsonDocument.Parse(JsonSerializer.Serialize(Native.Inventory()));
    var candidates = inventory.RootElement.EnumerateArray().Where(item => item.TryGetProperty("visible", out var visible) && visible.GetBoolean()
        && item.GetProperty("box").GetString() is { Length: > 0 }).ToArray();
    if (candidates.Length != 1) throw new IOException("Expected exactly one visible boxed PXG window");
    int pid = candidates[0].GetProperty("pid").GetInt32();
    uint hwnd = candidates[0].GetProperty("hwnd").GetUInt32();
    var watch = Stopwatch.StartNew();
    var bytes = Native.Capture(hwnd, pid, out int width, out int height, renderFullContent: true);
    double elapsed = watch.Elapsed.TotalMilliseconds;
    await File.WriteAllBytesAsync(Path.Combine(root, "evidence", "pxg-printwindow.png"), bytes);
    await File.WriteAllTextAsync(Path.Combine(root, "evidence", "pxg-capture.json"), JsonSerializer.Serialize(new
    { pid, hwnd, width, height, captureAndEncodeMs = elapsed, bytes = bytes.Length,
      qualification = "Single PrintWindow attempt against an existing PXG window, no remote input or two-box acceptance" }, json));
    return;
}
if (args.Length == 2 && args[0] is "--post-probe-messages" or "--recreate-probe" or "--resize-probe")
{
    Native.ProbeAction(int.Parse(args[1]), root, args[0]);
    return;
}
bool demo = args.Contains("--demo");
bool clientDemo = args.Contains("--client-demo");
string mode = clientDemo ? "PXG local integration diagnostic; file isolation and full usability unverified" : "synthetic";
if (args.Contains("--box-transport-check") || args.Contains("--client-transport-check"))
{
    bool realClient = args.Contains("--client-transport-check");
    var sessions = new[] { "SandVnc_A", "SandVnc_B" }.Select(name =>
        new BoxSession(name, realClient ? Path.Combine(root, "runtime", "client", "win32", "pxgme32.exe") : Path.Combine(root, "build", "Release", "input-probe.exe"), root, name,
            realClient ? Path.Combine(root, "build", "Release", "client-launcher.exe") : null)).ToArray();
    try
    {
        if (realClient && !File.Exists(Path.Combine(root, "runtime", "client", "win32", "pxgme32.exe")))
            throw new IOException("Workspace PXG copy is missing. Review evidence/client-security-block.json before another client experiment.");
        await Task.WhenAll(sessions.Select(session => session.Launch()));
        async Task<AdapterState> Command(BoxSession session, uint operation, int a = 0, int b = 0)
        {
            await session.Gate.WaitAsync();
            try { return await session.Wire(operation, a, b); }
            finally { session.Gate.Release(); }
        }
        var held = await Task.WhenAll(sessions.Select(session => Command(session, 2, 87, 1)));
        var released = await Command(sessions[0], 2, 87, 0);
        var remaining = await Command(sessions[1], 7);
        if (released.Keys != 0 || remaining.Keys != 1) throw new IOException("Box input separation failed");
        string result = JsonSerializer.Serialize(new { mode = realClient ? "Real PXG transport check; application behavior not yet verified" : "Real Sandboxie boxes, synthetic Win32 targets; not PXG acceptance", held, released, remaining },
            new JsonSerializerOptions(json) { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(root, "evidence", realClient ? "client-transport.json" : "box-transport.json"), result + "\n");
        Console.WriteLine(result);
    }
    catch (Exception error)
    {
        string result = JsonSerializer.Serialize(new { mode = realClient ? "PXG transport experiment" : "Boxed probe transport", status = "failed", error = error.ToString() },
            new JsonSerializerOptions(json) { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(root, "evidence", realClient ? "client-transport-failure.json" : "box-transport-failure.json"), result + "\n");
        Console.Error.WriteLine(result);
        Environment.ExitCode = 1;
    }
    finally { foreach (var session in sessions) if (session.Target is { HasExited: false } target) target.Kill(); }
    return;
}
int port = 8765;
int portIndex = Array.IndexOf(args, "--port");
if (portIndex >= 0) port = int.Parse(args[portIndex + 1]);
if (!demo && !clientDemo)
{
    Console.Error.WriteLine("Live PXG mode is gated: two new boxes, adapter compatibility, auxiliary windows and file isolation have not been validated. Use --inspect or --demo. See README.md.");
    Environment.ExitCode = 2;
    return;
}
string probe = Path.Combine(root, "build", "Release", "input-probe.exe");
var boxes = new[] { "A", "B" }.Select(name => clientDemo
    ? new BoxSession(name, Path.Combine(root, "runtime", "client", "win32", "pxgme32.exe"), root, "SandVnc_" + name,
        Path.Combine(root, "build", "Release", "client-launcher.exe"))
    : new BoxSession(name, probe, root)).ToArray();
await Task.WhenAll(boxes.Select(box => box.Launch()));
Directory.CreateDirectory(Path.Combine(root, "runtime"));
string baseUrl = "http://127.0.0.1:" + port;
await File.WriteAllTextAsync(Path.Combine(root, "runtime", "viewers.json"), JsonSerializer.Serialize(new
{
    mode, url = baseUrl,
    viewers = boxes.Select(box => new { box.Name, box.Token, pid = box.Target!.Id, url = baseUrl + "/#" + box.Token }),
    brokerPid = Environment.ProcessId
}, json));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
var app = builder.Build();
app.UseWebSockets();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'";
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost")) { context.Response.StatusCode = 403; return; }
    if (context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin != baseUrl)
    { context.Response.StatusCode = 403; return; }
    await next(context);
});
BoxSession? Authenticate(string? token)
{
    if (token is null || token.Length != 64) return null;
    byte[] provided = Encoding.ASCII.GetBytes(token);
    return boxes.FirstOrDefault(box => CryptographicOperations.FixedTimeEquals(provided, Encoding.ASCII.GetBytes(box.Token)));
}
BoxSession? FromHeader(HttpContext context)
{
    string value = context.Request.Headers.Authorization.ToString();
    return value.StartsWith("Bearer ", StringComparison.Ordinal) ? Authenticate(value[7..]) : null;
}
app.MapGet("/", () => Results.File(Path.Combine(AppContext.BaseDirectory, "viewer.html"), "text/html"));
app.MapGet("/status", (HttpContext context) => FromHeader(context) is { } box ? Results.Json(box.Metrics(), json) : Results.Unauthorized());
app.MapGet("/frame", async (HttpContext context) =>
{
    var box = FromHeader(context);
    if (box is null) { context.Response.StatusCode = 401; return; }
    Interlocked.Exchange(ref box.LastFrameRequest, Environment.TickCount64);
    var frame = box.Latest;
    if (box.Fault is not null || frame is null || Environment.TickCount64 - frame.StartedAt > 2000)
    { context.Response.StatusCode = 503; return; }
    context.Response.ContentType = "image/png";
    context.Response.Headers["X-Frame"] = frame.Number.ToString();
    context.Response.Headers["X-Capture-Ms"] = frame.CaptureMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    context.Response.Headers["X-Frame-Age-Ms"] = (Environment.TickCount64 - frame.StartedAt).ToString();
    context.Response.Headers["X-Input-Epoch"] = frame.InputBefore.Epoch.ToString();
    context.Response.Headers["X-Input-Sequence"] = frame.InputBefore.Sequence.ToString();
    context.Response.Headers["X-Input-Applied"] = frame.InputBefore.Applied.ToString();
    context.Response.Headers["X-Input-Keys"] = frame.InputBefore.Keys.ToString();
    context.Response.Headers["X-Capture-Started"] = frame.StartedAt.ToString();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(1500);
    try { await context.Response.Body.WriteAsync(frame.Data, timeout.Token); Interlocked.Add(ref box.BytesSent, frame.Data.Length); }
    catch (OperationCanceledException) { }
});
app.MapPost("/diagnostic/capture-delay", async (HttpContext context) =>
{
    var box = FromHeader(context);
    if (box is null) { context.Response.StatusCode = 401; return; }
    if (!int.TryParse(context.Request.Query["ms"], out int delay) || delay < 0 || delay > 10000)
    { context.Response.StatusCode = 400; return; }
    Interlocked.Exchange(ref box.CaptureDelayMs, delay);
    await context.Response.WriteAsync("ok");
});
app.MapPost("/diagnostic/publish-delay", (HttpContext context) =>
{
    var box = FromHeader(context);
    if (box is null) return Results.Unauthorized();
    if (!int.TryParse(context.Request.Query["ms"], out int delay) || delay < 0 || delay > 1500) return Results.BadRequest();
    Interlocked.Exchange(ref box.PublishDelayMs, delay);
    return Results.Ok();
});
app.Map("/input", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    BoxSession? box = null;
    ulong lease = 0;
    try
    {
        using var authTimeout = new CancellationTokenSource(3000);
        string auth = await Receive(socket, authTimeout.Token);
        box = Authenticate(auth) ?? throw new IOException("Unauthorized");
        lease = await box.Acquire(socket);
        await Send(socket, new { type = "lease", epoch = lease, mode, box = box.Name, pid = box.State!.Pid });
        while (socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(3000);
            var input = JsonSerializer.Deserialize<InputEvent>(await Receive(socket, timeout.Token), json) ?? throw new IOException("Invalid input");
            var watch = Stopwatch.StartNew();
            var state = await box.Input(socket, lease, input);
            await Send(socket, new { type = "ack", sequence = input.Sequence, adapterMs = watch.Elapsed.TotalMilliseconds, state });
        }
    }
    catch (Exception error) when (error is IOException or JsonException or WebSocketException or OperationCanceledException or InvalidOperationException)
    {
        socket.Abort();
    }
    finally { if (box is not null) await box.Release(socket, lease); }
});
Console.WriteLine($"Viewers ready: {mode}. URLs are in runtime/viewers.json.");
await app.RunAsync();

async Task Send(WebSocket socket, object value)
{
    using var timeout = new CancellationTokenSource(1000);
    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, json), WebSocketMessageType.Text, true, timeout.Token);
}
static async Task<string> Receive(WebSocket socket, CancellationToken token)
{
    byte[] buffer = new byte[2048];
    var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
    if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) throw new IOException("Invalid or oversized message");
    return Encoding.UTF8.GetString(buffer, 0, result.Count);
}
