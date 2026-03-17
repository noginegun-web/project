using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ScumOxygen.Core;

public sealed class PluginManager
{
    private readonly List<IPlugin> _loaded = new();

    public IReadOnlyList<IPlugin> Loaded => _loaded;

    public void LoadFromDirectory(string dir, Logger log)
    {
        if (!Directory.Exists(dir))
        {
            log.Info($"Plugins directory not found: {dir}");
            return;
        }

        foreach (var dll in Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                var types = asm.GetTypes().Where(t => !t.IsAbstract && typeof(IPlugin).IsAssignableFrom(t));

                foreach (var t in types)
                {
                    if (Activator.CreateInstance(t) is IPlugin plugin)
                    {
                        plugin.OnInit(log);
                        _loaded.Add(plugin);
                        log.Info($"Loaded plugin: {plugin.Name} ({t.FullName})");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"Failed to load {dll}: {ex.Message}");
            }
        }
    }

    public void ShutdownAll(Logger log)
    {
        foreach (var p in _loaded)
        {
            try
            {
                p.OnShutdown();
                log.Info($"Shutdown plugin: {p.Name}");
            }
            catch (Exception ex)
            {
                log.Error($"Shutdown failed for {p.Name}: {ex.Message}");
            }
        }
        _loaded.Clear();
    }
}
