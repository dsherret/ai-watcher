using System.Text;
#if WINDOWS
using System.Management;
#endif

namespace AIWatcher;

public class ClaudeCodeProvider : IAIProvider
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    // cache session ID → workspace, entries never change once created
    private readonly Dictionary<string, string> _workspaceCache = [];

    public string ProviderName => "Claude Code";

    public Task<IReadOnlyList<AIInstance>> GetInstancesAsync()
    {
        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");
        var projectsDir = Path.Combine(claudeDir, "projects");
        var debugDir = new DirectoryInfo(Path.Combine(claudeDir, "debug"));

        var instances = new List<AIInstance>();

        // single query to get all claude-related process info
        var processInfo = LoadProcessInfo();

        // 1) debug-log-based discovery (CLI sessions)
        //    debug logs identify which sessions exist; JSONL provides status
        if (Directory.Exists(projectsDir) && debugDir.Exists)
        {
            var now = DateTime.UtcNow;

            // count CLI processes per workspace so we know how many sessions to show
            var cliProcessCount = new Dictionary<string, int>(PathComparer);
            var cliWorkspaceSet = new HashSet<string>(PathComparer);
            foreach (var cli in processInfo.CliProcesses)
            {
                cliWorkspaceSet.Add(cli.Workspace);
                cliProcessCount.TryGetValue(cli.Workspace, out var count);
                cliProcessCount[cli.Workspace] = count + 1;
            }

            // collect all alive candidates per workspace
            var candidatesPerWorkspace = new Dictionary<string, List<(string sessionId, DateTime lastWrite)>>(
                PathComparer);

            foreach (var fi in debugDir.EnumerateFiles("*.txt"))
            {
                try
                {
                    var sessionId = Path.GetFileNameWithoutExtension(fi.Name);
                    var workspace = LookupWorkspace(projectsDir, sessionId);
                    if (workspace == null)
                        continue;

                    var isFresh = now - fi.LastWriteTimeUtc < StaleThreshold;
                    var hasCliProcess = cliWorkspaceSet.Contains(workspace);

                    // only keep if debug log is fresh OR a CLI process is alive
                    if (!isFresh && !hasCliProcess)
                        continue;

                    if (!candidatesPerWorkspace.TryGetValue(workspace, out var list))
                    {
                        list = [];
                        candidatesPerWorkspace[workspace] = list;
                    }
                    list.Add((sessionId, fi.LastWriteTimeUtc));
                }
                catch
                {
                    // file may have vanished between enumeration and read
                }
            }

            foreach (var (workspace, candidates) in candidatesPerWorkspace)
            {
                // sort by freshness descending, keep up to N where N = CLI process count
                // (if no processes, just 1 session based on fresh debug log)
                candidates.Sort((a, b) => b.lastWrite.CompareTo(a.lastWrite));
                var maxSessions = cliProcessCount.GetValueOrDefault(workspace, 1);

                foreach (var (sessionId, _) in candidates.Take(maxSessions))
                {
                    // read this session's own JSONL for status
                    var status = AIStatus.Active;
                    var lastActivity = DateTime.UtcNow;
                    var encodedPath = EncodeWorkspacePath(workspace);
                    var jsonlPath = Path.Combine(projectsDir, encodedPath, sessionId + ".jsonl");
                    if (File.Exists(jsonlPath))
                    {
                        status = ReadStatusFromJsonl(jsonlPath);
                        lastActivity = File.GetLastWriteTimeUtc(jsonlPath);
                    }

                    instances.Add(new AIInstance
                    {
                        Id = sessionId,
                        ProviderName = ProviderName,
                        Workspace = workspace,
                        Status = status,
                        LastActivity = lastActivity
                    });
                }
            }
        }

        // 2) CLI process fallback — for sessions that don't have a JSONL yet
        //    (e.g. just started, or session ID changed after compaction)
        AddCliProcessFallback(instances, processInfo, projectsDir);

        // 3) VS Code process-based detection (no debug logs for these)
        AddProcessBasedInstances(instances, processInfo, projectsDir);

        return Task.FromResult<IReadOnlyList<AIInstance>>(instances);
    }

    // --- Process discovery (platform-specific) ---

