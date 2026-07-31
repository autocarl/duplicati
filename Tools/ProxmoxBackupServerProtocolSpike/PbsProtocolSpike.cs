using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProxmoxBackupServerProtocolSpike;

public sealed record PbsSpikeEvidence(
    string Status,
    string Snapshot,
    string Archive,
    int PayloadBytes,
    string PayloadSha256,
    string ReadSha256,
    int IndexEntries);

public sealed class PbsProtocolSpike
{
    private const string BackupProtocol = "proxmox-backup-protocol-v1";
    private const string ReaderProtocol = "proxmox-backup-reader-protocol-v1";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("Duplicati PBS protocol spike payload v1\n");

    public async Task<PbsSpikeEvidence> RunAsync(PbsConnectionOptions options, CancellationToken cancellationToken)
    {
        var digest = SHA256.HashData(Payload);
        var digestHex = Convert.ToHexString(digest).ToLowerInvariant();
        var indexChecksum = PbsDynamicIndex.ComputeChecksum([(Payload.Length, digest)]);
        var indexChecksumHex = Convert.ToHexString(indexChecksum).ToLowerInvariant();
        var blob = PbsDataBlob.EncodeUncompressed(Payload);

        await using (var backup = await PbsHttp2Session.OpenAsync(
            options,
            BackupProtocol,
            PbsProtocolPaths.BackupUpgrade(options.Snapshot),
            cancellationToken).ConfigureAwait(false))
        {
            var writerId = ReadDataInt(await SendCheckedAsync(
                backup,
                "POST",
                PbsProtocolPaths.DynamicIndexCreate(options.ArchiveName),
                ReadOnlyMemory<byte>.Empty,
                null,
                "create dynamic index",
                cancellationToken).ConfigureAwait(false));

            await SendCheckedAsync(
                backup,
                "POST",
                PbsProtocolPaths.DynamicChunk(writerId, digestHex),
                blob,
                "application/octet-stream",
                "upload dynamic chunk",
                cancellationToken).ConfigureAwait(false);

            await SendCheckedAsync(
                backup,
                "PUT",
                PbsProtocolPaths.DynamicIndexAppend(writerId, [digestHex], [Payload.LongLength]),
                ReadOnlyMemory<byte>.Empty,
                null,
                "append dynamic index",
                cancellationToken).ConfigureAwait(false);

            await SendCheckedAsync(
                backup,
                "POST",
                PbsProtocolPaths.DynamicIndexClose(writerId, indexChecksumHex, Payload.LongLength, 1),
                ReadOnlyMemory<byte>.Empty,
                null,
                "close dynamic index",
                cancellationToken).ConfigureAwait(false);

            await SendCheckedAsync(
                backup,
                "POST",
                "/finish",
                ReadOnlyMemory<byte>.Empty,
                null,
                "finish PBS snapshot",
                cancellationToken).ConfigureAwait(false);
        }

        byte[] downloadedIndex;
        byte[] downloadedBlob;
        await using (var reader = await PbsHttp2Session.OpenAsync(
            options,
            ReaderProtocol,
            PbsProtocolPaths.ReaderUpgrade(options.Snapshot),
            cancellationToken).ConfigureAwait(false))
        {
            downloadedIndex = (await SendCheckedAsync(
                reader,
                "GET",
                PbsProtocolPaths.Download(options.ArchiveName),
                ReadOnlyMemory<byte>.Empty,
                null,
                "download dynamic index",
                cancellationToken).ConfigureAwait(false)).Body;
            var entries = PbsDynamicIndex.Parse(downloadedIndex);
            if (entries.Count != 1)
                throw new InvalidDataException($"Expected exactly one dynamic index entry, got {entries.Count}.");
            if (!CryptographicOperations.FixedTimeEquals(entries[0].Digest, digest))
                throw new InvalidDataException("Downloaded dynamic index references an unexpected chunk digest.");
            if (entries[0].EndOffset != Payload.LongLength)
                throw new InvalidDataException("Downloaded dynamic index has an unexpected end offset.");

            downloadedBlob = (await SendCheckedAsync(
                reader,
                "GET",
                PbsProtocolPaths.Chunk(digestHex),
                ReadOnlyMemory<byte>.Empty,
                null,
                "download dynamic chunk",
                cancellationToken).ConfigureAwait(false)).Body;
        }

        var recovered = PbsDataBlob.Decode(downloadedBlob);
        var readDigest = SHA256.HashData(recovered);
        if (!CryptographicOperations.FixedTimeEquals(digest, readDigest) || !recovered.AsSpan().SequenceEqual(Payload))
            throw new InvalidDataException("End-to-end PBS payload integrity verification failed.");

        var snapshot = $"{options.Snapshot.BackupType}/{options.Snapshot.BackupId}/{options.Snapshot.BackupTime}";
        return new PbsSpikeEvidence(
            "ok",
            snapshot,
            options.ArchiveName,
            Payload.Length,
            digestHex,
            Convert.ToHexString(readDigest).ToLowerInvariant(),
            1);
    }

    private static async Task<PbsHttp2Response> SendCheckedAsync(
        PbsHttp2Session session,
        string method,
        string path,
        ReadOnlyMemory<byte> body,
        string? contentType,
        string operation,
        CancellationToken cancellationToken)
    {
        var response = await session.SendAsync(method, path, body, contentType, cancellationToken).ConfigureAwait(false);
        PbsHttp2Session.EnsureSuccess(response, operation);
        return response;
    }

    private static int ReadDataInt(PbsHttp2Response response)
    {
        using var document = JsonDocument.Parse(response.Body);
        if (!document.RootElement.TryGetProperty("data", out var data))
            throw new InvalidDataException("PBS JSON response is missing the data property.");
        return data.ValueKind switch
        {
            JsonValueKind.Number when data.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(data.GetString(), out var value) => value,
            _ => throw new InvalidDataException("PBS JSON data property is not an integer.")
        };
    }
}
