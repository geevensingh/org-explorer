using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using OrgExplorer.Workflow.Updates;
using Velopack.Locators;

namespace OrgExplorer.Desktop;

// Compiled only by the explicit hosted fixture build. Production publish must not contain this type.
internal sealed class ControlledUpdateEffectsMarker;

internal static class UpdateEffects
{
    private static string Fixture => Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE")
        ?? throw new IOException("The controlled external update effects fixture is missing.");

    public static IReleasePackageTransport Transport(HttpClient http) => new ControlledTransport();
    public static IUpdateRestart Restart(UpdateRestartJournal journal) => new ObservedRestart(journal);

    private sealed class ControlledTransport : IReleasePackageTransport
    {
        public async Task<Stream> OpenPackageAsync(AuthorizedRelease release, CancellationToken cancellationToken)
        {
            var fixture = Fixture;
            var request = Path.Combine(fixture, "package-request.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { url = release.PackageUrl, release.FileName }), cancellationToken);
            return new FileStream(Path.Combine(fixture, "package.nupkg"), FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }

    private sealed class ObservedRestart(UpdateRestartJournal journal) : IUpdateRestart
    {
        public void ApplyAndRestart(AuthorizedRelease release, string packagePath, string? workspace)
        {
            var files = workspace is null ? null : Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
                .ToDictionary(file => Path.GetRelativePath(workspace, file), file =>
                {
                    using var input = File.OpenRead(file);
                    return Convert.ToHexString(SHA256.HashData(input));
                });
            File.WriteAllText(Path.Combine(Fixture, "handoff.json"), JsonSerializer.Serialize(new
            {
                version = release.Version.ToString(), workspace, packagePath, release.Sha256, workspaceFiles = files
            }));
            if (VelopackLocator.Current.CurrentlyInstalledVersion is null)
                throw new IOException("Controlled installer process boundary: unpackaged smoke deliberately does not install or restart.");
            new VelopackUpdateRestart(journal).ApplyAndRestart(release, packagePath, workspace);
        }
    }
}
