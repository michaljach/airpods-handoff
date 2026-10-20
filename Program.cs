namespace AirPodsHandoff;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\AirPodsHandoff", out bool firstInstance);
        if (!firstInstance) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
