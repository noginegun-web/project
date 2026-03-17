using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ScumOxygen.Core.Interfaces;
using ScumOxygen.Core.Models;
using ScumOxygen.Rcon.Protocol;

namespace ScumOxygen.Rcon;

/// <summary>
/// RCON-клиент с автопереподключением
/// </summary>
public sealed class RconClient : IRconClient
{
    private readonly RconConnectionOptions _options;
    private readonly ILogger<RconClient>? _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _packetIdLock = new();
    
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private PipeReader? _pipeReader;
    private PipeWriter? _pipeWriter;
    private RconPacketReader? _packetReader;
    private RconPacketWriter? _packetWriter;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;
    private int _currentPacketId = 0;
    private bool _isAuthenticated = false;

    public bool IsConnected => _tcpClient?.Connected == true && _isAuthenticated;

    public RconClient(RconConnectionOptions options, ILogger<RconClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
                return;

            await ConnectInternalAsync(cancellationToken);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = _options.InitialReconnectDelay;

        while (attempt < _options.MaxReconnectAttempts)
        {
            try
            {
                _logger?.LogInformation("Connecting to RCON server at {Host}:{Port} (attempt {Attempt})", 
                    _options.Host, _options.Port, attempt + 1);

                // Создаем TCP-соединение
                _tcpClient = new TcpClient();
                using var timeoutCts = new CancellationTokenSource(_options.ConnectTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                
                await _tcpClient.ConnectAsync(_options.Host, _options.Port, linkedCts.Token);
                
                _networkStream = _tcpClient.GetStream();
                
                // Создаем Pipes для эффективного I/O
                var pipe = new Pipe(new PipeOptions(
                    readerScheduler: PipeScheduler.ThreadPool,
                    writerScheduler: PipeScheduler.ThreadPool,
                    pauseWriterThreshold: 65536,
                    resumeWriterThreshold: 32768));
                
                _pipeReader = pipe.Reader;
                _pipeWriter = pipe.Writer;
                
                _packetReader = new RconPacketReader(pipe.Reader);
                _packetWriter = new RconPacketWriter(pipe.Writer);

                // Запускаем задачу для копирования данных из NetworkStream в Pipe
                _ = Task.Run(() => CopyStreamToPipeAsync(_networkStream, pipe.Writer, cancellationToken), cancellationToken);

                // Аутентификация
                await AuthenticateAsync(cancellationToken);
                
                // Запускаем Keep-Alive
                StartKeepAlive();

                _logger?.LogInformation("Successfully connected to RCON server");
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger?.LogWarning(ex, "Connection attempt {Attempt} failed", attempt);
                
                if (attempt >= _options.MaxReconnectAttempts)
                {
                    throw new InvalidOperationException(
                        $"Failed to connect after {_options.MaxReconnectAttempts} attempts", ex);
                }

                _logger?.LogInformation("Waiting {DelayMs}ms before retry...", delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
                
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * _options.ReconnectBackoffMultiplier, 
                             _options.MaxReconnectDelay.TotalMilliseconds));
            }
        }
    }

    private async Task CopyStreamToPipeAsync(NetworkStream stream, PipeWriter writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && stream.CanRead)
            {
                var memory = writer.GetMemory(4096);
                var read = await stream.ReadAsync(memory, ct);
                
                if (read == 0)
                {
                    _logger?.LogWarning("Network stream closed by server");
                    break;
                }
                
                writer.Advance(read);
                var result = await writer.FlushAsync(ct);
                
                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальное завершение
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error copying stream to pipe");
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var authId = GetNextPacketId();
        _logger?.LogDebug("Sending authentication with packet ID {PacketId}", authId);

        await _packetWriter!.SendAuthAsync(authId, _options.Password, cancellationToken);

        // Ждем ответа
        var response = await _packetReader!.ReadPacketAsync(cancellationToken);
        
        if (response == null)
            throw new InvalidOperationException("No response to authentication request");

        _logger?.LogDebug("Received auth response: ID={Id}, Type={Type}", response.Id, response.Type);

        // Source RCON: ID -1 означает неудачную аутентификацию
        if (response.Id == -1)
            throw new InvalidOperationException("Authentication failed: invalid password");

        // Иногда сервер отправляет дополнительный пакет
        if (response.Id != authId && response.Type == RconPacketType.SERVERDATA_RESPONSE_VALUE)
        {
            response = await _packetReader!.ReadPacketAsync(cancellationToken);
            if (response?.Id == -1)
                throw new InvalidOperationException("Authentication failed: invalid password");
        }

        _isAuthenticated = true;
        _logger?.LogDebug("Authentication successful");
    }

    public async Task<CommandResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);

        if (!IsConnected)
        {
            _logger?.LogWarning("Not connected, attempting to reconnect...");
            await ConnectAsync(cancellationToken);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var packetId = GetNextPacketId();

        try
        {
            _logger?.LogDebug("Executing command '{Command}' with packet ID {PacketId}", command, packetId);

            await _packetWriter!.SendCommandAsync(packetId, command, cancellationToken);

            // Собираем мульти-пакетный ответ
            var responseBuilder = new System.Text.StringBuilder();
            
            while (!cancellationToken.IsCancellationRequested)
            {
                var response = await _packetReader!.ReadPacketAsync(cancellationToken);
                
                if (response == null)
                    break;

                // Ответ на наш запрос или пустой пакет-завершитель
                if (response.Id == packetId)
                {
                    if (response.Type == RconPacketType.SERVERDATA_RESPONSE_VALUE)
                    {
                        if (string.IsNullOrEmpty(response.Body))
                        {
                            // Пустой пакет - конец ответа
                            break;
                        }
                        
                        if (responseBuilder.Length > 0)
                            responseBuilder.AppendLine();
                        responseBuilder.Append(response.Body);
                    }
                }
            }

            stopwatch.Stop();
            var result = responseBuilder.ToString();
            
            _logger?.LogDebug("Command executed in {ElapsedMs}ms, response length: {Length}", 
                stopwatch.ElapsedMilliseconds, result.Length);

            return CommandResult.Ok(result, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "Command execution failed: {Command}", command);
            
            // Сбрасываем состояние соединения
            _isAuthenticated = false;
            
            return CommandResult.Fail(ex.Message, stopwatch.Elapsed);
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            _keepAliveCts?.Cancel();
            
            if (_keepAliveTask != null)
            {
                try { await _keepAliveTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
                catch { /* ignore */ }
            }

            _packetWriter?.Dispose();
            _pipeReader?.Complete();
            _pipeWriter?.Complete();
            _networkStream?.Close();
            _tcpClient?.Close();

            _isAuthenticated = false;
            _logger?.LogInformation("Disconnected from RCON server");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void StartKeepAlive()
    {
        _keepAliveCts = new CancellationTokenSource();
        _keepAliveTask = Task.Run(async () =>
        {
            while (!_keepAliveCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.KeepAliveInterval, _keepAliveCts.Token);
                    
                    if (IsConnected)
                    {
                        // Отправляем пустую команду для поддержания соединения
                        await ExecuteAsync("", CancellationToken.None);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Keep-alive failed");
                }
            }
        }, _keepAliveCts.Token);
    }

    private int GetNextPacketId()
    {
        lock (_packetIdLock)
        {
            _currentPacketId = (_currentPacketId + 1) % int.MaxValue;
            if (_currentPacketId == 0) _currentPacketId = 1;
            return _currentPacketId;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _connectLock.Dispose();
        _keepAliveCts?.Dispose();
    }
}
