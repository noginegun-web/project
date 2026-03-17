using System.Buffers;
using System.Buffers.Binary;

namespace ScumOxygen.Rcon.Protocol;

/// <summary>
/// Типы пакетов Source RCON протокола
/// </summary>
public enum RconPacketType : int
{
    SERVERDATA_AUTH = 3,
    SERVERDATA_AUTH_RESPONSE = 2,
    SERVERDATA_EXECCOMMAND = 2,
    SERVERDATA_RESPONSE_VALUE = 0,
    SERVERDATA_EXECCOMMAND_2 = 4,
}

/// <summary>
/// Пакет RCON-протокола
/// Структура: Length(4) + ID(4) + Type(4) + Body + Null(1) + Null(1)
/// </summary>
public sealed class RconPacket
{
    public const int HeaderSize = 12; // Length + ID + Type
    public const int NullTerminatorSize = 2;
    
    public int Id { get; init; }
    public RconPacketType Type { get; init; }
    public string Body { get; init; } = string.Empty;
    
    public int TotalSize => HeaderSize + Body.Length + NullTerminatorSize;
    public int PayloadSize => sizeof(int) + sizeof(int) + Body.Length + NullTerminatorSize;

    public RconPacket(int id, RconPacketType type, string body)
    {
        Id = id;
        Type = type;
        Body = body ?? string.Empty;
    }

    /// <summary>
    /// Сериализует пакет в буфер
    /// </summary>
    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(HeaderSize);
        
        // Length (размер без учета самого поля Length)
        BinaryPrimitives.WriteInt32LittleEndian(span, PayloadSize);
        
        // ID
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), Id);
        
        // Type
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), (int)Type);
        
        writer.Advance(HeaderSize);
        
        // Body
        if (!string.IsNullOrEmpty(Body))
        {
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(Body);
            writer.Write(bodyBytes);
        }
        
        // Два null-терминатора
        span = writer.GetSpan(2);
        span[0] = 0;
        span[1] = 0;
        writer.Advance(2);
    }

    /// <summary>
/// Парсит пакет из последовательности байтов
/// </summary>
public static bool TryParse(ReadOnlySequence<byte> sequence, out RconPacket? packet, out SequencePosition consumed)
{
    packet = null;
    consumed = sequence.Start;

    if (sequence.Length < HeaderSize + NullTerminatorSize)
        return false;

    var reader = new SequenceReader<byte>(sequence);
    
    // Читаем Length (4 байта)
    if (!reader.TryReadLittleEndian(out int length))
        return false;

    if (length < 0 || length > 4096)
        throw new InvalidDataException($"Invalid packet length: {length}");

    var totalPacketSize = sizeof(int) + length; // +4 для поля length

    if (sequence.Length < totalPacketSize)
        return false;

    // Читаем ID (4 байта)
    if (!reader.TryReadLittleEndian(out int id))
        return false;

    // Читаем Type (4 байта)
    if (!reader.TryReadLittleEndian(out int type))
        return false;

    // Читаем тело (length - 8 для ID и Type - 2 для null-терминаторов)
    var bodyLength = length - 8 - 2;
    if (bodyLength < 0) bodyLength = 0;

    string body;
    if (bodyLength > 0)
    {
        if (!reader.TryReadExact(bodyLength, out var bodySequence))
            return false;
        
        // Конвертируем ReadOnlySequence<byte> в строку
        if (bodySequence.IsSingleSegment)
        {
            body = System.Text.Encoding.UTF8.GetString(bodySequence.First.Span);
        }
        else
        {
            // Для multi-segment собираем в массив
            var bodyBytes = bodySequence.ToArray();
            body = System.Text.Encoding.UTF8.GetString(bodyBytes);
        }
    }
    else
    {
        body = string.Empty;
    }

    // Пропускаем 2 null-терминатора
    reader.Advance(2);

    consumed = reader.Position;
    packet = new RconPacket(id, (RconPacketType)type, body);
    return true;
}
}
