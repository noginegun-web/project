using System;
using System.Collections.Generic;
using Oxygen.Csharp.API;

namespace Oxygen.Csharp.Core;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class InfoAttribute : Attribute
{
    public string Name { get; }
    public string Author { get; }
    public string Version { get; }

    public InfoAttribute(string name, string author, string version)
    {
        Name = name;
        Author = author;
        Version = version;
    }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DescriptionAttribute : Attribute
{
    public string Text { get; }
    public DescriptionAttribute(string text) => Text = text;
}

[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class CommandAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }

    public CommandAttribute(string name, string description = "")
    {
        Name = name;
        Description = description;
    }
}

[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class PermissionAttribute : Attribute
{
    public string Permission { get; }
    public PermissionAttribute(string permission) => Permission = permission;
}

[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class HookAttribute : Attribute
{
    public string EventName { get; }
    public HookAttribute(string eventName) => EventName = eventName;
}

public abstract class OxygenPlugin
{
    public virtual void OnLoad() { }
    public virtual void OnUnload() { }
    public virtual void OnPluginInit() { }
    public virtual void OnPluginUnload() { }

    // Player events
    public virtual void OnPlayerConnected(PlayerBase player) { }
    public virtual void OnPlayerDisconnected(PlayerBase player) { }
    public virtual void OnPlayerRespawned(PlayerBase player) { }
    public virtual bool OnPlayerTakeItemInHands(PlayerBase player, string itemName) => true;
    public virtual bool OnPlayerChat(PlayerBase player, string message, int chatType) => true;
    public virtual void OnPlayerDeath(PlayerBase victim, PlayerBase? killer, KillInfo info) { }
    public virtual void OnPlayerKill(PlayerBase killer, PlayerBase victim, KillInfo info) { }

    // Helpers
    protected T LoadConfig<T>() where T : new() => global::Oxygen.Csharp.API.Oxygen.Config<T>(GetConfigKey()).Load();
    protected void SaveConfig<T>(T obj) where T : new() => global::Oxygen.Csharp.API.Oxygen.Config<T>(GetConfigKey()).Save(obj);

    protected T? LoadData<T>(string name) where T : new() => global::Oxygen.Csharp.API.Oxygen.Data<T>(name).Load();
    protected void SaveData<T>(string name, T obj) where T : new() => global::Oxygen.Csharp.API.Oxygen.Data<T>(name).Save(obj);

    protected Guid Every(float seconds, Action action) => global::Oxygen.Csharp.API.Oxygen.Timers.Every(TimeSpan.FromSeconds(seconds), action);
    protected void CancelTimer(Guid id) => global::Oxygen.Csharp.API.Oxygen.Timers.Cancel(id);

    protected PlayerBase? FindPlayer(string nameOrId) => global::Oxygen.Csharp.API.Oxygen.FindPlayer(nameOrId);
    protected IReadOnlyList<PlayerBase> GetPlayers() => global::Oxygen.Csharp.API.Oxygen.ListPlayers();

    private string GetConfigKey()
    {
        var info = GetType().GetCustomAttributes(typeof(InfoAttribute), inherit: false);
        if (info.Length == 1 && info[0] is InfoAttribute attr && !string.IsNullOrWhiteSpace(attr.Name))
            return attr.Name.Replace(" ", "_");
        return GetType().Name;
    }
}
