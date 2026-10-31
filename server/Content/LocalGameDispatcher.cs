using Brs.Local.Bootstrap;
using Brs.Local.Transport;
using Brs.Portable.Game;
using Brs.Portable.Shared;

namespace Brs.Portable.Content;

public sealed class LocalGameDispatcher(GameApiDispatcher api, BootstrapSnapshot bootstrap, PacketCipher cipher,
    ImportedContentSnapshot? snapshot, Func<object> readinessProvider) : IHostDispatcher
{
    public async Task<HostResponse> DispatchAsync(HostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || request.Path.Equals("/api", StringComparison.OrdinalIgnoreCase))
            return await api.DispatchAsync(request).ConfigureAwait(false);
        var head = request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        var get = request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);
        if (!head && !get) { var denied = HostResponse.Empty(405); denied.SetHeader("Allow", "GET, HEAD"); return denied; }
        if (request.Path == "/.well-known/brsf-host" && !request.HasQueryString)
        {
            var health = HostResponse.Json(200, readinessProvider()); health.SetHeader("Cache-Control", "no-store");
            if (!head) return health;
            var length = health.ContentLength; health.Dispose(); return new HostResponse(200, Stream.Null, length, new Dictionary<string, IReadOnlyList<string>> { ["Content-Type"] = new[] { "application/json; charset=utf-8" }, ["Cache-Control"] = new[] { "no-store" } });
        }
        try
        {
            var query = RawQueryAdapter.Parse(request.QueryString);
            if (bootstrap.TryResolveNotice(request.Path, query, out var notice))
            {
                if (HasRange(request)) return HostResponse.Empty(416);
                var bytes = cipher.Encrypt(notice!);
                var response = new HostResponse(200, head ? Stream.Null : new MemoryStream(bytes, writable: false), bytes.Length);
                response.SetHeader("Content-Type", "application/octet-stream"); response.SetHeader("Cache-Control", "no-store"); return response;
            }
            if (snapshot is null) return HostResponse.Empty(request.Path.StartsWith("/Android/", StringComparison.Ordinal) ? 503 : 404);
            if (!snapshot.TryResolve(request.Path, query, out var item)) return HostResponse.Empty(404);
            if (HasRange(request)) return HostResponse.Empty(416);
            HostResponse result;
            if (head) result = new HostResponse(200, Stream.Null, item!.Length);
            else
            {
                var body = await snapshot.OpenVerifiedBodyAsync(item!, request.CancellationToken).ConfigureAwait(false);
                result = new HostResponse(200, body, item!.Length);
            }
            result.SetHeader("Content-Type", "application/octet-stream"); result.SetHeader("ETag", "\"" + item.Hash + "\""); return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or ArgumentException or ObjectDisposedException)
        { return HostResponse.Empty(503); }
    }

    private static bool HasRange(HostRequest request) => request.Headers.TryGetValue("Range", out var values) && values.Count > 0;
}
