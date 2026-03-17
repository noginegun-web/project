using System.IO;
using System.Text.Json;

namespace ScumOxygen.Core;

public sealed class DataFile<T> where T : new()
{
    private readonly string _path;

    public DataFile(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public T Load()
    {
        if (!File.Exists(_path))
        {
            var obj = new T();
            Save(obj);
            return obj;
        }

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<T>(json) ?? new T();
    }

    public void Save(T obj)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
