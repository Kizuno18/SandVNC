using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SandVnc;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    internal delegate bool EnumCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern bool PostMessageW(nint hwnd, uint message, nuint w, nint l);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryProcess(nint pid, StringBuilder box, nint image, nint sid, nint session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate uint UpdateConfig(uint operation, string section, string setting, string value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate uint IsBoxEnabled(string box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint SessionLeader(uint session, out nint pid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate uint QueryBoxPath(string box, nint files, nint registry, StringBuilder ipc,
        nint fileLength, nint registryLength, ref uint ipcLength);
    private static nint sandboxDll;
    private static QueryProcess? query;

    internal static string BoxOf(int pid)
    {
        if (query is null)
        {
            sandboxDll = NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus", "SbieDll.dll"));
            query = Marshal.GetDelegateForFunctionPointer<QueryProcess>(NativeLibrary.GetExport(sandboxDll, "SbieApi_QueryProcess"));
        }
        var name = new StringBuilder(64);
        return query(pid, name, 0, 0, 0) == 0 ? name.ToString() : "";
    }

    internal static object ConfigurationPreflight()
    {
        BoxOf(Environment.ProcessId);
        var enabled = Marshal.GetDelegateForFunctionPointer<IsBoxEnabled>(NativeLibrary.GetExport(sandboxDll, "SbieApi_IsBoxEnabled"));
        var update = Marshal.GetDelegateForFunctionPointer<UpdateConfig>(NativeLibrary.GetExport(sandboxDll, "SbieApi_UpdateConf"));
        var leader = Marshal.GetDelegateForFunctionPointer<SessionLeader>(NativeLibrary.GetExport(sandboxDll, "SbieApi_SessionLeader"));
        string candidate = "SVNC_Check_" + Guid.NewGuid().ToString("N")[..20];
        uint before = enabled(candidate);
        if (before != 0xC0000034) throw new IOException($"Probe name is not confirmed absent: 0x{before:X8}");
        uint permission = update(5, candidate, "", "");
        uint leaderResult = leader((uint)Process.GetCurrentProcess().SessionId, out nint leaderPid);
        return new { operation = "Remove an absent random section (permission probe only)",
            before = $"0x{before:X8}", update = $"0x{permission:X8}", after = $"0x{enabled(candidate):X8}",
            sessionLeaderQuery = $"0x{leaderResult:X8}", sessionLeaderPid = leaderPid.ToInt64(), callerPid = Environment.ProcessId,
            configFileWritten = false, existingSectionChanged = false };
    }

    internal static string BoxPipePrefix(string box)
    {
        if (box is not ("SandVnc_A" or "SandVnc_B")) throw new IOException("Unapproved box");
        BoxOf(Environment.ProcessId);
        var getPath = Marshal.GetDelegateForFunctionPointer<QueryBoxPath>(NativeLibrary.GetExport(sandboxDll, "SbieApi_QueryBoxPath"));
        var path = new StringBuilder(1024);
        uint length = 2048;
        uint status = getPath(box, 0, 0, path, 0, 0, ref length);
        if (status != 0 || path.Length == 0) throw new IOException($"Cannot resolve box IPC path: 0x{status:X8}");
        return path.ToString().Replace('\\', '_') + "\\";
    }

    internal static List<object> Inventory()
    {
        var result = new List<object>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!process.ProcessName.Contains("pxg", StringComparison.OrdinalIgnoreCase)) return true;
                var className = new StringBuilder(256);
                GetClassName(hwnd, className, 256);
                GetClientRect(hwnd, out var rect);
                result.Add(new { pid, hwnd = hwnd.ToInt64(), visible = IsWindowVisible(hwnd), className = className.ToString(),
                    width = rect.Right, height = rect.Bottom, executable = process.MainModule?.FileName,
                    started = process.StartTime.ToUniversalTime(), box = BoxOf((int)pid) });
            }
            catch (Exception error) { result.Add(new { pid, error = error.Message }); }
            return true;
        }, 0);
        return result;
    }

    internal static void ProbeAction(int pid, string root, string action)
    {
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.MainModule?.FileName, Path.Combine(root, "build", "Release", "input-probe.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only the workspace probe may receive diagnostic messages");
        bool sent = false;
        EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(hwnd, name, 256);
            if (!Matches((uint)hwnd, pid) || name.ToString() != "SandVncInputProbe") return true;
            if (action == "--recreate-probe") PostMessageW(hwnd, 0x8001, 0, 0);
            else if (action == "--resize-probe") SetWindowPos(hwnd, 0, 0, 0, 900, 450, 0x16);
            else
            {
                PostMessageW(hwnd, 0x100, 87, 1);
                PostMessageW(hwnd, 0x102, 120, 1);
                PostMessageW(hwnd, 0x200, 0, (200 << 16) | 200);
                PostMessageW(hwnd, 0x201, 1, (200 << 16) | 200);
                PostMessageW(hwnd, 0x202, 0, (200 << 16) | 200);
                PostMessageW(hwnd, 0x101, 87, unchecked((int)0xC0000001));
            }
            sent = true;
            return false;
        }, 0);
        if (!sent) throw new IOException("Probe window missing");
    }

    internal static bool Matches(uint hwnd, int pid)
    {
        GetWindowThreadProcessId((nint)hwnd, out uint actual);
        return actual == pid;
    }

    internal static int VisibleWindows(int pid)
    {
        int count = 0;
        EnumWindows((hwnd, _) => { if (IsWindowVisible(hwnd) && Matches((uint)hwnd, pid)) count++; return true; }, 0);
        return count;
    }

    internal static byte[] Capture(uint hwnd, int pid, out int width, out int height, bool renderFullContent = false)
    {
        if (!Matches(hwnd, pid) || !GetClientRect((nint)hwnd, out var rect)) throw new IOException("Window identity lost");
        width = rect.Right; height = rect.Bottom;
        if (width < 1 || height < 1 || width > 4096 || height > 4096) throw new IOException("Unsupported dimensions");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
            var dc = graphics.GetHdc();
            bool success;
            try { success = PrintWindow((nint)hwnd, dc, renderFullContent ? 3u : 1u); }
            finally { graphics.ReleaseHdc(dc); }
            if (!success) throw new IOException("PrintWindow failed; no fallback");
        }
        if (!Matches(hwnd, pid)) throw new IOException("Window identity lost after capture");
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
