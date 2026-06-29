namespace OpenSO.Launcher.Models;

/// <summary>Live server status from GET {api}/userapi/status (deserialized case-insensitively).</summary>
public sealed class ServerStatus
{
    public System.DateTime ServerTime { get; set; }
    public string? GameVersion { get; set; }
    public int PlayersOnline { get; set; }
    public int LotsOnline { get; set; }
    public ShardSummary[]? Shards { get; set; }
    public TopLot[]? TopLots { get; set; }
}

public sealed class ShardSummary
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public string? Version { get; set; }
    public int PlayersOnline { get; set; }
    public int LotsOnline { get; set; }
    public int OwnedLots { get; set; }
}

public sealed class TopLot
{
    public int ShardId { get; set; }
    public string? Name { get; set; }
    public uint Location { get; set; }
    public int Players { get; set; }

    /// <summary>Lot render thumbnail URL, set by the VM: {api}/userapi/city/{ShardId}/{Location}.png.</summary>
    public string? ThumbnailUrl { get; set; }
}
