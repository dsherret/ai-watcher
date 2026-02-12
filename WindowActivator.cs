#if WINDOWS
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace AIWatcher;

public static class WindowActivator
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextW(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Finds the window for a Claude Code session and brings it to the foreground.
    /// </summary>
    public static bool ActivateSession(string sessionId, string workspace, string providerName)
    {
        bool isVscode = providerName.Contains("VS Code", StringComparison.OrdinalIgnoreCase);

        // for VS Code sessions: match by window title since process tree is unreliable
        if (isVscode)
        {
            var folderName = Path.GetFileName(workspace.TrimEnd('\\', '/'));
            var hwnd = FindVscodeWindow(folderName);
            if (hwnd != IntPtr.Zero)
                return ActivateWindow(hwnd);
        }

        // for CLI sessions: find the process and walk up to the terminal window
        var processes = LoadProcessTree();
        var normalizedWorkspace = workspace.TrimEnd('\\', '/').ToUpperInvariant();

        // if the session ID encodes a PID (from process fallback), use it directly
        uint? pid = null;
        if (sessionId.StartsWith("cli-") && uint.TryParse(sessionId.AsSpan(4), out var directPid))
        {
            pid = directPid;
        }
        else
        {
            // match by command line, but verify the CWD matches the workspace
            // (a --resume flag can reference a session from a different workspace)
            pid = FindByCommandLine(processes, sessionId);
            if (pid != null)
            {
                var cwd = ProcessHelper.GetCurrentDirectory(pid.Value);
                if (cwd == null || !cwd.TrimEnd('\\', '/').ToUpperInvariant().Equals(normalizedWorkspace))
                    pid = null;
            }
        }
        pid ??= FindCliByWorkingDirectory(processes, workspace);

        if (pid != null)
        {
            var hwnd = FindAncestorWindow(processes, pid.Value);
            if (hwnd != IntPtr.Zero)
                return ActivateWindow(hwnd);
        }

        return false;
    }

    private static IntPtr FindVscodeWindow(string folderName)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var title = GetWindowTitle(hwnd);

            // VS Code titles look like: "folder - file - Visual Studio Code"
            if (title.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase) &&
                title.Contains(folderName, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private record ProcessInfo(uint Pid, uint ParentPid, string Name, string? CommandLine);

    private static Dictionary<uint, ProcessInfo> LoadProcessTree()
    {
        var map = new Dictionary<uint, ProcessInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process");
            foreach (var obj in searcher.Get())
            {
                var pid = Convert.ToUInt32(obj["ProcessId"]);
                var ppid = Convert.ToUInt32(obj["ParentProcessId"]);
                var name = obj["Name"]?.ToString() ?? "";
                var cmd = obj["CommandLine"]?.ToString();
                map[pid] = new ProcessInfo(pid, ppid, name, cmd);
                obj.Dispose();
            }
        }
        catch { }
        return map;
    }

    private static uint? FindByCommandLine(Dictionary<uint, ProcessInfo> tree, string sessionId)
    {
        foreach (var p in tree.Values)
        {
            if (p.CommandLine?.Contains(sessionId, StringComparison.OrdinalIgnoreCase) == true)
                return p.Pid;
        }
        return null;
    }

    private static uint? FindCliByWorkingDirectory(
        Dictionary<uint, ProcessInfo> tree, string workspace)
    {
        var normalizedWorkspace = workspace.TrimEnd('\\', '/').ToUpperInvariant();

        foreach (var p in tree.Values)
        {
            var nameUpper = p.Name.ToUpperInvariant();
            bool isCandidate = nameUpper == "CLAUDE.EXE" ||
                (nameUpper == "NODE.EXE" &&
                 p.CommandLine?.Contains("claude-code", StringComparison.OrdinalIgnoreCase) == true);

            if (!isCandidate) continue;

            // skip VS Code extension processes — we only want CLI processes here
            if (p.CommandLine?.Contains("claude-vscode", StringComparison.OrdinalIgnoreCase) == true)
                continue;

            var cwd = ProcessHelper.GetCurrentDirectory(p.Pid);
            if (cwd == null) continue;

            var normalizedCwd = cwd.TrimEnd('\\', '/').ToUpperInvariant();
            if (normalizedCwd == normalizedWorkspace)
                return p.Pid;
        }

        return null;
    }

    private static IntPtr FindAncestorWindow(Dictionary<uint, ProcessInfo> tree, uint pid)
    {
        var visited = new HashSet<uint>();
        var current = pid;

        for (int i = 0; i < 15; i++)
        {
            if (!visited.Add(current))
                break;

            try
            {
                var process = Process.GetProcessById((int)current);
                if (process.MainWindowHandle != IntPtr.Zero)
                    return process.MainWindowHandle;
            }
            catch { break; }

            if (tree.TryGetValue(current, out var info))
                current = info.ParentPid;
            else
                break;
        }

        return IntPtr.Zero;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool ActivateWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        return SetForegroundWindow(hwnd);
    }
}
#elif MACCATALYST
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AIWatcher;

public static class WindowActivator
{
    [DllImport("/usr/lib/libproc.dylib")]
    private static extern int proc_pidpath(int pid, byte[] buffer, int buffersize);

