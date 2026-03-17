using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ScumOxygen.Core;

public sealed class PluginCompiler
{
    private readonly Logger _log;
    private readonly string _errorLogPath;

    public PluginCompiler(Logger log, string errorLogPath)
    {
        _log = log;
        _errorLogPath = errorLogPath;
    }

    public string? Compile(string sourceFile, string outputDir)
    {
        try
        {
            var code = ReadAllTextSafe(sourceFile);
            var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp12));

            var references = GetReferences();

            var assemblyName = Path.GetFileNameWithoutExtension(sourceFile);
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var outputPath = Path.Combine(outputDir, $"{assemblyName}_{stamp}.dll");

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { tree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var result = compilation.Emit(fs);

            if (!result.Success)
            {
                foreach (var diag in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    _log.Error($"{sourceFile}: {diag}");
                    File.AppendAllText(_errorLogPath, $"[{DateTime.UtcNow:O}] {sourceFile}: {diag}{Environment.NewLine}");
                }
                return null;
            }

            _log.Info($"Compiled: {sourceFile} -> {outputPath}");
            return outputPath;
        }
        catch (Exception ex)
        {
            _log.Error($"[PluginCompiler] Failed to compile {sourceFile}: {ex.GetBaseException().Message}");
            File.AppendAllText(_errorLogPath, $"[{DateTime.UtcNow:O}] {sourceFile}: {ex}{Environment.NewLine}");
            return null;
        }
    }

    private static string ReadAllTextSafe(string path)
    {
        const int maxRetries = 8;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
        // Last attempt throws to surface the real error
        return File.ReadAllText(path);
    }

    private static List<MetadataReference> GetReferences()
    {
        var refs = new List<MetadataReference>();
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(tpa)) return refs;

        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // Ensure core assembly reference
        refs.Add(MetadataReference.CreateFromFile(Assembly.GetExecutingAssembly().Location));
        return refs;
    }
}
