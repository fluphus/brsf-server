using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;

namespace Brs.Portable.Shared;

public sealed class HostRequest
{
    public string Method { get; }
    public string Path { get; }
    // Raw HTTP query, including its leading '?' when nonempty.
    public string QueryString { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }
    public ReadOnlyMemory<byte> Body { get; }
    public int LocalPort { get; }
    public IPAddress? RemoteAddress { get; }
    public CancellationToken CancellationToken { get; }
    public bool HasQueryString => QueryString.Length != 0;
    public string? ContentType => Headers.ContainsKey("Content-Type") ? HeaderString("Content-Type") : null;

    public HostRequest(string method, string path, string queryString,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers, ReadOnlyMemory<byte> body,
        int localPort, IPAddress? remoteAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method); ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(queryString); ArgumentNullException.ThrowIfNull(headers);
        if (queryString.Length != 0 && queryString[0] != '?') throw new ArgumentException("Raw query must include '?'.", nameof(queryString));
        Method = method; Path = path; QueryString = queryString; Body = body;
        LocalPort = localPort; RemoteAddress = remoteAddress; CancellationToken = cancellationToken;
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers)
        {
            var values = pair.Value.ToArray();
            if (copy.TryGetValue(pair.Key, out var old)) values = old.Concat(values).ToArray();
            copy[pair.Key] = Array.AsReadOnly(values);
        }
        Headers = new ReadOnlyDictionary<string, IReadOnlyList<string>>(copy);
    }

    // Reproduce StringValues.ToString(), retaining duplicates via comma joining.
    public string HeaderString(string name) => Headers.TryGetValue(name, out var values) ? string.Join(',', values) : "";
}

public interface IHostDispatcher
{
    Task<HostResponse> DispatchAsync(HostRequest request);
}

public sealed class HostResponse : IDisposable, IAsyncDisposable
{
    private int disposed;
    private readonly Action? onDispose;
    public int StatusCode { get; }
    public Dictionary<string, IReadOnlyList<string>> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Stream Body { get; }
    public long? ContentLength { get; }

    public HostResponse(int statusCode, Stream body, long? contentLength = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null, Action? onDispose = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (statusCode is < 100 or > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));
        if (contentLength < 0) throw new ArgumentOutOfRangeException(nameof(contentLength));
        StatusCode = statusCode; Body = body; ContentLength = contentLength; this.onDispose = onDispose;
        if (headers is not null) foreach (var pair in headers)
        {
            var values = pair.Value.ToArray();
            if (Headers.TryGetValue(pair.Key, out var old)) values = old.Concat(values).ToArray();
            Headers[pair.Key] = Array.AsReadOnly(values);
        }
    }

    public void SetHeader(string name, params string[] values) => Headers[name] = Array.AsReadOnly(values.ToArray());

    public static HostResponse Empty(int statusCode) => Bytes(statusCode, ReadOnlyMemory<byte>.Empty);
    public static HostResponse Bytes(int statusCode, ReadOnlyMemory<byte> body, string? contentType = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
    {
        var response = new HostResponse(statusCode, new MemoryStream(body.ToArray(), writable: false), body.Length, headers);
        if (contentType is not null) response.SetHeader("Content-Type", contentType);
        return response;
    }
    public static HostResponse Json(int statusCode, object? value, JsonSerializerOptions? options = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null) =>
        Bytes(statusCode, JsonSerializer.SerializeToUtf8Bytes(value, options), "application/json; charset=utf-8", headers);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { Body.Dispose(); } finally { onDispose?.Invoke(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await Body.DisposeAsync().ConfigureAwait(false); } finally { onDispose?.Invoke(); }
    }
}
