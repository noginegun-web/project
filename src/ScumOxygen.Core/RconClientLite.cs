using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScumOxygen.Core.Models;

namespace ScumOxygen.Core;

internal sealed class RconClientLite
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;
    private readonly Logger _log;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _packetId = 1;

    public RconClientLite(string host, int port, string password, Logger log)
    {
        _host = host;
        _port = port;
        _password = password;
        _log = log;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client?.Connected == true && _stream != null) return;

        _client?.Close();
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, ct);
        _stream = _client.GetStream();
        await AuthenticateAsync(ct);
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        var id = NextId();
        await SendPacketAsync(id, 3, _password, ct);
        var resp = await ReadPacketAsync(ct);
        if (resp.Id == -1)
        {
            throw new InvalidOperationException("RCON authentication failed");
        }
    }

    public async Task<CommandResult> ExecuteAsync(string command, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var sw = Stopwatch.StartNew();

        var id = NextId();
        await SendPacketAsync(id, 2, command, ct);

        // Read until we get a response with matching id
        var sb = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            var resp = await ReadPacketAsync(ct);
            if (resp.Id == -1) break;
            if (resp.Id == id)
            {
                if (!string.IsNullOrEmpty(resp.Body))
                    sb.Append(resp.Body);
                // Some servers send multiple packets for long responses
                if (resp.Type == 0 && resp.Body.Length == 0)
                    break;
                if (resp.Body.Length < 4096)
                    break;
            }
        }

        sw.Stop();
        return CommandResult.Ok(sb.ToString(), sw.Elapsed);
    }

    private async Task SendPacketAsync(int id, int type, string body, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("RCON not connected");

        var payload = Encoding.ASCII.GetBytes(body);
        var length = 4 + 4 + payload.Length + 2;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write(length);
        bw.Write(id);
        bw.Write(type);
        bw.Write(payload);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Flush();

        var buf = ms.ToArray();
        await _stream.WriteAsync(buf, 0, buf.Length, ct);
    }

    private async Task<RconPacket> ReadPacketAsync(CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("RCON not connected");

        var lenBytes = await ReadExactAsync(_stream, 4, ct);
        var length = BitConverter.ToInt32(lenBytes, 0);
        var data = await ReadExactAsync(_stream, length, ct);

        var id = BitConverter.ToInt32(data, 0);
        var type = BitConverter.ToInt32(data, 4);
        var body = Encoding.ASCII.GetString(data, 8, length - 10);
        return new RconPacket(id, type, body);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken ct)
    {
        var buf = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buf, offset, length - offset, ct);
            if (read <= 0) throw new IOException("RCON connection closed");
            offset += read;
        }
        return buf;
    }

    private int NextId() => Interlocked.Increment(ref _packetId);

    private readonly struct RconPacket
    {
        public readonly int Id;
        public readonly int Type;
        public readonly string Body;
        public RconPacket(int id, int type, string body)
        {
            Id = id;
            Type = type;
            Body = body;
        }
    }
}
