using System;
using System.IO;
using System.Text.Json;

namespace ScumOxygen.Core;

public sealed class RuntimeConfig
{
    public bool EnableLocalWeb { get; set; } = true;
    public string LocalWebPrefix { get; set; } = "http://+:8090/";
    public string ApiKey { get; set; } = "";
    public string[] AllowedIps { get; set; } = Array.Empty<string>();
    public bool EnableCors { get; set; } = true;
    public string ServerId { get; set; } = "server-1";
    public string ServerName { get; set; } = "SCUM Server";
    public string DatabasePath { get; set; } = "";
    public string MapImageUrl { get; set; } = "https://scum-map.com/images/interactive_map/scum/island.jpg";
    public string MapSourceUrl { get; set; } = "https://scum-map.com/en/map/";

    public static RuntimeConfig Load(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var cfg = new RuntimeConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            return cfg;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RuntimeConfig>(json) ?? new RuntimeConfig();
    }
}
