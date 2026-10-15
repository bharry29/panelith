using System.Buffers.Binary;
using OmniPanel.Devices.Protocols.Thermalright;
using Xunit;

namespace OmniPanel.Devices.Tests.Protocols;

/// <summary>
/// Byte-exact checks on the Thermalright LY framing. The panel gives no useful
/// feedback when a header is wrong -- it simply shows nothing -- so these
/// assertions are the only practical way to keep the layout honest.
/// </summary>
public sealed class LyProtocolTests
{
    [Fact]
    public void HandshakeIsTwoKilobytesWithTheDocumentedPreamble()
    {
        var handshake = LyProtocol.BuildHandshake();

        Assert.Equal(2048, handshake.Length);
        Assert.Equal<byte>(0x02, handshake[0]);
        Assert.Equal<byte>(0xFF, handshake[1]);
        Assert.Equal<byte>(0x01, handshake[8]);
        Assert.All(handshake[9..], b => Assert.Equal<byte>(0, b));
    }

    [Theory]
    [InlineData(LyProtocol.ProductIdLy, 1)]
    [InlineData(LyProtocol.ProductIdLy1, 2)]
    public void CommandByteFollowsTheFirmwareVariant(int productId, byte expected)
    {
        Assert.Equal(expected, LyProtocol.CommandFor(productId));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(495, 1)]
    [InlineData(496, 2)]
    [InlineData(497, 2)]
    [InlineData(992, 3)]
    public void ChunkCountAlwaysLeavesATerminatingChunk(int payloadSize, int expected)
    {
        Assert.Equal(expected, LyProtocol.ChunkCountFor(payloadSize));
    }

    [Fact]
    public void OriginalFirmwarePadsTheChunkCountToWholeTwoKilobyteGroups()
    {
        // One chunk of payload pads out to four chunks, which is 2048 bytes.
        Assert.Equal(2048, LyProtocol.TransferSizeFor(100, LyProtocol.ProductIdLy));
        Assert.Equal(2048, LyProtocol.TransferSizeFor(1000, LyProtocol.ProductIdLy));
        Assert.Equal(4096, LyProtocol.TransferSizeFor(2000, LyProtocol.ProductIdLy));
    }

    [Fact]
    public void RevisedFirmwareSendsExactlyTheChunksItNeeds()
    {
        Assert.Equal(512, LyProtocol.TransferSizeFor(100, LyProtocol.ProductIdLy1));
        Assert.Equal(1024, LyProtocol.TransferSizeFor(600, LyProtocol.ProductIdLy1));
    }

    [Fact]
    public void PaddingKeepsTheTransferAlignedToTheBulkWriteSize()
    {
        // Every transfer must divide into whole 2 KiB tail writes, which is what
        // makes the 4 KiB write loop terminate exactly on the final byte.
        for (var payload = 1; payload < 60_000; payload += 997)
        {
            var size = LyProtocol.TransferSizeFor(payload, LyProtocol.ProductIdLy);
            Assert.Equal(0, size % 2048);
        }
    }

    [Fact]
    public void ChunkHeaderCarriesTheDocumentedFields()
    {
        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);

        var frame = LyProtocol.BuildFrame(payload, LyProtocol.ProductIdLy);

        Assert.Equal(2048, frame.Length);

        var chunkCount = LyProtocol.ChunkCountFor(payload.Length);
        Assert.Equal(3, chunkCount);

        for (var index = 0; index < chunkCount; index++)
        {
            var header = frame.AsSpan(index * LyProtocol.ChunkSize, LyProtocol.ChunkHeaderSize);

            Assert.Equal<byte>(0x01, header[0]);
            Assert.Equal<byte>(0xFF, header[1]);
            Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(header[2..6]));
            Assert.Equal<byte>(1, header[8]);
            Assert.Equal(chunkCount, BinaryPrimitives.ReadUInt16LittleEndian(header[9..11]));
            Assert.Equal(index, BinaryPrimitives.ReadUInt16LittleEndian(header[11..13]));
            Assert.Equal(0, header[13]);
            Assert.Equal(0, header[14]);
            Assert.Equal(0, header[15]);

