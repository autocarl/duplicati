using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ProxmoxBackupServerProtocolSpike;

public sealed record PbsDynamicIndexEntry(long EndOffset, byte[] Digest);

public static class PbsDynamicIndex
{
    private static ReadOnlySpan<byte> Magic => [28, 145, 78, 165, 25, 186, 179, 205];
    public const int HeaderSize = 4096;
    public const int EntrySize = 40;
    private const int ChecksumOffset = 32;

    public static byte[] ComputeChecksum(IEnumerable<(int EndOffset, byte[] Digest)> entries)
        => ComputeChecksum(entries.Select(x => ((long)x.EndOffset, x.Digest)));

    public static byte[] ComputeChecksum(IEnumerable<(long EndOffset, byte[] Digest)> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long previousOffset = 0;
        Span<byte> offset = stackalloc byte[8];

        foreach (var entry in entries)
        {
            if (entry.EndOffset < previousOffset)
                throw new ArgumentException("Dynamic index offsets must be non-decreasing.", nameof(entries));
            if (entry.Digest.Length != SHA256.HashSizeInBytes)
                throw new ArgumentException("Every dynamic index digest must be SHA-256.", nameof(entries));

            BinaryPrimitives.WriteUInt64LittleEndian(offset, checked((ulong)entry.EndOffset));
            hash.AppendData(offset);
            hash.AppendData(entry.Digest);
            previousOffset = entry.EndOffset;
        }

        return hash.GetHashAndReset();
    }

    public static IReadOnlyList<PbsDynamicIndexEntry> Parse(ReadOnlySpan<byte> index)
    {
        if (index.Length < HeaderSize)
            throw new InvalidDataException("PBS dynamic index is shorter than its header.");
        if (!index[..8].SequenceEqual(Magic))
            throw new InvalidDataException("PBS dynamic index magic does not match.");
        if ((index.Length - HeaderSize) % EntrySize != 0)
            throw new InvalidDataException("PBS dynamic index has a truncated entry.");

        var entriesBytes = index[HeaderSize..];
        var actualChecksum = SHA256.HashData(entriesBytes);
        if (!CryptographicOperations.FixedTimeEquals(index.Slice(ChecksumOffset, SHA256.HashSizeInBytes), actualChecksum))
            throw new InvalidDataException("PBS dynamic index checksum does not match its entries.");

        var entries = new List<PbsDynamicIndexEntry>(entriesBytes.Length / EntrySize);
        long previousOffset = 0;
        for (var offset = 0; offset < entriesBytes.Length; offset += EntrySize)
        {
            var endOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(entriesBytes.Slice(offset, 8)));
            if (endOffset < previousOffset)
                throw new InvalidDataException("PBS dynamic index offsets are not ordered.");

            entries.Add(new PbsDynamicIndexEntry(endOffset, entriesBytes.Slice(offset + 8, SHA256.HashSizeInBytes).ToArray()));
            previousOffset = endOffset;
        }

        return entries;
    }
}
