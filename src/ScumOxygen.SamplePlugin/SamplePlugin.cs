using ScumOxygen.Core;

namespace ScumOxygen.SamplePlugin;

public sealed class SamplePlugin : IPlugin
{
    public string Name => "SamplePlugin";

    public void OnInit(Logger log)
    {
        log.Info("SamplePlugin initialized");
    }

    public void OnShutdown()
    {
        // no-op
    }
}
