using System;
using System.IO;

namespace ScumOxygen.Core;

public static class OxygenPaths
{
    private static readonly Lazy<string> _baseDir = new(ResolveBaseDir);

    public static string BaseDir
    {
        get { return _baseDir.Value; }
    }
    public static string OxygenDir => Path.Combine(BaseDir, "oxygen");
    public static string PluginsDir => Path.Combine(OxygenDir, "plugins");
    public static string ConfigsDir => Path.Combine(OxygenDir, "configs");
    public static string DataDir => Path.Combine(OxygenDir, "data");
    public static string LogsDir => Path.Combine(OxygenDir, "logs");
    public static string CacheDir => Path.Combine(OxygenDir, "cache");
    public static string WebDir => Path.Combine(OxygenDir, "web");

    public static void Ensure()
    {
        Directory.CreateDirectory(OxygenDir);
        Directory.CreateDirectory(PluginsDir);
        Directory.CreateDirectory(ConfigsDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(WebDir);
    }

    private static string ResolveBaseDir()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(OxygenPaths).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDir))
        {
            return assemblyDir;
        }

        var bd = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(bd))
        {
            bd = AppDomain.CurrentDomain.BaseDirectory;
        }

        return bd ?? string.Empty;
    }
}
