using Xunit;
using ScumOxygen.Core.Models;
using ScumOxygen.Rcon.Parsers;

namespace ScumOxygen.Tests;

public class StatusParserTests
{
    private readonly StatusParser _parser = new();

    [Fact]
    public void Parse_ValidStatus_ReturnsStatus()
    {
        var input = @"hostname: My SCUM Server
map: Croatia
players: 25/64
uptime: 03:45:12
version: 1.0.12345
fps: 60
memory: 2048
TickRate: 30";

        var result = _parser.Parse(input);

        Assert.NotNull(result);
        Assert.Equal("My SCUM Server", result!.ServerName);
        Assert.Equal("Croatia", result.Map);
        Assert.Equal(25, result.CurrentPlayers);
        Assert.Equal(64, result.MaxPlayers);
        Assert.Equal(60, result.Fps);
        Assert.Equal(2048f, result.MemoryUsage);
        Assert.Equal(30, result.TickRate);
    }

    [Theory]
    [InlineData("01:30:45", 1, 30, 45)]
    [InlineData("30:45", 0, 30, 45)]
    [InlineData("2h 30m 15s", 2, 30, 15)]
    [InlineData("5h30m", 5, 30, 0)]
    [InlineData("10h", 10, 0, 0)]
    public void Parse_VariousUptimeFormats_ParsesCorrectly(string uptimeInput, int expectedHours, int expectedMinutes, int expectedSeconds)
    {
        var input = $"hostname: Test\nuptime: {uptimeInput}";
        
        var result = _parser.Parse(input);

        Assert.NotNull(result);
        Assert.Equal(expectedHours, result!.Uptime.Hours);
        Assert.Equal(expectedMinutes, result.Uptime.Minutes);
        Assert.Equal(expectedSeconds, result.Uptime.Seconds);
    }

    [Fact]
    public void Parse_EmptyResponse_ReturnsNull()
    {
        var result = _parser.Parse("");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_PartialResponse_UsesDefaults()
    {
        var input = "hostname: Test Server";
        
        var result = _parser.Parse(input);

        Assert.NotNull(result);
        Assert.Equal("Test Server", result!.ServerName);
        Assert.Equal("Unknown", result.Map);
        Assert.Equal(0, result.CurrentPlayers);
        Assert.Equal(0, result.MaxPlayers);
    }
}
