using System.Buffers.Binary;
using System.IO.Hashing;

namespace ProxmoxBackupServerProtocolSpike;

public static class PbsDataBlob
{
    private static ReadOnlySpan<byte> UncompressedMagic => [66, 171, 56, 7, 190, 131, 112, 161];
    private const int HeaderSize = 12;

    public static byte[] EncodeUncompressed(ReadOnlySpan<byte> payload)
    {
        var result = new byte[HeaderSize + payload.Length];
        UncompressedMagic.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), Crc32.HashToUInt32(payload));
        payload.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    public static byte[] Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderSize)
            throw new InvalidDataException("PBS data blob is shorter than its header.");
        if (!encoded[..8].SequenceEqual(UncompressedMagic))
            throw new InvalidDataException("PBS data blob is not an uncompressed blob supported by this spike.");

        var payload = encoded[HeaderSize..];
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(encoded.Slice(8, 4));
        var actualCrc = Crc32.HashToUInt32(payload);
        if (actualCrc != expectedCrc)
            throw new InvalidDataException($"PBS data blob CRC32 mismatch: expected {expectedCrc:x8}, got {actualCrc:x8}.");

        return payload.ToArray();
    }
}
