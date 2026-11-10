using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrgExplorer.Workflow.Updates;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ReleaseSignerInteropTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public async Task Publication_signer_output_is_consumed_by_the_actual_client_and_binds_the_original_bytes()
    {
        var root = FindCheckout();
        var directory = Path.Combine(root, ".test-workspaces", $"release-interop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            manifestUrl = "https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json",
            keys = new[] { new { keyId = "ephemeral-interop", algorithm = "ES256", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) } }
        });
        var package = Encoding.UTF8.GetBytes("abc");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "public-trust.json"), configuration);
            await File.WriteAllBytesAsync(Path.Combine(directory, "geevensingh.OrgExplorer-0.2.0-full.nupkg"), package);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", """
                $ErrorActionPreference = 'Stop'
                . $env:RELEASE_TEST_SCRIPT
                $key = Open-ReleaseSigningKey -TrustFile (Join-Path $env:RELEASE_TEST_DIRECTORY 'public-trust.json') -KeyId ephemeral-interop -PrivateKeyBase64 $env:RELEASE_TEST_PRIVATE
                $env:RELEASE_TEST_PRIVATE = $null
                try {
                    $null = New-SignedReleaseManifest -ReleaseDirectory $env:RELEASE_TEST_DIRECTORY -Version 0.2.0 -Key $key -KeyId ephemeral-interop
                } finally { $key.Dispose() }
                """ })
                start.ArgumentList.Add(argument);
            start.Environment["RELEASE_TEST_SCRIPT"] = Path.Combine(root, "eng", "Release.ps1");
            start.Environment["RELEASE_TEST_DIRECTORY"] = directory;
            start.Environment["RELEASE_TEST_PRIVATE"] = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            using var process = Process.Start(start)!;
            start.Environment.Remove("RELEASE_TEST_PRIVATE");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            Assert.True(process.ExitCode == 0, $"Controlled publication signer failed: {await output}\n{await error}");
            var envelope = await File.ReadAllBytesAsync(Path.Combine(directory, "org-explorer-update.win-x64.json"));
            var checker = new UpdateChecker(new ControlledFeed(envelope), UpdateTrust.Parse(configuration), new Version("0.1.0"));

            var result = await checker.CheckAsync();

            Assert.Equal(ReleaseCheckStatus.Available, result.Status);
            var release = Assert.IsType<AuthorizedRelease>(result.Release);
            Assert.Equal("v0.2.0", release.ReleaseTag);
            Assert.Equal("ephemeral-interop", release.KeyId);
            Assert.Equal(new Uri("https://github.com/geevensingh/org-explorer/releases/download/v0.2.0/geevensingh.OrgExplorer-0.2.0-full.nupkg"), release.PackageUrl);
            await release.VerifyPackageAsync(new MemoryStream(package));
            await Assert.ThrowsAsync<UpdateVerificationException>(() => release.VerifyPackageAsync(new MemoryStream("abd"u8.ToArray())));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string FindCheckout()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "eng", "Release.ps1"))) return directory.FullName;
        throw new InvalidOperationException("Run the signer interoperability test inside its source checkout.");
    }

    private sealed class ControlledFeed(byte[] envelope) : IReleaseFeed
    {
        public Task<byte[]> ReadManifestAsync(CancellationToken cancellationToken) => Task.FromResult(envelope);
    }
}
