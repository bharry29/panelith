using System.Buffers.Binary;
using OmniPanel.Devices.Protocols.Thermalright;

namespace OmniPanel.Devices.Tests.Protocols;

/// <summary>
/// The LY handshake is the only way to tell a 9.16" Trofeo from an 11.3", and
/// the height field is not always the visible height, so these cases are pinned
/// individually.
/// </summary>
public sealed class TrofeoLyGeometryTests
{
    [Theory]
    [InlineData(0x01, 1920, 480)]
    [InlineData(0x02, 1920, 480)]
    [InlineData(0x03, 1920, 480)]
    [InlineData(0x05, 1920, 400)]
    public void ModelByteSelectsTheSizeThermalrightSells(byte model, int width, int height)
    {
        var reply = ValidReply();
        reply[20] = model;

        Assert.Equal((width, height), TrofeoLyGeometry.FromHandshake(reply));
    }

    [Fact]
    public void TrustsAnExplicitSizeWhenItIsOneTheFamilyShips()
    {
        var reply = ValidReply();
        reply[20] = 0x01;
        WriteSize(reply, 1920, 462);

        Assert.Equal((1920, 462), TrofeoLyGeometry.FromHandshake(reply));
    }

    [Fact]
    public void IgnoresAnInternalBufferHeightAndUsesTheModelByte()
    {
        // Firmware has been observed reporting 599 here. That is a buffer, not
        // the panel, and driving it at 599 would send a frame the screen cannot
        // show.
        var reply = ValidReply();
        reply[20] = 0x05;
        WriteSize(reply, 1920, 599);

        Assert.Equal((1920, 400), TrofeoLyGeometry.FromHandshake(reply));
    }

    [Fact]
    public void ReturnsNullForAReplyThatIdentifiesNothing()
    {
        var reply = ValidReply();
        reply[20] = 0x2A;

        Assert.Null(TrofeoLyGeometry.FromHandshake(reply));
    }

    private static byte[] ValidReply()
    {
        var reply = new byte[LyProtocol.ResponseSize];
        reply[0] = 3;
        reply[1] = 0xFF;
        reply[8] = 1;
        return reply;
    }

    private static void WriteSize(byte[] reply, int width, int height)
    {
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(24, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(28, 4), height);
    }
}
