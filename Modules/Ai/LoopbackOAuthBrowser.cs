using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CanvasDesktop.Ai;

/// <summary>Ephemeral IPv4 loopback listener, bound before the system browser is opened.</summary>
public sealed class LoopbackOAuthBrowser(Action<Uri> openBrowser) : IOAuthBrowser
{
    public async Task<Uri> AuthorizeAsync(Func<Uri, Uri> authorizationUrl, CancellationToken cancellation)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = new Uri($"http://127.0.0.1:{port}/auth/callback");
        openBrowser(authorizationUrl(redirect));
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation).ConfigureAwait(false);
            using var stream = client.GetStream();
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var bytes = new List<byte>();
            var buffer = new byte[1024];
            bool complete = false;
            try
            {
                while (bytes.Count < 16384)
                {
                    int count = await stream.ReadAsync(buffer, requestDeadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    bytes.AddRange(buffer.AsSpan(0, count).ToArray());
                    if (Encoding.ASCII.GetString(bytes.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal)) { complete = true; break; }
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { continue; }
            string raw = Encoding.ASCII.GetString(bytes.ToArray());
            string[] lines = raw.Split("\r\n");
            string[] first = lines[0].Split(' ');
            bool valid = complete && bytes.Count <= 16384 && first.Length == 3 && first[0] == "GET"
                && first[2] == "HTTP/1.1" && first[1].StartsWith("/auth/callback?", StringComparison.Ordinal)
                && lines.Count(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) == 1
                && lines.Any(l => l.Equals($"Host: 127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase));
            string body = valid ? "Sign-in response received. Return to Canvas Window Composer." : "Unsupported callback request.";
            byte[] reply = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\n\r\n{body}");
            try { await stream.WriteAsync(reply, requestDeadline.Token).ConfigureAwait(false); }
            catch (IOException) { }
            if (valid) return new Uri(redirect, first[1]);
        }
        throw new AiConnectionException("Too many invalid callback requests. Start sign-in again.");
    }
}