            var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
            var expectedLength = index == chunkCount - 1
                ? payload.Length % LyProtocol.ChunkDataSize
                : LyProtocol.ChunkDataSize;
            Assert.Equal(expectedLength, declaredLength);
        }
    }

    [Fact]
    public void PayloadBytesSurviveChunkingInOrder()
    {
        var payload = new byte[1500];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        var frame = LyProtocol.BuildFrame(payload, LyProtocol.ProductIdLy1);

        var reassembled = new List<byte>(payload.Length);
        var chunkCount = LyProtocol.ChunkCountFor(payload.Length);
        for (var index = 0; index < chunkCount; index++)
        {
            var chunk = frame.AsSpan(index * LyProtocol.ChunkSize, LyProtocol.ChunkSize);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(chunk[6..8]);
            reassembled.AddRange(chunk.Slice(LyProtocol.ChunkHeaderSize, length).ToArray());
        }

        Assert.Equal(payload, reassembled);
    }

    [Fact]
    public void ExactMultiplePayloadEndsWithAZeroLengthChunk()
    {
        var payload = new byte[LyProtocol.ChunkDataSize * 2];

        var frame = LyProtocol.BuildFrame(payload, LyProtocol.ProductIdLy1);

        var lastHeader = frame.AsSpan(2 * LyProtocol.ChunkSize, LyProtocol.ChunkHeaderSize);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(lastHeader[6..8]));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(lastHeader[9..11]));
    }

    [Fact]
    public void PaddingChunksAreEntirelyZero()
    {
        var payload = new byte[100];
        Array.Fill(payload, (byte)0xAB);

        var frame = LyProtocol.BuildFrame(payload, LyProtocol.ProductIdLy);

        // One real chunk, then three zero chunks of padding.
        Assert.All(frame[LyProtocol.ChunkSize..], b => Assert.Equal<byte>(0, b));
    }

    [Fact]
    public void BuildFrameRejectsAnUndersizedDestination()
    {
        var payload = new byte[1000];
        var destination = new byte[512];

        var exception = Assert.Throws<ArgumentException>(
            () => LyProtocol.BuildFrame(payload, LyProtocol.ProductIdLy, destination));

        Assert.Contains("2048 bytes of chunk buffer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFrameOverwritesStaleBytesInAReusedBuffer()
    {
        // The render loop reuses one buffer across frames, so a shorter frame
        // must not leave the tail of a longer previous frame behind.
        var buffer = new byte[8192];
        Array.Fill(buffer, (byte)0xEE);

        var written = LyProtocol.BuildFrame(new byte[10], LyProtocol.ProductIdLy, buffer);

        Assert.All(buffer[LyProtocol.ChunkHeaderSize..written], b => Assert.Equal<byte>(0, b));
    }

    [Theory]
    [InlineData(new byte[] { 3, 0xFF, 0, 0, 0, 0, 0, 0, 1 }, false)]
    public void HandshakeValidationRejectsTruncatedReplies(byte[] response, bool expected)
    {
        Assert.Equal(expected, LyProtocol.IsValidHandshakeResponse(response));
    }

    [Fact]
    public void HandshakeValidationAcceptsBothDocumentedAndObservedReplies()
    {
        var documented = new byte[37];
        documented[0] = 3;
        documented[1] = 0xFF;
        documented[8] = 1;

        var observed = (byte[])documented.Clone();
        observed[8] = 2;

        Assert.True(LyProtocol.IsValidHandshakeResponse(documented));
        Assert.True(LyProtocol.IsValidHandshakeResponse(observed));
    }

    [Fact]
    public void HandshakeValidationRejectsSomethingElseEntirely()
    {
        var response = new byte[512];
        response[0] = 0x55;

        Assert.False(LyProtocol.IsValidHandshakeResponse(response));
    }
}
