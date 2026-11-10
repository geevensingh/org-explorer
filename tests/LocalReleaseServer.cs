using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OrgExplorer.TestFixtures;

internal sealed class LocalReleaseServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task serving;
    private int requests;
    private bool disposed;
    public Uri ManifestUrl { get; }
    public Func<byte[]> Response { get; set; }
    public int StatusCode { get; set; } = 200;
    public string ExtraHeaders { get; set; } = "";
    public bool IncludeLength { get; set; } = true;
    public Task? HoldResponse { get; set; }
    public TaskCompletionSource RequestArrived { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Requests => Volatile.Read(ref requests);

    public LocalReleaseServer(Func<byte[]> response, int port = 0)
    {
        Response = response;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        ManifestUrl = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/manifest");
        serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                TcpClient accepted;
                try { accepted = await listener.AcceptTcpClientAsync(lifetime.Token); }
                catch (InvalidOperationException) when (lifetime.IsCancellationRequested) { return; }
                using var connection = accepted;
                await using var stream = connection.GetStream();
                var header = new List<byte>();
                var single = new byte[1];
                while (header.Count < 8192 && await stream.ReadAsync(single, lifetime.Token) == 1)
                {
                    header.Add(single[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
                }
                if (!Encoding.ASCII.GetString(header.ToArray()).StartsWith("GET /manifest HTTP/1.1\r\n", StringComparison.Ordinal))
                    throw new InvalidOperationException("The product requested an unexpected endpoint; no package downloads are authorized in these tests.");
                Interlocked.Increment(ref requests);
                RequestArrived.TrySetResult();
                if (HoldResponse is { } held) await held.WaitAsync(lifetime.Token);
                var body = Response();
                var length = IncludeLength ? $"Content-Length: {body.Length}\r\n" : "";
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {StatusCode} Fixture\r\n{length}{ExtraHeaders}Connection: close\r\nContent-Type: application/json\r\n\r\n");
                await stream.WriteAsync(headers, lifetime.Token);
                await stream.WriteAsync(body, lifetime.Token);
            }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested && error is OperationCanceledException or SocketException or IOException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        listener.Stop();
        try { serving.GetAwaiter().GetResult(); }
        finally { lifetime.Dispose(); }
    }
}
