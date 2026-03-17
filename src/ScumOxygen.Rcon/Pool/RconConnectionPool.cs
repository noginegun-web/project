using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ScumOxygen.Core.Interfaces;
using ScumOxygen.Core.Models;

namespace ScumOxygen.Rcon.Pool;

/// <summary>
/// Арендованное соединение из пула (возвращается при dispose)
/// </summary>
public sealed class PooledRconClient : IRconClient
{
    private readonly IRconClient _innerClient;
    private readonly RconConnectionPool _pool;
    private int _disposed;

    public bool IsConnected => _innerClient.IsConnected;

    internal PooledRconClient(IRconClient innerClient, RconConnectionPool pool)
    {
        _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        => _innerClient.ConnectAsync(cancellationToken);

    public Task<Core.Models.CommandResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        => _innerClient.ExecuteAsync(command, cancellationToken);

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        => _innerClient.DisconnectAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _pool.ReturnAsync(_innerClient);
        }
    }
}

/// <summary>
/// Пул соединений RCON с минимальным и максимальным размером
/// </summary>
public sealed class RconConnectionPool : IRconConnectionPool
{
    private readonly RconConnectionOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<RconConnectionPool>? _logger;
    private readonly ConcurrentBag<IRconClient> _available = new();
    private readonly ConcurrentDictionary<IRconClient, bool> _allConnections = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly object _initLock = new();
    private int _initialized;
    private CancellationTokenSource? _maintenanceCts;
    private Task? _maintenanceTask;

    public int ActiveConnections => _allConnections.Count;
    public int AvailableConnections => _available.Count;

    public RconConnectionPool(RconConnectionOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<RconConnectionPool>();
        _semaphore = new SemaphoreSlim(options.MaxPoolSize, options.MaxPoolSize);
        
        // Запускаем фоновое поддержание минимального пула
        StartMaintenance();
    }

    /// <summary>
    /// Инициализирует минимальное количество соединений
    /// </summary>
    private async Task EnsureMinimumConnectionsAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        _logger?.LogInformation("Initializing connection pool with {Min} minimum connections", _options.MinPoolSize);

        var tasks = new List<Task>();
        for (int i = 0; i < _options.MinPoolSize; i++)
        {
            tasks.Add(CreateAndAddConnectionAsync(ct));
        }

        try
        {
            await Task.WhenAll(tasks);
            _logger?.LogInformation("Connection pool initialized with {Count} connections", _available.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize minimum connections");
            throw;
        }
    }

    private async Task<IRconClient> CreateAndAddConnectionAsync(CancellationToken ct)
    {
        var client = new RconClient(_options, _loggerFactory?.CreateLogger<RconClient>());
        
        try
        {
            await client.ConnectAsync(ct);
            _allConnections.TryAdd(client, true);
            _available.Add(client);
            _logger?.LogDebug("Created new connection. Pool: {Available}/{Total}", 
                _available.Count, _allConnections.Count);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<IRconClient> RentAsync(CancellationToken cancellationToken = default)
    {
        // Убедимся, что минимальное количество соединений создано
        await EnsureMinimumConnectionsAsync(cancellationToken);

        // Ждем, пока освободится слот в пуле
        if (!await _semaphore.WaitAsync(_options.CommandTimeout, cancellationToken))
        {
            throw new TimeoutException("Connection pool exhausted");
        }

        try
        {
            // Пробуем взять доступное соединение
            if (_available.TryTake(out var existingClient))
            {
                // Проверяем, что соединение живое
                if (existingClient.IsConnected)
                {
                    _logger?.LogDebug("Reusing existing connection. Pool: {Available}/{Total}", 
                        _available.Count, _allConnections.Count);
                    return new PooledRconClient(existingClient, this);
                }

                // Соединение мертво - удаляем и создаем новое
                _allConnections.TryRemove(existingClient, out _);
                await existingClient.DisposeAsync();
            }

            // Создаем новое соединение
            if (_allConnections.Count < _options.MaxPoolSize)
            {
                var newClient = await CreateAndAddConnectionAsync(cancellationToken);
                
                // Удаляем из available, так как оно будет арендовано
                _available.TryTake(out _);
                return new PooledRconClient(newClient, this);
            }

            throw new InvalidOperationException("Connection pool limit reached");
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    public ValueTask ReturnAsync(IRconClient client)
    {
        if (client is PooledRconClient pooled)
        {
            client = pooled; // Внутренний клиент будет возвращен через _innerClient в PooledRconClient
        }

        // Возвращаем семафор
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Игнорируем - может произойти при ошибках
        }

        // Добавляем обратно в пул, если соединение живое
        if (client.IsConnected)
        {
            _available.Add(client);
            _logger?.LogDebug("Returned connection to pool. Pool: {Available}/{Total}", 
                _available.Count, _allConnections.Count);
        }
        else
        {
            // Соединение мертво - удаляем
            _allConnections.TryRemove(client, out _);
            _ = client.DisposeAsync();
            _logger?.LogDebug("Removed dead connection from pool. Pool: {Available}/{Total}", 
                _available.Count, _allConnections.Count);
        }

        return ValueTask.CompletedTask;
    }

    private void StartMaintenance()
    {
        _maintenanceCts = new CancellationTokenSource();
        _maintenanceTask = Task.Run(async () =>
        {
            while (!_maintenanceCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _maintenanceCts.Token);
                    await PerformMaintenanceAsync(_maintenanceCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Maintenance task failed");
                }
            }
        }, _maintenanceCts.Token);
    }

    private async Task PerformMaintenanceAsync(CancellationToken ct)
    {
        // Проверяем и восстанавливаем минимальное количество соединений
        var available = _available.Count;
        var total = _allConnections.Count;

        if (total < _options.MinPoolSize)
        {
            var toCreate = _options.MinPoolSize - total;
            _logger?.LogInformation("Maintenance: creating {Count} connections to reach minimum", toCreate);

            for (int i = 0; i < toCreate; i++)
            {
                try
                {
                    if (await _semaphore.WaitAsync(TimeSpan.FromSeconds(5), ct))
                    {
                        try
                        {
                            await CreateAndAddConnectionAsync(ct);
                        }
                        finally
                        {
                            _semaphore.Release();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to create connection during maintenance");
                }
            }
        }

        // Проверяем здоровье соединений
        var toCheck = new List<IRconClient>();
        while (_available.TryTake(out var client))
        {
            toCheck.Add(client);
        }

        foreach (var client in toCheck)
        {
            if (client.IsConnected)
            {
                _available.Add(client);
            }
            else
            {
                _allConnections.TryRemove(client, out _);
                await client.DisposeAsync();
                _logger?.LogDebug("Removed dead connection during maintenance");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _maintenanceCts?.Cancel();
        
        if (_maintenanceTask != null)
        {
            try { await _maintenanceTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* ignore */ }
        }

        // Закрываем все соединения
        var all = _allConnections.Keys.ToList();
        _logger?.LogInformation("Disposing {Count} connections from pool", all.Count);

        foreach (var client in all)
        {
            try { await client.DisposeAsync(); }
            catch { /* ignore */ }
        }

        _available.Clear();
        _allConnections.Clear();
        _semaphore.Dispose();
        _maintenanceCts?.Dispose();
    }
}
