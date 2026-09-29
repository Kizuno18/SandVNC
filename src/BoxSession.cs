using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace SandVnc;

internal record Frame(byte[] Data, long Number, int Width, int Height, double CaptureMs, long StartedAt,
    AdapterState InputBefore);
internal record AdapterState(uint Status, ulong Epoch, ulong Sequence, int Pid, uint Hwnd, int Width,
    int Height, uint Keys, uint Modifiers, int X, int Y, ulong Applied);
internal record InputEvent(string Type, long Sequence, int A = 0, int B = 0, long Frame = 0);

internal sealed class BoxSession(string name, string executable, string root, string? sandboxBox = null, string? launcherPath = null)
{
    internal string Name { get; } = name;
    internal string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal string PipeName { get; } = "sandvnc-" + Guid.NewGuid().ToString("N");
    internal volatile string? Fault;
    internal volatile AdapterState? State;
    internal volatile Frame? Latest;
    internal Process? Target;
    internal long Frames, BytesSent;
    internal double CaptureWallMs;
    internal long CaptureDelayMs;
    internal long PublishDelayMs;
    internal volatile Frame? PendingFrame;
    internal long LastFrameRequest;
    internal ulong Epoch;
    internal long LastClientSequence;
    internal WebSocket? Controller;
    internal readonly SemaphoreSlim Gate = new(1, 1);
    private NamedPipeServerStream? activePipe;
    private long geometryFloor;
    private ulong wireSequence;
    private long lastViewerPulse;
    private DateTime creation;

