using Xunit;
using ScumOxygen.Core.Models;
using ScumOxygen.Rcon.Parsers;

namespace ScumOxygen.Tests;

public class ListPlayersParserTests
{
    private readonly ListPlayersParser _parser = new();

    [Theory]
    [InlineData("0, TestPlayer, 76561198000000000, 192.168.1.1, 01:23:45, 50, Sector A1, 100.0, 85.0")]
    [InlineData("0,TestPlayer,76561198000000000,192.168.1.1,01:23:45,50,Sector A1,100.0,85.0")]
    public void Parse_ValidCsvFormat_ReturnsPlayer(string input)
    {
        var result = _parser.Parse(input);

        Assert.NotNull(result);
        Assert.Single(result);
        
        var player = result![0];
        Assert.Equal(0, player.Id);
        Assert.Equal("TestPlayer", player.Name);
        Assert.Equal("76561198000000000", player.SteamId);
        Assert.Equal("192.168.1.1", player.IpAddress);
        Assert.Equal(50, player.Ping);
        Assert.Equal("Sector A1", player.Location);
        Assert.Equal(100.0f, player.Health);
        Assert.Equal(85.0f, player.Stamina);
    }

    [Fact]
    public void Parse_MultipleLines_ReturnsMultiplePlayers()
    {
        var input = @"0, PlayerOne, 76561198000000001, 10.0.0.1, 00:15:30, 45, Sector B2, 95.0, 80.0
1, PlayerTwo, 76561198000000002, 10.0.0.2, 01:00:00, 60, Sector C3, 88.5, 90.0";

        var result = _parser.Parse(input);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal("PlayerOne", result[0].Name);
        Assert.Equal("PlayerTwo", result[1].Name);
    }

    [Fact]
    public void Parse_EmptyResponse_ReturnsEmptyList()
    {
        var result = _parser.Parse("");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void Parse_ColonFormat_ReturnsPlayer()
    {
        var input = "ID: 0, Name: TestPlayer, SteamID: 76561198000000000, IP: 192.168.1.1, Time: 00:30:00";
        
        var result = _parser.Parse(input);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("TestPlayer", result![0].Name);
    }
}
