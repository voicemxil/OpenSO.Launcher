using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenSO.Launcher.Services;

/// <summary>Completeness of a detected The Sims Online asset directory.</summary>
public enum TsoInstallState
{
    /// <summary>No TSO install at the candidate path (missing dir, or an empty/unrelated dir).</summary>
    Absent,
    /// <summary>A partial install — some required content present, but not enough for the game to run.</summary>
    Incomplete,
    /// <summary>All required game files/dirs are present — the client will load it.</summary>
    Complete
}

/// <summary>Result of validating one candidate TSO directory (see <see cref="TsoAssetValidator"/>).</summary>
/// <param name="State">Complete / Incomplete / Absent.</param>
/// <param name="TsoClientDir">The resolved <c>…/TSOClient</c> directory that was inspected (with the game's
/// <c>tuning.dat</c> beside the content dirs), or null when nothing usable was found.</param>
/// <param name="MissingItems">Required files/dirs that were absent (empty when Complete).</param>
public sealed record TsoValidation(TsoInstallState State, string? TsoClientDir, IReadOnlyList<string> MissingItems);

/// <summary>
/// Cheap structural completeness check for an installed The Sims Online asset tree — the guard that keeps
/// the launcher from trusting (or "playing" against) a half-extracted or wrong directory.
///
/// The required set is derived from what the game actually reads, NOT invented:
///   • <c>tuning.dat</c> is the authoritative "TSO is here" file — every one of the game's per-OS
///     locators tests exactly <c>File.Exists(&lt;TSOClient&gt;/tuning.dat)</c> before returning a path
///     (OpenSO: tso.client/Utils/GameLocator/{Windows,MacOS,Linux}Locator.cs), and Content.Init loads it
///     first (tso.content/Content.cs → <c>GlobalTuning = new Tuning(Path.Combine(basePath, "tuning.dat"))</c>).
///   • The content directories the client scans/loads under that base path
///     (tso.content providers + Content.cs GetPath): <c>uigraphics/</c> (Content.OpenFile prepends it),
///     <c>objectdata/</c> (globals/*.iff), <c>packingslips/</c> (catalog/objecttable/purchasable.xml),
///     <c>sounddata/</c> (the HIT/EVT audio banks). A real TSO install always ships all four; their
///     absence means the extract is truncated.
///
/// A candidate may point at either the <c>The Sims Online</c> PARENT (the registry <c>InstallDir</c> and
/// the launcher-managed folder — the game appends <c>\TSOClient\</c>) or directly at the <c>TSOClient</c>
/// dir (the legacy <c>C:\Program Files\Maxis\The Sims Online\TSOClient</c> path). Both forms are resolved.
/// </summary>
public static class TsoAssetValidator
{
    /// <summary>The authoritative file every game locator checks for.</summary>
    public const string TuningFile = "tuning.dat";

    /// <summary>Content directories the client loads under the TSOClient base path (see class remarks).</summary>
    public static readonly IReadOnlyList<string> RequiredDirs =
        new[] { "uigraphics", "objectdata", "packingslips", "sounddata" };

    /// <summary>Classifies <paramref name="candidatePath"/> as Complete / Incomplete / Absent.</summary>
    public static TsoValidation Validate(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return new TsoValidation(TsoInstallState.Absent, null, RequiredNames());

        string full;
        try { full = Path.GetFullPath(candidatePath); }
        catch { return new TsoValidation(TsoInstallState.Absent, null, RequiredNames()); }

        // Resolve which directory actually holds tuning.dat (the "TSOClient" dir):
        //   1. the candidate itself if tuning.dat sits directly in it (legacy TSOClient-form path),
        //   2. else a TSOClient/ subdir (registry InstallDir / launcher-managed "The Sims Online" parent),
        //   3. else assume the candidate was meant to be the TSOClient dir (so a truncated install still
        //      classifies as Incomplete rather than silently Absent).
        string tsoClient;
        if (File.Exists(Path.Combine(full, TuningFile)))
            tsoClient = full;
        else if (Directory.Exists(Path.Combine(full, "TSOClient")))
            tsoClient = Path.Combine(full, "TSOClient");
        else
            tsoClient = full;

        if (!Directory.Exists(tsoClient))
            return new TsoValidation(TsoInstallState.Absent, null, RequiredNames());

        var missing = new List<string>();
        int present = 0;

        if (File.Exists(Path.Combine(tsoClient, TuningFile))) present++;
        else missing.Add(TuningFile);

        foreach (var dir in RequiredDirs)
        {
            var p = Path.Combine(tsoClient, dir);
            // Require the directory to exist AND hold at least one entry — an empty placeholder dir is not
            // real content (a partial extract can leave empty dirs behind).
            bool ok = Directory.Exists(p) && HasAnyEntry(p);
            if (ok) present++;
            else missing.Add(dir);
        }

        int total = 1 + RequiredDirs.Count;
        if (present == 0)
            // Nothing at all — an empty/unrelated directory. Not a TSO install.
            return new TsoValidation(TsoInstallState.Absent, null, missing);
        if (present == total)
            return new TsoValidation(TsoInstallState.Complete, tsoClient, Array.Empty<string>());
        return new TsoValidation(TsoInstallState.Incomplete, tsoClient, missing);
    }

    private static bool HasAnyEntry(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    private static IReadOnlyList<string> RequiredNames()
    {
        var names = new List<string>(1 + RequiredDirs.Count) { TuningFile };
        names.AddRange(RequiredDirs);
        return names;
    }
}
