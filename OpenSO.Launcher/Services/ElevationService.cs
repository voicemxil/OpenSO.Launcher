using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Runs a shell command with administrator/root privileges — the cross-platform replacement for the
/// upstream launcher's `sudo-prompt` dependency, used by the Mono/SDL installers (apt/pacman,
/// `installer -pkg`, `hdiutil`/framework copy) and eventually for Windows registry writes.
///
/// - Linux: `pkexec` (graphical polkit prompt) if available, else `sudo`.
/// - macOS: AppleScript `do shell script ... with administrator privileges` (native auth dialog).
/// - Windows: relaunches the command elevated via ShellExecute "runas" (UAC prompt).
/// </summary>
public sealed class ElevationService
{
    public sealed record ElevationResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    /// <summary>
    /// Quote a string for safe interpolation into a POSIX shell command (sh -c / `do shell script`):
    /// single-quote it and escape embedded single quotes ('foo' -> 'foo'\''bar'). Callers building
    /// elevated commands MUST pass every runtime value (file paths especially) through this — naive
    /// escaping lets a crafted path inject `;`/`&&`/`$( )` into a root shell.
    /// </summary>
    public static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Run <paramref name="command"/> elevated. <paramref name="prompt"/> is shown in the
    /// auth dialog where the OS supports it (macOS).</summary>
    public async Task<ElevationResult> RunAsync(string command, string prompt = "OpenSO Launcher needs permission",
        CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return await RunMacAsync(command, prompt, ct);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return await RunLinuxAsync(command, ct);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await RunWindowsAsync(command, ct);
        throw new PlatformNotSupportedException("Elevation not supported on this OS.");
    }

    private static async Task<ElevationResult> RunLinuxAsync(string command, CancellationToken ct)
    {
        // Prefer pkexec (GUI polkit prompt); fall back to sudo (works if a terminal/askpass exists).
        var elevator = WhichExists("pkexec") ? "pkexec" : "sudo";
        return await ExecAsync(elevator, new[] { "/bin/sh", "-c", command }, ct);
    }

    private static async Task<ElevationResult> RunMacAsync(string command, string prompt, CancellationToken ct)
    {
        // osascript shows the native admin auth dialog. Escape embedded quotes for AppleScript.
        var escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script = $"do shell script \"{escaped}\" with prompt \"{prompt}\" with administrator privileges";
        return await ExecAsync("osascript", new[] { "-e", script }, ct);
    }

    private static async Task<ElevationResult> RunWindowsAsync(string command, CancellationToken ct)
    {
        // ShellExecute "runas" triggers UAC. We can't capture stdout/stderr through ShellExecute,
        // so we run the command via cmd and rely on the exit code.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            return new ElevationResult(p.ExitCode, "", "");
        }
        catch (System.ComponentModel.Win32Exception ex) // user declined UAC
        {
            return new ElevationResult(1223, "", "Elevation was cancelled: " + ex.Message);
        }
    }

    private static async Task<ElevationResult> ExecAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new ElevationResult(p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static bool WhichExists(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/which", Arguments = exe,
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            })!;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
