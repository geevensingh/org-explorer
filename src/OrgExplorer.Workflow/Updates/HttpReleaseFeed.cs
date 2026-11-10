using System.Net;

namespace OrgExplorer.Workflow.Updates;

public sealed class ReleaseFeedException(ReleaseCheckStatus status, string message) : Exception(message)
{
    public ReleaseCheckStatus Status { get; } = status;
}

public sealed class HttpReleaseFeed(HttpClient client, Uri manifestUrl) : IReleaseFeed
{
    public async Task<byte[]> ReadManifestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.UserAgent.ParseAdd("OrgExplorer/1");
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden &&
             response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))
            throw new ReleaseFeedException(ReleaseCheckStatus.RateLimited, "Update checks are rate limited by the release host. Retry later.");
        if (!response.IsSuccessStatusCode)
            throw new ReleaseFeedException(ReleaseCheckStatus.Failed, $"Update feed returned HTTP {(int)response.StatusCode}. Retry later.");
        if (response.Content.Headers.ContentLength > UpdateJson.MaximumBytes)
            throw new UpdateVerificationException("Update metadata exceeds the supported size.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > UpdateJson.MaximumBytes)
                throw new UpdateVerificationException("Update metadata exceeds the supported size.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }
}
