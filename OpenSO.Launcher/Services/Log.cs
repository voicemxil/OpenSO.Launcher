using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Minimal zero-dependency file logger. Writes to &lt;AppData&gt;/OpenSO Launcher/logs/launcher-yyyyMMdd.log
/// (one file per day, older than <see cref="RetentionDays"/> pruned on init), and mirrors to the
/// debugger/console. Deliberately hand-rolled rather than pulling Serilog: the launcher ships as a
/// self-contained binary and the rest of the codebase avoids non-essential dependencies. Every method
/// is exception-safe — logging must never be the thing that crashes an install.
/// </summary>
public static class Log
{
    private const int RetentionDays = 7;
    private static readonly object Gate = new();
    private static string? _dir;

    private static string Dir => _dir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSO Launcher", "logs");

    private static string FilePath =>
        Path.Combine(Dir, $"launcher-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Creates the log dir, prunes old files, and writes a session header. Call once at startup.</summary>
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            PruneOldLogs();
            Write("INFO", $"=== OpenSO Launcher {AppVersion()} started === OS={RuntimeInformation.OSDescription} " +
                          $"arch={RuntimeInformation.OSArchitecture} runtime={RuntimeInformation.FrameworkDescription}");
        }
        catch { /* logging must never throw */ }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", Format(message, ex));
    public static void Error(string message, Exception? ex = null) => Write("ERROR", Format(message, ex));

    private static string Format(string message, Exception? ex) =>
        ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        try { System.Diagnostics.Debug.WriteLine(line); } catch { }
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { /* out of disk / no perms — drop the line rather than crash */ }
    }

    private static string AppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var f in Directory.EnumerateFiles(Dir, "launcher-*.log")
                         .Where(f => File.GetLastWriteTime(f) < cutoff))
                try { File.Delete(f); } catch { /* skip locked file */ }
        }
        catch { /* dir just created / unreadable — nothing to prune */ }
    }
}
