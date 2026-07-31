using System.Globalization;
using System.Text.Json;

namespace ProxmoxBackupServerProtocolSpike;

public sealed record PbsSnapshot(
    string Datastore,
    string BackupType,
    string BackupId,
    long BackupTime,
    string? Namespace);

public static class PbsProtocolPaths
{
    public static string BackupUpgrade(PbsSnapshot snapshot)
        => Build("/api2/json/backup", SnapshotParameters(snapshot));

    public static string ReaderUpgrade(PbsSnapshot snapshot)
        => Build("/api2/json/reader", SnapshotParameters(snapshot));

    public static string DynamicIndexCreate(string archiveName)
        => Build("/dynamic_index", [("archive-name", archiveName)]);

    public static string DynamicChunk(int writerId, string digest)
        => Build("/dynamic_chunk", [("wid", Invariant(writerId)), ("digest", digest)]);

    public static string DynamicIndexAppend(int writerId, IReadOnlyList<string> digests, IReadOnlyList<long> offsets)
        => Build(
            "/dynamic_index",
            [
                ("wid", Invariant(writerId)),
                ("digest-list", JsonSerializer.Serialize(digests)),
                ("offset-list", JsonSerializer.Serialize(offsets))
            ]);

    public static string DynamicIndexClose(int writerId, string checksum, long size, int chunkCount)
        => Build(
            "/dynamic_close",
            [
                ("wid", Invariant(writerId)),
                ("chunk-count", Invariant(chunkCount)),
                ("size", Invariant(size)),
                ("csum", checksum)
            ]);

    public static string Download(string fileName)
        => Build("/download", [("file-name", fileName)]);

    public static string Chunk(string digest)
        => Build("/chunk", [("digest", digest)]);

    private static IReadOnlyList<(string Name, string Value)> SnapshotParameters(PbsSnapshot snapshot)
    {
        var result = new List<(string, string)>
        {
            ("store", snapshot.Datastore),
            ("backup-type", snapshot.BackupType),
            ("backup-id", snapshot.BackupId),
            ("backup-time", Invariant(snapshot.BackupTime))
        };
        if (!string.IsNullOrWhiteSpace(snapshot.Namespace))
            result.Add(("ns", snapshot.Namespace));
        return result;
    }

    private static string Build(string path, IReadOnlyList<(string Name, string Value)> parameters)
        => path + "?" + string.Join("&", parameters.Select(x => $"{Escape(x.Name)}={Escape(x.Value)}"));

    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string Invariant<T>(T value) where T : IFormattable
        => value.ToString(null, CultureInfo.InvariantCulture);
}
