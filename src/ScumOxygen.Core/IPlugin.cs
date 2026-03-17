namespace ScumOxygen.Core;

public interface IPlugin
{
    string Name { get; }
    void OnInit(Logger log);
    void OnShutdown();
}