    /// <summary>
    /// Finds the window for a Claude Code session and brings it to the foreground.
    /// </summary>
    public static bool ActivateSession(string sessionId, string workspace, string providerName)
    {
        bool isVscode = providerName.Contains("VS Code", StringComparison.OrdinalIgnoreCase);

        if (isVscode)
        {
            // try common VS Code-like editors
            return ActivateAppByName("Visual Studio Code")
                || ActivateAppByName("Code")
                || ActivateAppByName("Cursor");
        }

        // for CLI sessions: walk up the process tree to find the terminal app
        var tree = LoadProcessTree();

        uint? pid = null;
        if (sessionId.StartsWith("cli-") && uint.TryParse(sessionId.AsSpan(4), out var directPid))
        {
            pid = directPid;
        }
        else
        {
            pid = FindByCommandLine(tree, sessionId);
            if (pid != null)
            {
                var cwd = ProcessHelper.GetCurrentDirectory(pid.Value);
                if (cwd == null || cwd.TrimEnd('/') != workspace.TrimEnd('/'))
                    pid = null;
            }
        }
        pid ??= FindCliByWorkingDirectory(tree, workspace);

        if (pid != null)
        {
            var appName = FindAncestorApp(tree, pid.Value);
            if (appName != null)
                return ActivateAppByName(appName);
        }

        return false;
    }

    private record ProcessInfo(uint Pid, uint ParentPid, string Args);

    private static Dictionary<uint, ProcessInfo> LoadProcessTree()
    {
        var map = new Dictionary<uint, ProcessInfo>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                ArgumentList = { "-Aeo", "pid=,ppid=,args=" },
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return map;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var trimmed = line.AsSpan().Trim();

                    var pidEnd = trimmed.IndexOf(' ');
                    if (pidEnd < 0) continue;
                    if (!uint.TryParse(trimmed[..pidEnd], out var pid)) continue;

                    var afterPid = trimmed[(pidEnd + 1)..].TrimStart();
                    var ppidEnd = afterPid.IndexOf(' ');
                    if (ppidEnd < 0) continue;
                    if (!uint.TryParse(afterPid[..ppidEnd], out var ppid)) continue;

                    var args = afterPid[(ppidEnd + 1)..].TrimStart().ToString();

                    map[pid] = new ProcessInfo(pid, ppid, args);
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    private static uint? FindByCommandLine(Dictionary<uint, ProcessInfo> tree, string sessionId)
    {
        foreach (var p in tree.Values)
        {
            if (p.Args.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
                return p.Pid;
        }
        return null;
    }

    private static uint? FindCliByWorkingDirectory(
        Dictionary<uint, ProcessInfo> tree, string workspace)
    {
        var normalized = workspace.TrimEnd('/');

        foreach (var p in tree.Values)
        {
            var firstSpace = p.Args.IndexOf(' ');
            var execPath = firstSpace >= 0 ? p.Args[..firstSpace] : p.Args;
            var execName = Path.GetFileName(execPath);

            bool isCandidate =
                execName.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
                (execName.Equals("node", StringComparison.OrdinalIgnoreCase) &&
                 p.Args.Contains("claude-code", StringComparison.OrdinalIgnoreCase));

            if (!isCandidate) continue;
            if (p.Args.Contains("claude-vscode", StringComparison.OrdinalIgnoreCase))
                continue;

            var cwd = ProcessHelper.GetCurrentDirectory(p.Pid);
            if (cwd?.TrimEnd('/') == normalized)
                return p.Pid;
        }

        return null;
    }

    /// <summary>
    /// Walks up the process tree to find the nearest ancestor that lives inside a .app bundle.
    /// Returns the app name suitable for AppleScript activation.
    /// </summary>
    private static string? FindAncestorApp(Dictionary<uint, ProcessInfo> tree, uint pid)
    {
        var visited = new HashSet<uint>();
        var current = pid;

        while (visited.Add(current))
        {
            var appName = GetAppNameFromPid((int)current);
            if (appName != null)
                return appName;

            if (tree.TryGetValue(current, out var info))
                current = info.ParentPid;
            else
                break;
        }

        return null;
    }

    /// <summary>
    /// Gets the app bundle name for a process by checking if its executable is inside a .app bundle.
    /// </summary>
    private static string? GetAppNameFromPid(int pid)
    {
        try
        {
            var buffer = new byte[4096];
            var length = proc_pidpath(pid, buffer, buffer.Length);
            if (length <= 0) return null;

            var path = Encoding.UTF8.GetString(buffer, 0, length);

            // check if path contains ".app/Contents/MacOS/"
            var appIdx = path.IndexOf(".app/", StringComparison.Ordinal);
            if (appIdx < 0) return null;

            // extract app name: find the last / before ".app/"
            var lastSlash = path.LastIndexOf('/', appIdx);
            if (lastSlash < 0) return null;

            return path[(lastSlash + 1)..appIdx];
        }
        catch
        {
            return null;
        }
    }

    private static bool ActivateAppByName(string appName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", $"tell application \"{appName}\" to activate" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
#endif
