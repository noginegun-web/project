using System.Buffers;
using System.IO.Pipelines;

namespace ScumOxygen.Rcon.Protocol;

/// <summary>
/// Чтение RCON-пакетов из PipeReader
/// </summary>
public sealed class RconPacketReader
{
    private readonly PipeReader _reader;
    private int _nextPacketId = 1;

    public RconPacketReader(PipeReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>
    /// Читает следующий пакет из потока
    /// </summary>
    public async Task<RconPacket?> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;

            if (RconPacket.TryParse(buffer, out var packet, out var consumed))
            {
                _reader.AdvanceTo(consumed);
                return packet;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                if (buffer.Length > 0)
                {
                    throw new InvalidDataException("Incomplete packet at end of stream");
                }
                return null;
            }
        }

        throw new OperationCanceledException();
    }

    /// <summary>
    /// Генерирует следующий ID пакета
    /// </summary>
    public int GetNextPacketId() => Interlocked.Increment(ref _nextPacketId);
}