    internal async Task Launch()
    {
        string serverPipe = sandboxBox is null ? PipeName : Native.BoxPipePrefix(sandboxBox) + PipeName;
        activePipe = new(serverPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = root };
        start.Environment["SANDVNC_PIPE"] = PipeName;
        if (sandboxBox is not null)
        {
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus", "Start.exe");
            start.ArgumentList.Add("/box:" + sandboxBox);
            start.ArgumentList.Add("/env:SANDVNC_PIPE=" + PipeName);
            start.ArgumentList.Add(launcherPath ?? executable);
            start.CreateNoWindow = true;
        }
        using var launcher = Process.Start(start) ?? throw new IOException("Process launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await activePipe.WaitForConnectionAsync(timeout.Token);
        if (!Native.GetNamedPipeClientProcessId(activePipe.SafePipeHandle, out uint actualPid)) throw new IOException("Cannot verify adapter PID");
        if (sandboxBox is null && (actualPid != launcher.Id || launcher.HasExited)) throw new IOException("Pipe client is not the launched process");
        if (sandboxBox is not null && Native.BoxOf((int)actualPid) != sandboxBox) throw new IOException("Pipe client belongs to another box");
        State = await ReadState(timeout.Token);
        if (State.Pid != actualPid) throw new IOException("Adapter PID mismatch");
        Target = Process.GetProcessById(State.Pid);
        creation = Target.StartTime;
        if (!string.Equals(Target.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("Wrong probe executable");
        if (State.Status != 0 || State.Hwnd == 0) throw new IOException("Adapter handshake failed: " + System.Text.Json.JsonSerializer.Serialize(State));
        if (!Native.Matches(State.Hwnd, State.Pid)) throw new IOException("Wrong window owner");
        await Wire(1);
        _ = Heartbeat();
        _ = Task.Factory.StartNew(CaptureLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private async Task<AdapterState> ReadState(CancellationToken token)
    {
        byte[] data = new byte[64];
        await activePipe!.ReadExactlyAsync(data, token);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x31435653) throw new IOException("Protocol mismatch");
        using var reader = new BinaryReader(new MemoryStream(data));
        reader.ReadUInt32();
        return new(reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadUInt32(),
            reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt64());
    }

    internal async Task<AdapterState> Wire(uint operation, int a = 0, int b = 0, Frame? geometry = null)
    {
        if (operation == 1) { Epoch++; wireSequence = 0; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(0x31435653u); writer.Write(operation); writer.Write(Epoch); writer.Write(++wireSequence); writer.Write(a); writer.Write(b);
        writer.Write(geometry?.Width ?? 0); writer.Write(geometry?.Height ?? 0);
        await activePipe!.WriteAsync(output.ToArray(), timeout.Token);
        var state = await ReadState(timeout.Token);
        if (state.Epoch != Epoch || state.Sequence != wireSequence) throw new IOException("Adapter reply sequence mismatch");
        if (State is not null && (state.Pid != State.Pid || state.Hwnd != State.Hwnd)) throw new IOException("Adapter instance changed");
        State = state;
        if (state.Status != 0) throw new IOException("Adapter rejected operation: " + state.Status);
        return state;
    }

    internal async Task<ulong> Acquire(WebSocket socket)
    {
        await Gate.WaitAsync();
        try
        {
            EnsureAlive();
            Controller?.Abort();
            Controller = null;
            await Wire(1);
            LastClientSequence = 0;
            Controller = socket;
            lastViewerPulse = Environment.TickCount64;
            return Epoch;
        }
        finally { Gate.Release(); }
    }

    internal async Task<AdapterState> Input(WebSocket socket, ulong lease, InputEvent input)
    {
        await Gate.WaitAsync();
        try
        {
            EnsureAlive();
            if (Controller != socket || Epoch != lease) throw new IOException("Lease revoked");
            if (input.Sequence <= LastClientSequence) throw new IOException("Stale input sequence");
            LastClientSequence = input.Sequence;
            Frame? geometry = null;
            if (input.Type is "move" or "button")
            {
                var frame = Latest ?? throw new IOException("No captured geometry");
                if (input.Frame < Math.Max(frame.Number - 20, Interlocked.Read(ref geometryFloor)) || input.Frame > frame.Number ||
                    Environment.TickCount64 - frame.StartedAt > 2000 || State!.Width != frame.Width || State.Height != frame.Height)
                    throw new IOException("Stale frame geometry");
                geometry = frame;
            }
            uint operation = input.Type switch { "key" => 2, "text" => 3, "move" => 4, "button" => 5, "pulse" => 6, "status" => 7, _ => throw new IOException("Unknown input") };
            if (operation == 2 && (input.A < 8 || input.A > 165 || input.B is < 0 or > 1)) throw new IOException("Invalid key");
            lastViewerPulse = Environment.TickCount64;
            return await Wire(operation, input.A, input.B, geometry);
        }
        finally { Gate.Release(); }
    }

    internal async Task Release(WebSocket socket, ulong lease)
    {
        await Gate.WaitAsync();
        try
        {
            if (Controller != socket || Epoch != lease) return;
            Controller = null;
            await Wire(1);
        }
        catch (Exception error) { Fault = error.Message; activePipe?.Dispose(); }
        finally { Gate.Release(); }
    }

    private void EnsureAlive()
    {
        if (Fault is not null) throw new IOException(Fault);
        if (Target is null || Target.HasExited || Target.StartTime != creation || State is null || !Native.Matches(State.Hwnd, Target.Id))
            throw new IOException("Target instance ended");
        if (Native.VisibleWindows(Target.Id) != 1) throw new IOException("Auxiliary window requires a supported capture/input surface");
    }

    private async Task Heartbeat()
    {
        while (true)
        {
            await Task.Delay(250);
            await Gate.WaitAsync();
            try
            {
                EnsureAlive();
                if (Controller is not null && Environment.TickCount64 - lastViewerPulse > 2000)
                {
                    Controller.Abort(); Controller = null; await Wire(1);
                }
                else await Wire(6);
            }
            catch (Exception error)
            {
                Fault = error.Message; Latest = null; Controller?.Abort(); Controller = null;
                activePipe?.Dispose();
                return;
            }
            finally { Gate.Release(); }
        }
    }

    private void CaptureLoop()
    {
        while (Fault is null)
        {
            try
            {
                if (Environment.TickCount64 - Interlocked.Read(ref LastFrameRequest) > 1500) { Thread.Sleep(100); continue; }
                Thread.Sleep((int)Math.Clamp(Interlocked.Read(ref CaptureDelayMs), 0, 10000));
                EnsureAlive();
                var inputBefore = State!;
                long startedAt = Environment.TickCount64;
                var watch = Stopwatch.StartNew();
                byte[] data = Native.Capture(State!.Hwnd, State.Pid, out int width, out int height, renderFullContent: launcherPath is not null);
                double elapsed = watch.Elapsed.TotalMilliseconds;
                CaptureWallMs += elapsed;
                long number = Interlocked.Increment(ref Frames);
                var frame = new Frame(data, number, width, height, elapsed, startedAt, inputBefore);
                PendingFrame = frame;
                Thread.Sleep((int)Math.Clamp(Interlocked.Read(ref PublishDelayMs), 0, 1500));
                if (Latest?.Width != width || Latest?.Height != height) Interlocked.Exchange(ref geometryFloor, number);
                if (Fault is null) Latest = frame;
                PendingFrame = null;
                Thread.Sleep(Math.Max(1, 100 - (int)elapsed));
            }
            catch (Exception error) { Fault = error.Message; Latest = null; PendingFrame = null; }
        }
    }

    internal object Metrics()
    {
        var frame = Latest;
        var state = State;
        var pending = PendingFrame;
        return new { name = Name, pid = state?.Pid, hwnd = state?.Hwnd, fault = Fault,
            state, frames = Frames, bytesSent = BytesSent, captureWallMs = CaptureWallMs,
            frameAgeMs = frame is null ? (long?)null : Environment.TickCount64 - frame.StartedAt,
            pendingFrame = pending is null ? null : new { pending.Number, pending.StartedAt, pending.InputBefore } };
    }
}
