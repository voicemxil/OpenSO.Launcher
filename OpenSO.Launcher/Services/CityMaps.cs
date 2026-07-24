using System.IO;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Resolves a shard's city map to its thumbnail file on disk (the SERVER STATUS card banner).
///
/// The map id a shard advertises (/userapi/status shards[].map, i.e. the fso_shards.map column)
/// spans TWO content sets, and the game client picks between them purely on the number: ids &gt;= 100
/// are FreeSO/OpenSO-authored maps that ship inside the client (Content/Cities/city_XXXX, PNG
/// assets), while ids &lt; 100 are the 30 original TSO maps that come from the user's The Sims Online
/// install (&lt;TSOClient&gt;/cities/city_XXXX, BMP assets). Same split as the client's
/// CityContent.LoadContent and UICitySelector.
///
/// Production Genesis runs map 0013 — an original TSO map — so a launcher that only knew the
/// Content/Cities half showed no banner at all for the live server.
/// </summary>
public static class CityMaps
{
    /// <summary>Ids below this come from the original TSO install; ids at or above it ship with the client.</summary>
    private const int FirstFsoMap = 100;

    /// <summary>
    /// The thumbnail file for <paramref name="map"/>, or null when it can't be resolved — an
    /// unparseable id, a missing install for that content set, or a map that ships no thumbnail.
    /// <paramref name="fsoDir"/> is the OpenSO client dir; <paramref name="tsoClientDir"/> is the
    /// resolved TSOClient dir (TsoValidation.TsoClientDir — already normalized across the
    /// "The Sims Online" parent form and the legacy ...\The Sims Online\TSOClient form). Either may
    /// be null when that component isn't installed.
    /// </summary>
    public static string? ResolveThumbnail(string? map, string? fsoDir, string? tsoClientDir)
    {
        // fso_shards.map is a free-form varchar with no server-side validation or padding, so treat
        // anything non-numeric as "no banner" rather than throwing the way the client's int.Parse would.
        if (!int.TryParse(map?.Trim(), out var id) || id < 0) return null;

        var fso = id >= FirstFsoMap;
        // Folders are always 4-digit zero-padded even when the column value isn't ("101" -> city_0101).
        var dir = fso
            ? Combine(fsoDir, "Content", "Cities", "city_" + id.ToString("0000"))
            : Combine(tsoClientDir, "cities", "city_" + id.ToString("0000"));
        if (dir == null) return null;

        // Extension follows the content set, but the client sniffs it per folder (CityMap.cs falls back
        // to png when elevation.bmp is absent), so accept either rather than blanking an odd-packed map.
        foreach (var ext in fso ? new[] { "png", "bmp" } : new[] { "bmp", "png" })
        {
            var path = Path.Combine(dir, "thumbnail." + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Path.Combine that propagates "no install" (null/blank root) as null.</summary>
    private static string? Combine(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var path = root;
        foreach (var part in parts) path = Path.Combine(path, part);
        return path;
    }
}
