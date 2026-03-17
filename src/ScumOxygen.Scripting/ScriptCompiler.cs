using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;

namespace ScumOxygen.Scripting;

/// <summary>
/// Компилятор C# скриптов с использованием Roslyn
/// </summary>
public sealed class ScriptCompiler
{
    private readonly List<MetadataReference> _references;
    private readonly CSharpCompilationOptions _options;

    public ScriptCompiler()
    {
        _references = new List<MetadataReference>
        {
            // Базовые сборки
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Numerics.Vector3).Assembly.Location),
        };

        _options = new CSharpCompilationOptions(
            outputKind: OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false,
            checkOverflow: false,
            platform: Platform.AnyCpu);
    }

    /// <summary>
    /// Добавляет ссылку на сборку
    /// </summary>
    public void AddReference(Assembly assembly)
    {
        _references.Add(MetadataReference.CreateFromFile(assembly.Location));
    }

    /// <summary>
    /// Компилирует код скрипта
    /// </summary>
    public CompilationResult Compile(string scriptName, string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        
        // Добавляем базовые using'и
        var compilation = CSharpCompilation.Create(
            assemblyName: $"Script_{scriptName}_{Guid.NewGuid():N}",
            syntaxTrees: new[] { syntaxTree },
            references: _references,
            options: _options);

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Id}: {d.GetMessage()}")
                .ToList();

            return CompilationResult.FromFailure(errors);
        }

        ms.Seek(0, SeekOrigin.Begin);
        return CompilationResult.FromSuccess(ms.ToArray());
    }
}

/// <summary>
/// Результат компиляции
/// </summary>
public readonly record struct CompilationResult
{
    public bool Success { get; init; }
    public byte[]? AssemblyBytes { get; init; }
    public IReadOnlyList<string> Errors { get; init; }

    public static CompilationResult FromSuccess(byte[] assemblyBytes) => 
        new() { Success = true, AssemblyBytes = assemblyBytes, Errors = Array.Empty<string>() };

    public static CompilationResult FromFailure(IEnumerable<string> errors) => 
        new() { Success = false, AssemblyBytes = null, Errors = errors.ToList() };
}
