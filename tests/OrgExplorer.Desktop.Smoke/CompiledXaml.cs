using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Interop;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static int VerifyCompiledXaml(string executable)
    {
        var assemblyPath = Path.ChangeExtension(executable, ".dll");
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
            resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var settings = Path.Combine(Path.GetTempPath(), "OrgExplorer-Xaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settings);
        Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
        AssemblyLoadContext.Default.Resolving += Resolve;
        Application? application = null;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var type = assembly.GetType("OrgExplorer.Desktop.MainWindow", throwOnError: true)!;
            var window = (Window)Activator.CreateInstance(type)!;
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The compiled-XAML check must never show the window or create its native handle.");
            window.Close();
            Console.WriteLine("PASS: actual compiled MainWindow BAML and connectors load without showing a window.");
            return 0;
        }
        finally
        {
            application?.Shutdown();
            AssemblyLoadContext.Default.Resolving -= Resolve;
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
            Directory.Delete(settings, true);
        }
    }
}