#if WINDOWS
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private record ProcessInfoResult(
        List<CliProcess> CliProcesses,
        List<VscodeProcess> VscodeProcesses);

    private record CliProcess(uint Pid, string? CommandLine, string Workspace);
    private record VscodeProcess(uint Pid, string CommandLine, string Workspace);

    private static ProcessInfoResult LoadProcessInfo()
    {
        var cliProcesses = new List<CliProcess>();
        var vscodeProcesses = new List<VscodeProcess>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name = 'claude.exe' OR Name = 'node.exe'");
            foreach (var obj in searcher.Get())
            {
                try
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    var cmdLine = obj["CommandLine"]?.ToString();
                    var pid = Convert.ToUInt32(obj["ProcessId"]);

                    var isClaudeCode =
                        name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) ||
                        (name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) &&
                         cmdLine?.Contains("claude-code", StringComparison.OrdinalIgnoreCase) == true);

                    if (!isClaudeCode) continue;

                    var isVscode = cmdLine?.Contains("claude-vscode", StringComparison.OrdinalIgnoreCase) == true;

                    var cwd = ProcessHelper.GetCurrentDirectory(pid);
                    if (cwd == null) continue;
                    var workspace = cwd.TrimEnd('\\');

                    if (isVscode)
                        vscodeProcesses.Add(new VscodeProcess(pid, cmdLine!, workspace));
                    else
                        cliProcesses.Add(new CliProcess(pid, cmdLine, workspace));
                }
                catch { }
                finally { obj.Dispose(); }
            }
        }
        catch { }

        return new ProcessInfoResult(cliProcesses, vscodeProcesses);
    }
#elif MACCATALYST
    private static readonly StringComparer PathComparer = StringComparer.Ordinal;

    private record ProcessInfoResult(
        List<CliProcess> CliProcesses,
        List<VscodeProcess> VscodeProcesses);

    private record CliProcess(uint Pid, string? CommandLine, string Workspace);
    private record VscodeProcess(uint Pid, string CommandLine, string Workspace);

    private static ProcessInfoResult LoadProcessInfo()
    {
        var cliProcesses = new List<CliProcess>();
        var vscodeProcesses = new List<VscodeProcess>();

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ps",
                ArgumentList = { "-Aeo", "pid=,args=" },
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return new([], []);

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var trimmed = line.AsSpan().Trim();

                    // parse PID
                    var pidEnd = trimmed.IndexOf(' ');
                    if (pidEnd < 0) continue;
                    if (!uint.TryParse(trimmed[..pidEnd], out var pid)) continue;

                    // rest is the full command line (args)
                    var args = trimmed[(pidEnd + 1)..].TrimStart().ToString();

                    // extract executable name from args
                    var firstSpace = args.IndexOf(' ');
                    var execPath = firstSpace >= 0 ? args[..firstSpace] : args;
                    var execName = Path.GetFileName(execPath);

                    var isClaudeCode =
                        execName.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
                        (execName.Equals("node", StringComparison.OrdinalIgnoreCase) &&
                         args.Contains("claude-code", StringComparison.OrdinalIgnoreCase));

                    if (!isClaudeCode) continue;

                    var isVscode = args.Contains("claude-vscode", StringComparison.OrdinalIgnoreCase);

                    var cwd = ProcessHelper.GetCurrentDirectory(pid);
                    if (cwd == null) continue;
                    var workspace = cwd.TrimEnd('/');

                    if (isVscode)
                        vscodeProcesses.Add(new VscodeProcess(pid, args, workspace));
                    else
                        cliProcesses.Add(new CliProcess(pid, args, workspace));
                }
                catch { }
            }
        }
        catch { }

        return new ProcessInfoResult(cliProcesses, vscodeProcesses);
    }
#else
    private static readonly StringComparer PathComparer = StringComparer.Ordinal;

    private record ProcessInfoResult(
        List<CliProcess> CliProcesses,
        List<VscodeProcess> VscodeProcesses);

    private record CliProcess(uint Pid, string? CommandLine, string Workspace);

    private static ProcessInfoResult LoadProcessInfo()
        => new([], []);
