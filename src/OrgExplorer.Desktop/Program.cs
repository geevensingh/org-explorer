using Velopack;

namespace OrgExplorer.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Fast installer hooks must exit before constructing WPF or opening user data.
        // Pending packages are not authorization to apply an update (tickets #17/#18).
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
