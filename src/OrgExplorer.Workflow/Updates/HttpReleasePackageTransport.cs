namespace OrgExplorer.Workflow.Updates;

public sealed class HttpReleasePackageTransport(HttpClient http) : IReleasePackageTransport
{
    public Task<Stream> OpenPackageAsync(AuthorizedRelease release, CancellationToken cancellationToken) =>
        http.GetStreamAsync(release.PackageUrl, cancellationToken);
}
