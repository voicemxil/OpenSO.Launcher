using System.Text.Json.Serialization;

namespace OpenSO.Launcher.Models;

/// <summary>
/// System.Text.Json <b>source-generated</b> metadata for the launcher's two reflection-serialized DTOs:
/// the persisted <see cref="LauncherSettings"/> (serialize + deserialize) and the server-status payload
/// <see cref="ServerStatus"/> (deserialize; its referenced <see cref="ShardSummary"/> / <see cref="TopLot"/>
/// are pulled in automatically). Every other JSON path in the launcher is <c>JsonDocument.Parse</c> (a DOM
/// reader — reflection-free and already trim-safe); these two are the only <c>JsonSerializer.Serialize/
/// Deserialize&lt;T&gt;</c> uses, which are reflection-based and would emit IL2026/IL3050 trimmer warnings
/// and can silently lose members under <c>PublishTrimmed</c>. Routing them through this compile-time
/// context makes them trim-safe with zero reflection.
///
/// Options are baked into the context: <c>WriteIndented</c> matches the old settings-file writer, and
/// <c>PropertyNameCaseInsensitive</c> matches the old status reader (server may send camelCase). See
/// BUILD_AND_TEST.md → "Published size &amp; trimming".
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LauncherSettings))]
[JsonSerializable(typeof(ServerStatus))]
[JsonSerializable(typeof(System.Collections.Generic.List<Services.NewsItem>))] // NewsService's offline feed cache
internal sealed partial class LauncherJsonContext : JsonSerializerContext;
