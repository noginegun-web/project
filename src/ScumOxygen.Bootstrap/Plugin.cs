using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

namespace ScumOxygen.Bootstrap;

public static class Plugin
{
    private static int s_initialized;
    private static readonly object Sync = new();
    private static readonly Lazy<string> BaseDir = new(ResolveBaseDir);

    [UnmanagedCallersOnly(EntryPoint = "InitializeNative", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int InitializeNative(IntPtr args, int size)
    {
        if (Interlocked.Exchange(ref s_initialized, 1) != 0)
        {
            Log("InitializeNative skipped: already initialized");
            return 1;
        }

        try
        {
            var launchArgs = "scum-server";
            if (args != IntPtr.Zero && size > 0)
            {
                launchArgs = Marshal.PtrToStringUTF8(args, size) ?? launchArgs;
            }

            Log($"Bootstrap start. BaseDir={BaseDir.Value}");
            Log($"Args={launchArgs}");

            AssemblyLoadContext.Default.Resolving += ResolveManagedAssembly;

            var runtimePath = Path.Combine(BaseDir.Value, "ScumOxygen.Runtime.dll");
            if (!File.Exists(runtimePath))
            {
                Log($"Runtime assembly missing: {runtimePath}");
                return -1;
            }

            var runtimeAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(runtimePath);
            var pluginType = runtimeAssembly.GetType("ScumOxygen.Core.Plugin", throwOnError: true);
            var initializeMethod = pluginType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
            if (initializeMethod == null)
            {
                Log("ScumOxygen.Core.Plugin.Initialize not found");
                return -1;
            }

            var result = initializeMethod.Invoke(null, new object?[] { launchArgs });
            var rc = result is int i ? i : 0;
            Log($"Runtime Initialize returned rc={rc}");
            return rc;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            LogException("TargetInvocationException", ex.InnerException);
            return -1;
        }
        catch (Exception ex)
        {
            LogException("Bootstrap exception", ex);
            return -1;
        }
    }

    private static Assembly? ResolveManagedAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        try
        {
            var candidate = string.Equals(name.Name, "ScumOxygen.Core", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(BaseDir.Value, "ScumOxygen.Runtime.dll")
                : Path.Combine(BaseDir.Value, $"{name.Name}.dll");

            if (File.Exists(candidate))
            {
                Log($"Resolving managed assembly: {name} -> {candidate}");
                return context.LoadFromAssemblyPath(candidate);
            }
        }
        catch (Exception ex)
        {
            LogException($"ResolveManagedAssembly failed for {name}", ex);
        }

        return null;
    }

    private static string ResolveBaseDir()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDir))
        {
            return assemblyDir;
        }

        var appContextDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(appContextDir))
        {
            return appContextDir;
        }

        return AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
    }

    private static void LogException(string title, Exception ex)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        AppendException(builder, ex, 0);
        Log(builder.ToString().TrimEnd());
    }

    private static void AppendException(StringBuilder builder, Exception ex, int depth)
    {
        builder.AppendLine($"{new string(' ', depth * 2)}{ex.GetType().FullName}: {ex.Message}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            builder.AppendLine(ex.StackTrace);
        }

        if (ex is ReflectionTypeLoadException typeLoad && typeLoad.LoaderExceptions.Length > 0)
        {
            foreach (var loaderEx in typeLoad.LoaderExceptions.Where(e => e != null))
            {
                AppendException(builder, loaderEx!, depth + 1);
            }
        }

        if (ex.InnerException != null)
        {
            AppendException(builder, ex.InnerException, depth + 1);
        }
    }

    private static void Log(string message)
    {
        lock (Sync)
        {
            var logDir = Path.Combine(BaseDir.Value, "oxygen", "logs");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, "bootstrap.log");
            File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
        }
    }
}
