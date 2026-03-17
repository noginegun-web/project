using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace ScumOxygen.Core;

/// <summary>
/// Entry point for native CLR host
/// </summary>
public static class Plugin
{
    private static bool s_initialized = false;
    private static Logger? s_log;
    private static OxygenRuntime? s_runtime;

    [ComVisible(true)]
    public static int Initialize(string args)
    {
        if (s_initialized)
        {
            return 1;
        }

        try
        {
            OxygenPaths.Ensure();
            var logPath = Path.Combine(OxygenPaths.LogsDir, "Oxygen.log");
            s_log = new Logger(logPath);

            s_log.Info("ScumOxygen.Core initializing...");
            s_log.Info($"BaseDir: {OxygenPaths.BaseDir}");
            s_log.Info($"Args: {args}");

            Oxygen.Csharp.API.OxyConsole.PrintImpl = msg => s_log.Info("[Console] " + msg);
            Oxygen.Csharp.API.Server.BroadcastImpl = msg => s_log.Info("[Broadcast] " + msg);

            s_runtime = new OxygenRuntime(s_log);
            s_runtime.Start();
            _ = new ApiController(s_log, s_runtime);
            new ControlClient(s_log, s_runtime).Start();

            s_initialized = true;
            s_log.Info("ScumOxygen.Core initialized");
            return 0;
        }
        catch (Exception ex)
        {
            if (s_log == null)
            {
                OxygenPaths.Ensure();
                var logPath = Path.Combine(OxygenPaths.LogsDir, "Oxygen.log");
                s_log = new Logger(logPath);
            }
            s_log.Error($"Initialization failed: {ex}");
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "InitializeNative", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int InitializeNative(IntPtr args, int size)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "init.native.txt");
            File.AppendAllText(path, $"InitializeNative @ {DateTime.UtcNow:O}{Environment.NewLine}");
        }
        catch
        {
        }

        try
        {
            string launchArgs = "scum-server";
            if (args != IntPtr.Zero && size > 0)
            {
                launchArgs = Marshal.PtrToStringUTF8(args, size) ?? launchArgs;
            }

            return Initialize(launchArgs);
        }
        catch
        {
            return -1;
        }
    }

    public static void InitializeManaged()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "init.managed.txt");
            File.AppendAllText(path, $"InitializeManaged @ {DateTime.UtcNow:O}{Environment.NewLine}");
            Initialize("scum-server");
        }
        catch
        {
        }
    }

    [ComVisible(true)]
    public static void Shutdown()
    {
        if (!s_initialized) return;

        if (s_log != null && s_runtime != null)
        {
            s_log.Info("ScumOxygen.Core shutting down...");
            s_runtime.Stop();
        }

        s_initialized = false;
    }

    [ComVisible(true)]
    public static string GetVersion()
    {
        return "1.0.0";
    }
}
