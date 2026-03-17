using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace ScumOxygen.Core;

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _corePath;

    public PluginLoadContext(string pluginPath, string corePath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _corePath = corePath;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name != null && assemblyName.Name.Equals("ScumOxygen.Core", StringComparison.OrdinalIgnoreCase))
        {
            var existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
            {
                var name = a.GetName().Name;
                return name != null && name.Equals("ScumOxygen.Core", StringComparison.OrdinalIgnoreCase);
            });
            if (existing != null) return existing;
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(_corePath);
        }
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
