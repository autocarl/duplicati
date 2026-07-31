using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ProxmoxBackupServerProtocolSpike;

namespace ProxmoxBackupServerProtocolSpike.Tests;

[TestFixture]
public sealed class PbsDataBlobTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("Duplicati PBS protocol spike payload v1\n");

    [Test]
    public void EncodeUncompressedUsesDocumentedMagicAndIeeeCrc32LittleEndian()
    {
        var encoded = PbsDataBlob.EncodeUncompressed(Payload);

        Assert.That(encoded[..8], Is.EqualTo(new byte[] { 66, 171, 56, 7, 190, 131, 112, 161 }));
        Assert.That(encoded[8..12], Is.EqualTo(new byte[] { 0xa4, 0x80, 0xf1, 0xd2 }));
        Assert.That(encoded[12..], Is.EqualTo(Payload));
    }

    [Test]
    public void DecodeUncompressedRoundTripsPayload()
    {
        var decoded = PbsDataBlob.Decode(PbsDataBlob.EncodeUncompressed(Payload));
        Assert.That(decoded, Is.EqualTo(Payload));
    }

    [Test]
    public void DecodeRejectsCorruptPayload()
    {
        var encoded = PbsDataBlob.EncodeUncompressed(Payload);
        encoded[^1] ^= 0xff;
        Assert.That(() => PbsDataBlob.Decode(encoded), Throws.TypeOf<InvalidDataException>());
    }
}

[TestFixture]
public sealed class PbsDynamicIndexTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("Duplicati PBS protocol spike payload v1\n");
    private const string ExpectedDigest = "37237816a998891048d38b72469882e9e29ab3626ac3593f09d35712ddef81a3";
    private const string ExpectedChecksum = "f415f9c96a991ffcfd1cd12d0d45d6ce61847b9f3c26ff2d67ff19c666db8775";

    [Test]
    public void ComputeChecksumUsesLittleEndianEndOffsetFollowedByDigest()
    {
        var digest = SHA256.HashData(Payload);
        var checksum = PbsDynamicIndex.ComputeChecksum([(Payload.Length, digest)]);
        Assert.That(Convert.ToHexString(checksum).ToLowerInvariant(), Is.EqualTo(ExpectedChecksum));
    }

    [Test]
    public void ParseReadsAndValidatesOneEntry()
    {
        var digest = Convert.FromHexString(ExpectedDigest);
        var checksum = Convert.FromHexString(ExpectedChecksum);
        var bytes = new byte[PbsDynamicIndex.HeaderSize + PbsDynamicIndex.EntrySize];
        new byte[] { 28, 145, 78, 165, 25, 186, 179, 205 }.CopyTo(bytes, 0);
        checksum.CopyTo(bytes, 32);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(PbsDynamicIndex.HeaderSize, 8), (ulong)Payload.Length);
        digest.CopyTo(bytes, PbsDynamicIndex.HeaderSize + 8);

        var entries = PbsDynamicIndex.Parse(bytes);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].EndOffset, Is.EqualTo(Payload.Length));
            Assert.That(Convert.ToHexString(entries[0].Digest).ToLowerInvariant(), Is.EqualTo(ExpectedDigest));
        });
    }

    [Test]
    public void ParseRejectsIndexChecksumMismatch()
    {
        var digest = Convert.FromHexString(ExpectedDigest);
        var bytes = new byte[PbsDynamicIndex.HeaderSize + PbsDynamicIndex.EntrySize];
        new byte[] { 28, 145, 78, 165, 25, 186, 179, 205 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(PbsDynamicIndex.HeaderSize, 8), (ulong)Payload.Length);
        digest.CopyTo(bytes, PbsDynamicIndex.HeaderSize + 8);
        Assert.That(() => PbsDynamicIndex.Parse(bytes), Throws.TypeOf<InvalidDataException>());
    }
}
