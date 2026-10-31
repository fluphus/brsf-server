using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Brs.Portable.Shared;

namespace Brs.Portable.Transport;

/// <summary>Narrow one-request, close-only HTTP transport. Contains no game or owner authentication.</summary>
public sealed class LoopbackHttpServer : IAsyncDisposable
{
    public const int MaximumConnections = 32;
    public const int MaximumRequestLineBytes = 4096;
    // Includes the request line, every CRLF and the final empty line.
    public const int MaximumHeaderBytes = 8192;
    public const int MaximumHeaders = 32;
    private readonly int requestedPort;
    private readonly int bodyLimit;
    private readonly IHostDispatcher dispatcher;
    private readonly TimeSpan headerDeadline;
    private readonly TimeSpan bodyDeadline;
    private readonly X509Certificate2? certificate;
    private readonly HashSet<string>? allowedHosts;
    private readonly TimeSpan handshakeTimeout;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private Run? run;
    private bool disposed;
    public int BoundPort => Volatile.Read(ref run)?.Port ?? 0;
    public bool IsRunning
    {
        get { var current = Volatile.Read(ref run); return current is not null && !current.Stop.IsCancellationRequested && !current.AcceptTask.IsCompleted; }
    }
    // Hosts must observe this task for unexpected accept-loop death, not infer health from a bound port.
    public Task Completion => Volatile.Read(ref run)?.AcceptTask ?? Task.CompletedTask;

