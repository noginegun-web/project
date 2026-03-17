using System.Buffers;
using System.IO.Pipelines;

namespace ScumOxygen.Rcon.Protocol;

/// <summary>
/// Запись RCON-пакетов в PipeWriter
/// </summary>
public sealed class RconPacketWriter : IDisposable
{
    private readonly PipeWriter _writer;
    private readonly object _lock = new();

    public RconPacketWriter(PipeWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <summary>
    /// Отправляет пакет
    /// </summary>
    public async Task WritePacketAsync(RconPacket packet, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            packet.Serialize(_writer);
        }
        
        var result = await _writer.FlushAsync(cancellationToken);
        
        if (result.IsCanceled)
            throw new OperationCanceledException();
        
        if (result.IsCompleted)
            throw new InvalidOperationException("Writer completed unexpectedly");
    }

    /// <summary>
    /// Отправляет команду и возвращает ID пакета
    /// </summary>
    public async Task<int> SendCommandAsync(int packetId, string command, CancellationToken cancellationToken = default)
    {
        // Отправляем мульти-пакетную последовательность для надежности
        var commandPacket = new RconPacket(packetId, RconPacketType.SERVERDATA_EXECCOMMAND, command);
        var endPacket = new RconPacket(packetId, RconPacketType.SERVERDATA_RESPONSE_VALUE, string.Empty);
        
        lock (_lock)
        {
            commandPacket.Serialize(_writer);
            endPacket.Serialize(_writer);
        }
        
        var result = await _writer.FlushAsync(cancellationToken);
        
        if (result.IsCanceled)
            throw new OperationCanceledException();
        
        if (result.IsCompleted)
            throw new InvalidOperationException("Writer completed unexpectedly");
            
        return packetId;
    }

    /// <summary>
    /// Отправляет пакет аутентификации
    /// </summary>
    public async Task<int> SendAuthAsync(int packetId, string password, CancellationToken cancellationToken = default)
    {
        var authPacket = new RconPacket(packetId, RconPacketType.SERVERDATA_AUTH, password);
        
        lock (_lock)
        {
            authPacket.Serialize(_writer);
        }
        
        var result = await _writer.FlushAsync(cancellationToken);
        
        if (result.IsCanceled)
            throw new OperationCanceledException();
        
        if (result.IsCompleted)
            throw new InvalidOperationException("Writer completed unexpectedly");
            
        return packetId;
    }

    public void Dispose()
    {
        _writer.Complete();
    }
}
