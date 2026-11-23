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
        using var diagnostic = OrgExplorer.Workflow.PassiveSaveCapture.Install(
            typeof(Program).Assembly.GetType("OrgExplorer.Desktop.ControlledUpdateEffectsMarker") is null
                ? "rich-normal-entry-exe" : "workspace-controlled-entry-exe", typeof(MainWindow).Assembly);
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
