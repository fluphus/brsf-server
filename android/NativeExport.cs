namespace Brs.AndroidLocalHost;

internal sealed record NativeExportSpec(string Path, string Mime, string FileName);

internal static class NativeExportPolicy
{
    public const string Origin = "http://127.0.0.1:18180";
    public static bool SameOrigin(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
        uri.Host == "127.0.0.1" && uri.Port == 18180 && uri.UserInfo.Length == 0;
    public static NativeExportSpec? Resolve(string? url, string method = "GET")
    {
        if (method != "GET" || !SameOrigin(url)) return null;
        var uri = new Uri(url!);
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var path = uri.AbsolutePath;
        if (path == "/management/v1/backup") return new(path, "application/octet-stream", "brsf-server-backup.sqlite3");
        var parts = path.Split('/');
        if (parts.Length != 6 || parts[1] != "management" || parts[2] != "v1" || parts[3] != "accounts" || parts[5] != "export") return null;
        var uid = parts[4];
        if (uid.Length is < 1 or > 20 || uid[0] == '0' || !uid.All(char.IsAsciiDigit)) return null;
        return new(path, "application/json", "brsf-account-" + uid + ".json");
    }
}

internal static class NativeExportTransfer
{
    // The caller supplies a non-redirecting client and native-only owner cookie.
    // A picker destination is opened only after a successful HTTP response.
    public static async Task<long> SaveAsync(HttpClient client, NativeExportSpec export, string cookie,
        Func<Stream> openOutput, CancellationToken token)
    {
        var target = NativeExportPolicy.Origin + export.Path;
        if (NativeExportPolicy.Resolve(target) != export || string.IsNullOrEmpty(cookie)) throw new InvalidDataException("invalid_native_export");
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode(); // includes rejection of every redirect
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = openOutput();
        var buffer = new byte[65536]; long copied = 0; int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); copied = checked(copied + read); }
        if (response.Content.Headers.ContentLength is { } length && copied != length) throw new IOException("incomplete_native_export");
        await output.FlushAsync(token).ConfigureAwait(false);
        return copied;
    }
}
