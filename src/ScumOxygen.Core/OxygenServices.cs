using System;
using System.Linq;
using System.Reflection;
using ScumOxygen.Core;

namespace Oxygen.Csharp.API;

public static class Oxygen
{
    private static TimerService? _timers;
    private static PermissionService? _permissions;
    private static HookService? _hooks;
    private static WebApiService? _web;
    private static Func<string, PlayerBase?>? _findPlayerImpl;
    private static Func<IReadOnlyList<PlayerBase>>? _listPlayersImpl;

    public static TimerService Timers
    {
        get => _timers ??= ResolveFromSiblingAssembly<TimerService>("Timers")
            ?? throw new InvalidOperationException("Oxygen.Timers is not initialized.");
        internal set => _timers = value;
    }

    public static PermissionService Permissions
    {
        get => _permissions ??= ResolveFromSiblingAssembly<PermissionService>("Permissions")
            ?? throw new InvalidOperationException("Oxygen.Permissions is not initialized.");
        internal set => _permissions = value;
    }

    public static HookService Hooks
    {
        get => _hooks ??= ResolveFromSiblingAssembly<HookService>("Hooks")
            ?? throw new InvalidOperationException("Oxygen.Hooks is not initialized.");
        internal set => _hooks = value;
    }

    public static WebApiService Web
    {
        get => _web ??= ResolveFromSiblingAssembly<WebApiService>("Web")
            ?? throw new InvalidOperationException("Oxygen.Web is not initialized.");
        internal set => _web = value;
    }

    public static Func<string, PlayerBase?>? FindPlayerImpl
    {
        get => _findPlayerImpl ??= ResolveFromSiblingAssembly<Func<string, PlayerBase?>>("FindPlayerImpl");
        internal set => _findPlayerImpl = value;
    }

    public static Func<IReadOnlyList<PlayerBase>>? ListPlayersImpl
    {
        get => _listPlayersImpl ??= ResolveFromSiblingAssembly<Func<IReadOnlyList<PlayerBase>>>("ListPlayersImpl");
        internal set => _listPlayersImpl = value;
    }

    public static DataFile<T> Data<T>(string name) where T : new()
    {
        var path = System.IO.Path.Combine(ScumOxygen.Core.OxygenPaths.DataDir, name + ".json");
        return new DataFile<T>(path);
    }

    public static DataFile<T> Config<T>(string name) where T : new()
    {
        var path = System.IO.Path.Combine(ScumOxygen.Core.OxygenPaths.ConfigsDir, name + ".json");
        return new DataFile<T>(path);
    }

    public static Database Database(string connectionString) => new Database(connectionString);

    public static PlayerBase? FindPlayer(string nameOrId)
    {
        return FindPlayerImpl?.Invoke(nameOrId);
    }

    public static IReadOnlyList<PlayerBase> ListPlayers()
    {
        return ListPlayersImpl?.Invoke() ?? Array.Empty<PlayerBase>();
    }

    private static T? ResolveFromSiblingAssembly<T>(string memberName) where T : class
    {
        var currentAssembly = typeof(Oxygen).Assembly;
        var candidates = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a =>
                !ReferenceEquals(a, currentAssembly) &&
                string.Equals(a.GetName().Name, currentAssembly.GetName().Name, StringComparison.OrdinalIgnoreCase));

        foreach (var assembly in candidates)
        {
            var oxygenType = assembly.GetType(typeof(Oxygen).FullName ?? "Oxygen.Csharp.API.Oxygen", throwOnError: false);
            if (oxygenType == null) continue;

            var property = oxygenType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property?.GetMethod != null)
            {
                try
                {
                    if (property.GetValue(null) is T propertyValue)
                        return propertyValue;
                }
                catch
                {
                }
            }

            var field = oxygenType.GetField($"_{char.ToLowerInvariant(memberName[0])}{memberName.Substring(1)}", BindingFlags.NonPublic | BindingFlags.Static)
                ?? oxygenType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is T fieldValue)
                return fieldValue;
        }

        return null;
    }
}