    public LoopbackHttpServer(int port, IHostDispatcher dispatcher, int maximumRequestBodyBytes,
        TimeSpan? headerDeadline = null, TimeSpan? bodyDeadline = null, LoopbackTlsOptions? tls = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (maximumRequestBodyBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumRequestBodyBytes));
        requestedPort = port; this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        bodyLimit = maximumRequestBodyBytes;
        this.headerDeadline = Deadline(headerDeadline ?? TimeSpan.FromSeconds(10));
        this.bodyDeadline = Deadline(bodyDeadline ?? TimeSpan.FromSeconds(30));
        if (tls is not null)
        {
            certificate = tls.Certificate ?? throw new ArgumentException("TLS certificate is required.", nameof(tls));
            if (!certificate.HasPrivateKey) throw new ArgumentException("TLS certificate must have a private key.", nameof(tls));
            ArgumentNullException.ThrowIfNull(tls.AllowedHosts);
            if (tls.AllowedHosts.Count == 0 || tls.AllowedHosts.Any(host => !ExactHost(host)))
                throw new ArgumentException("TLS requires explicit, valid Host values without wildcards.", nameof(tls));
            allowedHosts = new HashSet<string>(tls.AllowedHosts, StringComparer.OrdinalIgnoreCase);
            handshakeTimeout = Deadline(tls.HandshakeTimeout ?? TimeSpan.FromSeconds(10));
        }
    }

    private static bool ExactHost(string host) => !string.IsNullOrEmpty(host) &&
        host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']') &&
        Uri.TryCreate("https://" + host, UriKind.Absolute, out var uri) && uri.Host.Length > 0 &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static TimeSpan Deadline(TimeSpan value) => value > TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue
        ? value : throw new ArgumentOutOfRangeException(nameof(value), "Deadline must be positive and bounded.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (run is not null) throw new InvalidOperationException("Listener is already started.");
            var listener = new TcpListener(IPAddress.Loopback, requestedPort);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(MaximumConnections); }
            catch { listener.Stop(); throw; }
            var next = new Run(listener);
            next.AcceptTask = AcceptAsync(next);
            Volatile.Write(ref run, next);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopLockedAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopLockedAsync()
    {
        var current = run;
        if (current is null) return;
        // Keep the run published until all of its connection cleanup has finished.
        current.Stop.Cancel(); current.Listener.Stop();
        foreach (var client in current.Connections.Keys) client.Dispose();
        Exception? acceptFailure = null;
        try { await current.AcceptTask.ConfigureAwait(false); }
        catch (Exception ex) { acceptFailure = ex; }
        foreach (var client in current.Connections.Keys) client.Dispose();
        try { await Task.WhenAll(current.Connections.Values).ConfigureAwait(false); }
        finally
        {
            current.Stop.Dispose(); current.Slots.Dispose();
            Volatile.Write(ref run, null);
        }
        if (acceptFailure is not null) throw new InvalidOperationException("HTTP accept loop failed.", acceptFailure);
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (!disposed) { disposed = true; await StopLockedAsync().ConfigureAwait(false); } }
        finally { lifecycle.Release(); }
    }

    private sealed class Run(TcpListener listener)
    {
        public readonly TcpListener Listener = listener;
        public readonly int Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        public readonly CancellationTokenSource Stop = new();
        public readonly SemaphoreSlim Slots = new(MaximumConnections, MaximumConnections);
        public readonly ConcurrentDictionary<TcpClient, Task> Connections = new();
        public Task AcceptTask = Task.CompletedTask;
    }

    private async Task AcceptAsync(Run current)
    {
        while (!current.Stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await current.Listener.AcceptTcpClientAsync(current.Stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            { if (current.Stop.IsCancellationRequested) return; throw; }
            if (!current.Slots.Wait(0)) { client.Dispose(); continue; }
            client.NoDelay = true;
            var task = HandleAsync(current, client);
            current.Connections[client] = task;
            _ = task.ContinueWith(_ => current.Connections.TryRemove(client, out var ignored),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(Run current, TcpClient client)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(current.Stop.Token);
        var token = connection.Token;
        var abortWatch = Task.CompletedTask;
        HostResponse? response = null;
        var finalStarted = false;
        Stream? stream = null;
        SslStream? secureStream = null;
        try
        {
            var network = client.GetStream();
            if (certificate is not null)
            {
                secureStream = new SslStream(network, leaveInnerStreamOpen: true);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(handshakeTimeout);
                await secureStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowRenegotiation = false,
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, handshake.Token).ConfigureAwait(false);
                // Publish only an authenticated stream: never send HTTP errors during/after failed TLS.
                stream = secureStream;
            }
            else stream = network;
            Parsed parsed;
            using (var header = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                header.CancelAfter(headerDeadline);
                parsed = await ReadHeadersAsync(stream, current.Port, header.Token).ConfigureAwait(false);
            }
            if (parsed.Continue)
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
            var body = new byte[parsed.Length];
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(bodyDeadline);
                var offset = 0;
                while (offset < body.Length)
                {
                    var n = await stream.ReadAsync(body.AsMemory(offset), deadline.Token).ConfigureAwait(false);
                    if (n == 0) throw new ProtocolFault(400);
                    offset += n;
                }
            }
            var request = new HostRequest(parsed.Method, parsed.Path, parsed.Query,
                parsed.Headers.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                body, current.Port, ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address, token);
            // Observe terminal socket errors without reading beyond Content-Length. A send-half-close
            // alone is not an abort: that client can still legitimately receive its response.
            abortWatch = WatchSocketErrorsAsync(client.Client, connection);
            response = await DispatchAsync(request, token).ConfigureAwait(false);
            var headers = ResponseHeaders(response, parsed.Method);
            finalStarted = true;
            await stream.WriteAsync(headers, token).ConfigureAwait(false);
            if (parsed.Method == "HEAD" || response.StatusCode is 204 or 304 || response.StatusCode < 200) return;
            var buffer = new byte[32768];
            if (response.ContentLength is { } length)
            {
                while (length > 0)
                {
                    var n = await ReadResponseAsync(response.Body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token).ConfigureAwait(false);
                    if (n == 0) throw new IOException("Response ended before its declared length.");
                    await stream.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                    length -= n;
                }
            }
            else
            {
                int n;
                while ((n = await ReadResponseAsync(response.Body, buffer, token).ConfigureAwait(false)) != 0)
                    await stream.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
            }
        }
        catch (ProtocolFault ex)
        { if (!finalStarted && !token.IsCancellationRequested) await SafeErrorAsync(stream, ex.Status, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { if (!finalStarted && !token.IsCancellationRequested) await SafeErrorAsync(stream, 408, token).ConfigureAwait(false); }
        catch (Exception)
        { if (!finalStarted && !token.IsCancellationRequested) await SafeErrorAsync(stream, 500, token).ConfigureAwait(false); }
        finally
        {
            connection.Cancel(); await abortWatch.ConfigureAwait(false);
            if (response is not null) await DisposeQuietlyAsync(response).ConfigureAwait(false);
            if (secureStream is not null)
                try { await secureStream.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            // Queue FIN after the complete response. Unread pipelined/invalid input is never drained.
            try { client.Client?.Shutdown(SocketShutdown.Send); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            client.Dispose(); current.Slots.Release();
        }
    }

    private static async Task WatchSocketErrorsAsync(Socket socket, CancellationTokenSource connection)
    {
        try
        {
            // An event-driven zero-byte receive waits for socket readiness/error without consuming
            // or buffering another request byte. Normal FIN (or unread pipelined data) completes
            // without canceling; only a terminal socket error aborts the response producer.
            _ = await socket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None, connection.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { connection.Cancel(); }
    }

    private static async Task<int> ReadResponseAsync(Stream body, Memory<byte> bytes, CancellationToken token) =>
        await body.ReadAsync(bytes, token).AsTask().WaitAsync(token).ConfigureAwait(false);

    private async Task<HostResponse> DispatchAsync(HostRequest request, CancellationToken token)
    {
        var pending = dispatcher.DispatchAsync(request);
        try { return await pending.WaitAsync(token).ConfigureAwait(false) ?? throw new InvalidOperationException("Missing response."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A dispatcher may ignore cancellation and return a resource later. It still has one owner.
            _ = DisposeLateAsync(pending); throw;
        }
    }
    private static async Task DisposeLateAsync(Task<HostResponse> pending)
    { try { var response = await pending.ConfigureAwait(false); if (response is not null) await DisposeQuietlyAsync(response).ConfigureAwait(false); } catch (Exception) { } }
    private static async ValueTask DisposeQuietlyAsync(HostResponse response)
    { try { await response.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }

    private sealed record Parsed(string Method, string Path, string Query, Dictionary<string, List<string>> Headers, int Length, bool Continue);
    private sealed class ProtocolFault(int status) : Exception { public int Status { get; } = status; }

    private async Task<Parsed> ReadHeadersAsync(Stream stream, int port, CancellationToken token)
    {
        var reader = new HeaderReader(stream, token);
        var requestLine = await reader.LineAsync(MaximumRequestLineBytes).ConfigureAwait(false);
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || !Token(parts[0]) || parts[2] is not ("HTTP/1.1" or "HTTP/1.0")) throw new ProtocolFault(400);
        var target = parts[1];
        if (target.Length == 0 || target[0] != '/' || target.StartsWith("//", StringComparison.Ordinal) ||
            target.Any(c => c is <= ' ' or >= (char)127 || c is '\\' or '#')) throw new ProtocolFault(400);
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var query = question < 0 ? "" : target[question..];
        if (path.Contains('%') || path.Split('/').Any(s => s is "." or "..")) throw new ProtocolFault(400);
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        while (true)
        {
            var line = await reader.LineAsync(MaximumHeaderBytes).ConfigureAwait(false);
            if (line.Length == 0) break;
            if (++count > MaximumHeaders || line[0] is ' ' or '\t') throw new ProtocolFault(400);
            var colon = line.IndexOf(':');
            if (colon <= 0 || !Token(line[..colon])) throw new ProtocolFault(400);
            var name = line[..colon]; var value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(c => c < ' ' && c != '\t' || c >= (char)127)) throw new ProtocolFault(400);
            if (!headers.TryGetValue(name, out var values)) headers.Add(name, values = []);
            values.Add(value);
        }
        if (!headers.TryGetValue("Host", out var host) || host.Count != 1 ||
            !(allowedHosts is not null ? allowedHosts.Contains(host[0]) :
                host[0].Equals("localhost:" + port, StringComparison.OrdinalIgnoreCase) || host[0] == "127.0.0.1:" + port))
            throw new ProtocolFault(400);
        if (headers.ContainsKey("Transfer-Encoding")) throw new ProtocolFault(400);
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var contentLength))
        {
            if (contentLength.Count != 1 || !DecimalLength(contentLength[0], out var size)) throw new ProtocolFault(400);
            if (size > bodyLimit) throw new ProtocolFault(413);
            length = (int)size;
        }
        var expectContinue = false;
        if (headers.TryGetValue("Expect", out var expect))
        {
            if (expect.Count != 1 || !expect[0].Equals("100-continue", StringComparison.OrdinalIgnoreCase)) throw new ProtocolFault(417);
            expectContinue = true;
        }
        return new Parsed(parts[0], path, query, headers, length, expectContinue);
    }

    private static bool DecimalLength(string text, out long value)
    {
        value = 0;
        return text.Length > 0 && text.All(c => c is >= '0' and <= '9') &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
    private static bool Token(string text) => text.Length > 0 && text.All(c =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || "!#$%&'*+-.^_`|~".Contains(c));

    private sealed class HeaderReader(Stream stream, CancellationToken token)
    {
        private readonly byte[] one = new byte[1];
        private int total;
        private async Task<byte> ByteAsync()
        {
            if (++total > MaximumHeaderBytes) throw new ProtocolFault(431);
            if (await stream.ReadAsync(one, token).ConfigureAwait(false) != 1) throw new ProtocolFault(400);
            return one[0];
        }
        public async Task<string> LineAsync(int maximum)
        {
            var bytes = new List<byte>();
            while (true)
            {
                var b = await ByteAsync().ConfigureAwait(false);
                if (b == 13)
                {
                    if (await ByteAsync().ConfigureAwait(false) != 10) throw new ProtocolFault(400);
                    if (bytes.Count + 2 > maximum) throw new ProtocolFault(431);
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                if (b == 10 || b is < 32 and not 9 || b >= 127) throw new ProtocolFault(400);
                bytes.Add(b);
                if (bytes.Count + 2 > maximum) throw new ProtocolFault(431);
            }
        }
    }

    private static byte[] ResponseHeaders(HostResponse response, string method)
    {
        foreach (var (name, values) in response.Headers)
        {
            if (!Token(name) || values.Count == 0 || values.Any(v => v is null || v.Any(c => c < ' ' || c >= (char)127)))
                throw new InvalidDataException("Invalid response header.");
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Transfer encoding is adapter controlled.");
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) &&
                (values.Count != 1 || !values[0].Equals("close", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Connection is adapter controlled.");
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                (values.Count != 1 || !DecimalLength(values[0], out var length) || response.ContentLength != length))
                throw new InvalidDataException("Contradictory content length.");
        }
        var text = new StringBuilder("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(Reason(response.StatusCode)).Append("\r\n");
        foreach (var (name, values) in response.Headers)
        {
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var value in values) text.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        // 204 has no representation body/framing; HEAD and 304 may advertise representation length.
        if (response.StatusCode >= 200 && response.StatusCode != 204 && response.ContentLength is { } count)
            text.Append("Content-Length: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        text.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static string Reason(int status) => status switch
    { 100 => "Continue", 200 => "OK", 204 => "No Content", 304 => "Not Modified", 400 => "Bad Request", 408 => "Request Timeout", 413 => "Content Too Large", 417 => "Expectation Failed", 431 => "Request Header Fields Too Large", 500 => "Internal Server Error", _ => "Response" };

    private static async Task SafeErrorAsync(Stream? stream, int status, CancellationToken token)
    {
        if (stream is null) return;
        var body = Encoding.ASCII.GetBytes(Reason(status) + "\n");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/plain; charset=us-ascii\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        try { await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(body, token).ConfigureAwait(false); }
        catch (Exception) { }
    }
}
