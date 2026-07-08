using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Turns a raw "the file is in use" failure into a clear, human explanation that names the file and — on
/// Windows, via the Restart Manager — the actual process(es) holding it. The old behavior dropped a bare
/// <see cref="IOException"/> message ("The process cannot access the file '…' because it is being used by
/// another process") into the progress subtext; this makes the launcher say plainly which program to close.
/// </summary>
public static class FileLocks
{
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;

    /// <summary>True if <paramref name="ex"/> (or any inner exception) is a file-sharing/lock violation.</summary>
    public static bool IsFileInUse(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            int code = e.HResult & 0xFFFF;
            if (e is IOException && (code == ERROR_SHARING_VIOLATION || code == ERROR_LOCK_VIOLATION)) return true;
            if (e.Message.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    /// <summary>Pulls the quoted file path out of a framework I/O error message, if present.</summary>
    public static string? TryExtractPath(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var m = Regex.Match(e.Message, "'([^']+)'");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Builds a loud, specific message for a file-in-use failure: the file name plus, when we can find them,
    /// the processes locking it ("OpenSO (PID 1234)"). Falls back to a clear generic instruction.
    /// </summary>
    public static string Explain(Exception ex)
    {
        var path = TryExtractPath(ex);
        var file = string.IsNullOrEmpty(path) ? "A game file" : Path.GetFileName(path);
        var lockers = path != null ? WhoIsLocking(path) : new List<string>();

        if (lockers.Count > 0)
            return $"{file} is locked by {string.Join(", ", lockers)}. Close it and try again.";

        return $"{file} is in use by another program. If OpenSO is open, close it completely — and check Task Manager for a lingering OpenSO process — then try again.";
    }

    /// <summary>
    /// Returns "Name (PID nnnn)" for each process currently holding <paramref name="path"/> open, via the
    /// Windows Restart Manager. Empty on non-Windows or if the query fails (best-effort diagnostics only).
    /// </summary>
    public static List<string> WhoIsLocking(string path)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            foreach (var p in RestartManager.GetLockingProcesses(path))
            {
                try { result.Add($"{p.ProcessName} (PID {p.Id})"); }
                finally { p.Dispose(); }
            }
        }
        catch { /* Restart Manager unavailable/failed — the generic message still helps */ }
        return result;
    }
}

/// <summary>
/// Minimal P/Invoke wrapper over the Windows Restart Manager (rstrtmgr.dll) to find which processes hold a
/// file open. This is the same mechanism installers use to say "close these apps before continuing".
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RestartManager
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    private const int RM_INVALID_SESSION = -1;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    private enum RM_APP_TYPE { RmUnknownApp = 0, RmMainWindow = 1, RmOtherWindow = 2, RmService = 3, RmExplorer = 4, RmConsole = 5, RmCritical = 1000 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)] public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    public static List<System.Diagnostics.Process> GetLockingProcesses(string path)
    {
        var procs = new List<System.Diagnostics.Process>();
        var key = Guid.NewGuid().ToString();
        if (RmStartSession(out uint handle, 0, key) != 0) return procs;
        try
        {
            if (RmRegisterResources(handle, 1, new[] { path }, 0, null, 0, null) != 0) return procs;

            uint pnProcInfoNeeded = 0, pnProcInfo = 0, rebootReasons = 0;
            const int ERROR_MORE_DATA = 234;
            int rv = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null, ref rebootReasons);
            if (rv == ERROR_MORE_DATA && pnProcInfoNeeded > 0)
            {
                var info = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;
                if (RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, info, ref rebootReasons) == 0)
                {
                    for (int i = 0; i < pnProcInfo; i++)
                    {
                        try { procs.Add(System.Diagnostics.Process.GetProcessById(info[i].Process.dwProcessId)); }
                        catch { /* process already gone / access denied — skip */ }
                    }
                }
            }
        }
        finally { RmEndSession(handle); }
        return procs;
    }
}
