using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.Loader;

namespace ScumOxygen.Scripting;

/// <summary>
/// Движок выполнения скриптов с изоляцией
/// </summary>
public sealed class ScriptEngine : IDisposable
{
    private readonly ScriptCompiler _compiler;
    private readonly ILogger<ScriptEngine>? _logger;
    private readonly Dictionary<string, LoadedScript> _loadedScripts = new();
    private readonly object _lock = new();

    public ScriptEngine(ILogger<ScriptEngine>? logger = null)
    {
        _compiler = new ScriptCompiler();
        _logger = logger;
        
        // Добавляем ссылки на API сборки
        _compiler.AddReference(typeof(ScriptEngine).Assembly);
    }

    /// <summary>
    /// Загружает и компилирует скрипт
    /// </summary>
    public async Task<ScriptLoadResult> LoadScriptAsync(string name, string sourceCode, CancellationToken ct = default)
    {
        return await Task.Run(() => LoadScript(name, sourceCode), ct);
    }

    private ScriptLoadResult LoadScript(string name, string sourceCode)
    {
        lock (_lock)
        {
            // Выгружаем старую версию если есть
            if (_loadedScripts.TryGetValue(name, out var oldScript))
            {
                _logger?.LogInformation("Unloading previous version of script '{ScriptName}'", name);
                oldScript.Context.Unload();
                _loadedScripts.Remove(name);
            }

            // Компилируем
            var compileResult = _compiler.Compile(name, sourceCode);
            if (!compileResult.Success)
            {
                _logger?.LogError("Compilation failed for script '{ScriptName}': {Errors}", 
                    name, string.Join(", ", compileResult.Errors));
                return ScriptLoadResult.FromFailure(compileResult.Errors);
            }

            try
            {
                // Создаем изолированный контекст
                var context = new ScriptLoadContext(AppContext.BaseDirectory);
                
                using var ms = new MemoryStream(compileResult.AssemblyBytes!);
                var assembly = context.LoadFromStream(ms);

                // Ищем тип с методом Main или класс реализующий IScript
                var entryType = FindEntryType(assembly);
                if (entryType == null)
                {
                    context.Unload();
                    return ScriptLoadResult.FromFailure(new[] { "No entry point found. Define Main() method or implement IScript interface." });
                }

                var script = new LoadedScript
                {
                    Name = name,
                    Context = context,
                    Assembly = assembly,
                    EntryType = entryType,
                    SourceCode = sourceCode
                };

                _loadedScripts[name] = script;
                _logger?.LogInformation("Script '{ScriptName}' loaded successfully", name);

                return ScriptLoadResult.FromSuccess(script);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load script '{ScriptName}'", name);
                return ScriptLoadResult.FromFailure(new[] { ex.Message });
            }
        }
    }

    /// <summary>
    /// Выгружает скрипт
    /// </summary>
    public bool UnloadScript(string name)
    {
        lock (_lock)
        {
            if (_loadedScripts.TryGetValue(name, out var script))
            {
                script.Context.Unload();
                _loadedScripts.Remove(name);
                _logger?.LogInformation("Script '{ScriptName}' unloaded", name);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Выполняет метод в скрипте
    /// </summary>
    public async Task<object?> ExecuteAsync(string scriptName, string methodName, object?[]? args = null, CancellationToken ct = default)
    {
        LoadedScript? script;
        lock (_lock)
        {
            if (!_loadedScripts.TryGetValue(scriptName, out script))
                throw new InvalidOperationException($"Script '{scriptName}' not found");
        }

        return await Task.Run(() =>
        {
            // Создаем экземпляр
            var instance = Activator.CreateInstance(script.EntryType);
            if (instance == null)
                throw new InvalidOperationException("Failed to create script instance");

            // Ищем метод
            var method = script.EntryType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                throw new InvalidOperationException($"Method '{methodName}' not found");

            // Вызываем
            var result = method.Invoke(instance, args);
            
            if (result is Task task)
            {
                task.Wait(ct);
                var resultProperty = task.GetType().GetProperty("Result");
                return resultProperty?.GetValue(task);
            }

            return result;
        }, ct);
    }

    /// <summary>
    /// Получает список загруженных скриптов
    /// </summary>
    public IReadOnlyList<string> GetLoadedScripts()
    {
        lock (_lock)
        {
            return _loadedScripts.Keys.ToList();
        }
    }

    private static Type? FindEntryType(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            // Ищем метод Main
            if (type.GetMethod("Main", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance) != null)
                return type;

            // Или интерфейс IScript
            if (type.GetInterface("IScript") != null)
                return type;
        }

        // Если есть только один тип с методом Execute
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance) != null)
                return type;
        }

        return assembly.GetTypes().FirstOrDefault();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var script in _loadedScripts.Values)
            {
                script.Context.Unload();
            }
            _loadedScripts.Clear();
        }
    }
}

/// <summary>
/// Загруженный скрипт
/// </summary>
public sealed class LoadedScript
{
    public required string Name { get; init; }
    public required ScriptLoadContext Context { get; init; }
    public required Assembly Assembly { get; init; }
    public required Type EntryType { get; init; }
    public required string SourceCode { get; init; }
}

/// <summary>
/// Результат загрузки скрипта
/// </summary>
public readonly record struct ScriptLoadResult
{
    public bool Success { get; init; }
    public LoadedScript? Script { get; init; }
    public IReadOnlyList<string> Errors { get; init; }

    public static ScriptLoadResult FromSuccess(LoadedScript script) => 
        new() { Success = true, Script = script, Errors = Array.Empty<string>() };

    public static ScriptLoadResult FromFailure(IEnumerable<string> errors) => 
        new() { Success = false, Script = null, Errors = errors.ToList() };
}
