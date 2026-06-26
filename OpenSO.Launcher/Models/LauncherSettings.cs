using System;
using System.IO;
using System.Text.Json;

namespace OpenSO.Launcher.Models;

/// <summary>
/// Persisted launcher + in-game preferences (JSON in the launcher's roaming app-data dir). Mirrors the
/// upstream launcher's userSettings.ini (Game.GraphicsMode/3DMode/RefreshRate, Launcher.DesktopNotifications,
/// Launcher.OnGameClose). Saved on every change so the SETTINGS page is sticky across runs.
/// </summary>
public sealed class LauncherSettings
{
    public string GraphicsMode { get; set; } = "OpenGL";
    public bool Enable3D { get; set; } = false;
    public int RefreshRate { get; set; } = 60;
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
