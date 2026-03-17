using Xunit;
using ScumOxygen.Rcon.Protocol;
using System.Buffers;

namespace ScumOxygen.Tests;

public class RconPacketTests
{
    [Fact]
    public void Serialize_ExecuteCommand_ReturnsCorrectBytes()
    {
        var packet = new RconPacket(1, RconPacketType.SERVERDATA_EXECCOMMAND, "listplayers");
        var writer = new ArrayBufferWriter<byte>();
        
        packet.Serialize(writer);
        var data = writer.WrittenSpan;

        // Length (4 bytes)
        var length = BitConverter.ToInt32(data.Slice(0, 4));
        Assert.True(length > 0);
        
        // ID (4 bytes)
        var id = BitConverter.ToInt32(data.Slice(4, 4));
        Assert.Equal(1, id);
        
        // Type (4 bytes)
        var type = BitConverter.ToInt32(data.Slice(8, 4));
        Assert.Equal((int)RconPacketType.SERVERDATA_EXECCOMMAND, type);
        
        // Body + 2 null terminators
        var bodyEnd = 12 + "listplayers".Length;
        Assert.Equal(0, data[bodyEnd]);
        Assert.Equal(0, data[bodyEnd + 1]);
    }

    [Fact]
    public void TryParse_ValidPacket_ReturnsPacket()
    {
        var originalPacket = new RconPacket(5, RconPacketType.SERVERDATA_RESPONSE_VALUE, "response text");
        var writer = new ArrayBufferWriter<byte>();
        originalPacket.Serialize(writer);
        
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        
        bool parsed = RconPacket.TryParse(sequence, out var packet, out var consumed);
        
        Assert.True(parsed);
        Assert.NotNull(packet);
        Assert.Equal(5, packet!.Id);
        Assert.Equal(RconPacketType.SERVERDATA_RESPONSE_VALUE, packet.Type);
        Assert.Equal("response text", packet.Body);
    }

    [Fact]
    public void TryParse_IncompletePacket_ReturnsFalse()
    {
        var incompleteData = new byte[] { 20, 0, 0, 0, 1, 0, 0, 0 };
        var sequence = new ReadOnlySequence<byte>(incompleteData);
        
        bool parsed = RconPacket.TryParse(sequence, out var packet, out var consumed);
        
        Assert.False(parsed);
        Assert.Null(packet);
    }
}
