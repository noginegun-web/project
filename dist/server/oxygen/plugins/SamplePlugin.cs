using System;
using Oxygen.Csharp.Core;
using Oxygen.Csharp.API;
using OxygenApi = Oxygen.Csharp.API.Oxygen;

[Info("SamplePlugin", "Codex", "1.0.0")]
[Description("Пример плагина с командой и таймером")]
public class SamplePlugin : OxygenPlugin
{
    public override void OnPluginInit()
    {
        System.Console.WriteLine("SamplePlugin loaded");
    }

    [Command("hello", "Пример команды")]
    public void Hello(string[] args)
    {
        Server.Broadcast("Привет из SamplePlugin");
    }
}
