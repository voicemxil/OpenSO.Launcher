using System;
using System.IO;
using System.Text.Json;

namespace OpenSO.Launcher.Models;

/// <summary>
/// Persisted launcher + in-game preferences (JSON in the launcher's roaming app-data dir). Mirrors the
/// upstream launcher's userSettings.ini (Game.GraphicsMode/3DMode). Saved on every change so the
/// SETTINGS page is sticky across runs.
/// </summary>
public sealed class LauncherSettings
{
    public string GraphicsMode { get; set; } = "OpenGL";
    public bool Enable3D { get; set; } = false;
    public bool AutoUpdateLauncher { get; set; } = true;
    public bool LiveNotifications { get; set; } = true;
    public string ClosingBehavior { get; set; } = "Exit launcher";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSO Launcher", "settings.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new LauncherSettings();
        }
        catch { /* corrupt/missing -> defaults */ }
        return new LauncherSettings();
    }

    private static readonly object SaveLock = new();

    public void Save()
    {
        try
        {
            // Serialize BEFORE taking the lock, write-temp-then-rename INSIDE it: rapid successive
            // changes (every settings control saves on change) can't interleave writes, and a crash
            // mid-write can only lose the .tmp — never truncate the real settings.json.
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            lock (SaveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true); // same-volume rename: atomic replace
            }
        }
        catch { /* non-fatal */ }
    }
}