#endif

    private static void AddCliProcessFallback(
        List<AIInstance> instances,
        ProcessInfoResult processInfo, string projectsDir)
    {
#if WINDOWS || MACCATALYST
        // count how many CLI instances we already have per workspace
        var foundCountPerWorkspace = new Dictionary<string, int>(PathComparer);
        foreach (var inst in instances)
        {
            if (!inst.ProviderName.Contains("VS Code"))
            {
                foundCountPerWorkspace.TryGetValue(inst.Workspace, out var c);
                foundCountPerWorkspace[inst.Workspace] = c + 1;
            }
        }

        // group CLI processes by workspace
        var cliByWorkspace = new Dictionary<string, List<CliProcess>>(PathComparer);
        foreach (var cli in processInfo.CliProcesses)
        {
            if (!cliByWorkspace.TryGetValue(cli.Workspace, out var list))
            {
                list = [];
                cliByWorkspace[cli.Workspace] = list;
            }
            list.Add(cli);
        }

        foreach (var (workspace, cliProcs) in cliByWorkspace)
        {
            var alreadyFound = foundCountPerWorkspace.GetValueOrDefault(workspace, 0);
            var deficit = cliProcs.Count - alreadyFound;
            if (deficit <= 0) continue;

            // there are CLI processes we didn't find via debug logs
            // (new sessions without JSONL, or sessions with changed IDs)
            for (var i = 0; i < deficit; i++)
            {
                var cli = cliProcs[cliProcs.Count - 1 - i]; // pick from end (arbitrary)
                instances.Add(new AIInstance
                {
                    Id = $"cli-{cli.Pid}",
                    ProviderName = "Claude Code",
                    Workspace = workspace,
                    Status = AIStatus.Active,
                    LastActivity = DateTime.UtcNow
                });
            }
        }
#endif
    }

    private static void AddProcessBasedInstances(
        List<AIInstance> instances,
        ProcessInfoResult processInfo, string projectsDir)
    {
#if WINDOWS || MACCATALYST
        // track VS Code workspaces to show only one VS Code session per workspace
        // (multiple claude.exe processes per workspace are common due to extension restarts)
        var vscodeWorkspaces = new HashSet<string>(PathComparer);

        foreach (var vsc in processInfo.VscodeProcesses)
        {
            try
            {
                // one VS Code session per workspace
                if (!vscodeWorkspaces.Add(vsc.Workspace))
                    continue;

                // extract session ID from --resume flag if present
                var sessionId = ExtractResumeId(vsc.CommandLine) ?? $"vscode-{vsc.Pid}";

                var status = AIStatus.Active;
                var lastActivity = DateTime.UtcNow;
                var encodedPath = EncodeWorkspacePath(vsc.Workspace);
                var projectFolder = Path.Combine(projectsDir, encodedPath);
                if (Directory.Exists(projectFolder))
                {
                    // if we know the session ID, read its specific JSONL
                    var knownJsonl = !sessionId.StartsWith("vscode-")
                        ? Path.Combine(projectFolder, sessionId + ".jsonl")
                        : null;

                    if (knownJsonl != null && File.Exists(knownJsonl))
                    {
                        status = ReadStatusFromJsonl(knownJsonl);
                        lastActivity = File.GetLastWriteTimeUtc(knownJsonl);
                    }
                    else
                    {
                        // fall back to freshest JSONL in folder
                        var (jsonlStatus, jsonlTime, sid) = ReadFreshestJsonlStatus(projectFolder);
                        status = jsonlStatus;
                        if (jsonlTime != DateTime.MinValue)
                            lastActivity = jsonlTime;
                        if (sid != null && sessionId.StartsWith("vscode-"))
                            sessionId = sid;
                    }
                }

                instances.Add(new AIInstance
                {
                    Id = sessionId,
                    ProviderName = "Claude Code (VS Code)",
                    Workspace = vsc.Workspace,
                    Status = status,
                    LastActivity = lastActivity
                });
            }
            catch { }
        }
#endif
    }

    /// <summary>
    /// Finds the most recently modified JSONL in a project folder and reads its status.
    /// </summary>
    private static (AIStatus status, DateTime lastWrite, string? sessionId) ReadFreshestJsonlStatus(
        string projectFolder)
    {
        string? freshestJsonl = null;
        var latestWrite = DateTime.MinValue;
        foreach (var jsonl in Directory.EnumerateFiles(projectFolder, "*.jsonl"))
        {
            var writeTime = File.GetLastWriteTimeUtc(jsonl);
            if (writeTime > latestWrite)
            {
                latestWrite = writeTime;
                freshestJsonl = jsonl;
            }
        }

        if (freshestJsonl == null)
            return (AIStatus.Active, DateTime.MinValue, null);

        var status = ReadStatusFromJsonl(freshestJsonl);
        var sessionId = Path.GetFileNameWithoutExtension(freshestJsonl);
        return (status, latestWrite, sessionId);
    }

    private static string? ExtractResumeId(string commandLine)
    {
        const string flag = "--resume ";
        var idx = commandLine.IndexOf(flag, StringComparison.Ordinal);
        if (idx < 0) return null;

        var start = idx + flag.Length;
        var end = commandLine.IndexOf(' ', start);
        return end < 0 ? commandLine[start..] : commandLine[start..end];
    }

    private string? LookupWorkspace(string projectsDir, string sessionId)
    {
        if (_workspaceCache.TryGetValue(sessionId, out var cached))
            return cached;

        var jsonlName = sessionId + ".jsonl";
        foreach (var projectFolder in Directory.EnumerateDirectories(projectsDir))
        {
            if (File.Exists(Path.Combine(projectFolder, jsonlName)))
            {
                var workspace = DecodeWorkspacePath(Path.GetFileName(projectFolder));
                _workspaceCache[sessionId] = workspace;
                return workspace;
            }
        }
        return null;
    }

    // JSONL entries can be very large (full code diffs), so read a bigger tail
    private const int JsonlTailBytes = 65536;

    private static readonly TimeSpan ActiveWriteThreshold = TimeSpan.FromSeconds(15);

    private static AIStatus ReadStatusFromJsonl(string jsonlPath)
    {
        try
        {
            // if the file was modified very recently, Claude is actively working
            // (streaming responses, compacting, etc.)
            var lastWrite = File.GetLastWriteTimeUtc(jsonlPath);
            if (DateTime.UtcNow - lastWrite < ActiveWriteThreshold)
                return AIStatus.Working;

            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length;
            if (length == 0) return AIStatus.Active;

            // read a tail window and isolate the last JSONL entry.
            // we must check only the last entry — the previous 512-byte approach
            // could bleed into the prior entry and misidentify the type.
            var readStart = Math.Max(0, length - JsonlTailBytes);
            fs.Seek(readStart, SeekOrigin.Begin);
            var buf = new byte[length - readStart];
            _ = fs.Read(buf, 0, buf.Length);
            var tail = Encoding.UTF8.GetString(buf);

            // extract last entry (everything after the last newline, trimming trailing newlines)
            var trimmed = tail.AsSpan().TrimEnd('\n').TrimEnd('\r');
            var lastNl = trimmed.LastIndexOf('\n');
            var lastEntry = lastNl >= 0 ? trimmed[(lastNl + 1)..] : trimmed;

            // determine message type from the last entry only
            if (lastEntry.Contains(",\"type\":\"user\",", StringComparison.Ordinal) ||
                lastEntry.Contains(",\"type\":\"human\",", StringComparison.Ordinal))
            {
                // an interrupted request leaves a user entry but Claude isn't working
                if (lastEntry.Contains("Request interrupted by user", StringComparison.Ordinal))
                    return AIStatus.WaitingForInput;
                return AIStatus.Working;
            }

            if (!lastEntry.Contains(",\"type\":\"assistant\",", StringComparison.Ordinal))
                return AIStatus.Active;

            // it's an assistant message — check if it contains tool_use
            if (lastEntry.Contains("\"type\":\"tool_use\"", StringComparison.Ordinal))
                return AIStatus.WaitingForPermission;

            return AIStatus.WaitingForInput;
        }
        catch
        {
            return AIStatus.Active;
        }
    }

    // --- Path encoding/decoding (platform-specific) ---

    private static string EncodeWorkspacePath(string path)
    {
#if WINDOWS
        // V:\AIWatcher → V--AIWatcher
        return path.Replace(@":\", "--").Replace(@"\", "--");
#else
        // /Users/david/dev/ai-watcher → -Users-david-dev-ai-watcher
        return path.Replace('/', '-');
#endif
    }

    private static string DecodeWorkspacePath(string folderName)
    {
#if WINDOWS
        // folder names encode paths: "V--AIWatcher" → "V:\AIWatcher"
        // first segment before -- is the drive letter, rest are path separators
        var parts = folderName.Split("--");
        if (parts.Length < 2)
            return folderName;

        // first part is drive letter (or root), rejoin rest with backslash
        return parts[0].ToUpperInvariant() + @":\" + string.Join(@"\", parts[1..]);
#else
        // macOS/Linux: folder encodes path with / replaced by -
        // e.g. "-Users-david-dev-ai-watcher" → "/Users/david/dev/ai-watcher"
        // Decode by probing the filesystem to resolve ambiguity with hyphenated names
        if (!folderName.StartsWith('-'))
            return folderName;

        var segments = folderName[1..].Split('-');
        return ProbeDecodePath(segments, 0, "") ?? "/" + folderName[1..].Replace('-', '/');
#endif
    }

#if !WINDOWS
    /// <summary>
    /// Recursively probes the filesystem to decode an encoded workspace path.
    /// Tries shortest segment first (greedy), with backtracking for hyphenated names.
    /// </summary>
    private static string? ProbeDecodePath(string[] segments, int startIdx, string currentPath)
    {
        if (startIdx >= segments.Length)
            return Directory.Exists(currentPath) ? currentPath : null;

        for (var len = 1; len <= segments.Length - startIdx; len++)
        {
            var component = string.Join('-', segments[startIdx..(startIdx + len)]);
            var nextPath = currentPath + "/" + component;

            // if this is the last component, it must be the final directory
            if (startIdx + len == segments.Length)
            {
                if (Directory.Exists(nextPath))
                    return nextPath;
                continue;
            }

            // intermediate component must exist as a directory
            if (!Directory.Exists(nextPath))
                continue;

            var result = ProbeDecodePath(segments, startIdx + len, nextPath);
            if (result != null)
                return result;
        }

        return null;
    }
#endif
}
