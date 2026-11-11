using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using OrgExplorer.Workflow.Updates;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class UpdateSettingsTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "updates-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Startup_checks_are_enabled_by_default_and_user_preference_survives_reopen()
    {
        using var fixture = new SignedReleaseFixture();
        var path = Path.Combine(root, "settings.json");
        var settings = new UpdateSettings(fixture.Checker(), path);
        Assert.True(settings.CheckOnStartup);
        Assert.Equal("Startup update checks are enabled.", settings.StartupPreferenceGuidance);
        await settings.StartupAsync();
        Assert.Equal(ReleaseCheckStatus.Available, settings.Result.Status);
        Assert.True(settings.SetCheckOnStartup(false));
        Assert.Equal("Startup update checks are disabled.", settings.StartupPreferenceGuidance);
        var reopened = new UpdateSettings(fixture.Checker(), path);
        Assert.False(reopened.CheckOnStartup);
        Assert.Equal("Startup update checks are disabled.", reopened.StartupPreferenceGuidance);
        await reopened.StartupAsync();
        Assert.Equal(ReleaseCheckStatus.NotChecked, reopened.Result.Status);
        await reopened.CheckAsync();
        Assert.Equal(ReleaseCheckStatus.Available, reopened.Result.Status);
    }

    [Fact]
    public async Task Slow_actual_feed_does_not_block_local_work_and_bad_checks_do_not_erase_the_workspace()
    {
        using var fixture = new SignedReleaseFixture();
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyCurrent(workspace);
        using var server = new LocalReleaseServer(() => fixture.Sign());
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldResponse = held.Task;
        using var http = new HttpClient();
        var checker = new UpdateChecker(new HttpReleaseFeed(http, server.ManifestUrl),
            UpdateTrust.Parse(fixture.Configuration(server.ManifestUrl.ToString())), new Version("0.1.0"));
        var settings = new UpdateSettings(checker, Path.Combine(root, "settings.json"));
        var check = settings.StartupAsync();
        await server.RequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(check.IsCompleted);
        Assert.Equal(ReleaseCheckStatus.Checking, settings.Result.Status);
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(app.OpenWorkspace(workspace), app.Status);
            Assert.True(app.EditPreferredName("avery@example.com", "Local edit while update check waits"), app.Status);
            Assert.True(app.CanSafelyClose);
        }
        held.SetResult();
        await check;
        Assert.Equal(ReleaseCheckStatus.Available, settings.Result.Status);
        server.StatusCode = 429;
        await settings.CheckAsync();
        Assert.Equal(ReleaseCheckStatus.RateLimited, settings.Result.Status);
        Assert.Null(settings.Result.Release);
        Assert.Contains("unknown", settings.Result.Message);
        server.StatusCode = 200;
        server.Response = () => "{}"u8.ToArray();
        await settings.CheckAsync();
        Assert.Equal(ReleaseCheckStatus.VerificationFailed, settings.Result.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(workspace), reopened.Status);
        Assert.Equal("Local edit while update check waits", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.True(reopened.EditPreferredName("avery@example.com", "Local edit after verification failure"), reopened.Status);
        Assert.True(reopened.CanSafelyClose);
    }

    [Theory]
    [InlineData(429, "", ReleaseCheckStatus.RateLimited)]
    [InlineData(403, "X-RateLimit-Remaining: 0\r\n", ReleaseCheckStatus.RateLimited)]
    [InlineData(403, "", ReleaseCheckStatus.Failed)]
    [InlineData(404, "", ReleaseCheckStatus.Failed)]
    [InlineData(503, "", ReleaseCheckStatus.Failed)]
    public async Task Actual_http_errors_remain_failures_not_false_up_to_date(int status, string headers, ReleaseCheckStatus expected)
    {
        using var fixture = new SignedReleaseFixture();
        using var server = new LocalReleaseServer(() => fixture.Sign()) { StatusCode = status, ExtraHeaders = headers };
        using var http = new HttpClient();
        var checker = new UpdateChecker(new HttpReleaseFeed(http, server.ManifestUrl),
            UpdateTrust.Parse(fixture.Configuration(server.ManifestUrl.ToString())), new Version("0.1.0"));
        var result = await checker.CheckAsync();
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Release);
        Assert.DoesNotContain("up to date", result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Actual_feed_is_bounded_even_without_a_declared_content_length(bool includeLength)
    {
        using var fixture = new SignedReleaseFixture();
        using var server = new LocalReleaseServer(() => new byte[70000]) { IncludeLength = includeLength };
        using var http = new HttpClient();
        var checker = new UpdateChecker(new HttpReleaseFeed(http, server.ManifestUrl),
            UpdateTrust.Parse(fixture.Configuration(server.ManifestUrl.ToString())), new Version("0.1.0"));
        Assert.Equal(ReleaseCheckStatus.VerificationFailed, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task Disconnected_feed_is_explicit_and_retry_uses_the_same_trust_policy()
    {
        using var fixture = new SignedReleaseFixture();
        Uri endpoint;
        using (var server = new LocalReleaseServer(() => fixture.Sign())) endpoint = server.ManifestUrl;
        using var http = new HttpClient();
        var checker = new UpdateChecker(new HttpReleaseFeed(http, endpoint),
            UpdateTrust.Parse(fixture.Configuration(endpoint.ToString())), new Version("0.1.0"));
        Assert.Equal(ReleaseCheckStatus.Offline, (await checker.CheckAsync()).Status);
        using var restarted = new LocalReleaseServer(() => fixture.Sign(), endpoint.Port);
        Assert.Equal(ReleaseCheckStatus.Available, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task Completed_http_requests_allow_immediate_server_shutdown()
    {
        using var http = new HttpClient();
        for (var iteration = 0; iteration < 128; iteration++)
        {
            using var server = new LocalReleaseServer(() => "{}"u8.ToArray());
            using var response = await http.GetAsync(server.ManifestUrl);
            Assert.Equal("{}", await response.Content.ReadAsStringAsync());
            server.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_shutdown_does_not_hide_unexpected_response_faults(bool disposed)
    {
        var responseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception expected = disposed
            ? new ObjectDisposedException("response-handler")
            : new InvalidOperationException("Unexpected response failure.");
        using var server = new LocalReleaseServer(() =>
        {
            responseReached.SetResult();
            throw expected;
        });
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(server.ManifestUrl.Host, server.ManifestUrl.Port);
        await client.GetStream().WriteAsync("GET /manifest HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray());
        await responseReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(expected, Record.Exception(server.Dispose));
    }

    [Fact]
    public void Corrupt_or_unwritable_preferences_are_visible_and_do_not_claim_a_successful_change()
    {
        using var fixture = new SignedReleaseFixture();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{broken");
        var settings = new UpdateSettings(fixture.Checker(), path);
        Assert.True(settings.CheckOnStartup);
        Assert.Equal("Startup update checks are enabled.", settings.StartupPreferenceGuidance);
        Assert.Contains("could not be read", settings.PreferenceError);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(settings.SetCheckOnStartup(false));
            Assert.True(settings.CheckOnStartup);
            Assert.Equal("Startup update checks are enabled.", settings.StartupPreferenceGuidance);
            Assert.Contains("not saved", settings.PreferenceError);
            Assert.Equal("{broken", File.ReadAllText(path));
        }
        Assert.True(settings.SetCheckOnStartup(false));
        Assert.Null(settings.PreferenceError);
        Assert.Equal("Startup update checks are disabled.", settings.StartupPreferenceGuidance);
        Assert.False(new UpdateSettings(fixture.Checker(), path).CheckOnStartup);
        Assert.Empty(Directory.GetFiles(root, "*.pending"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
