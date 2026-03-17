using System;
using ScumOxygen.Core;

namespace Oxygen.Csharp.API;

public static class Oxygen
{
    public static TimerService Timers { get; internal set; } = null!;
    public static PermissionService Permissions { get; internal set; } = null!;
    public static HookService Hooks { get; internal set; } = null!;
    public static WebApiService Web { get; internal set; } = null!;
    public static Func<string, PlayerBase?>? FindPlayerImpl { get; internal set; }
    public static Func<IReadOnlyList<PlayerBase>>? ListPlayersImpl { get; internal set; }

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
}
