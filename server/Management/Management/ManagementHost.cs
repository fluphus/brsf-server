using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Brs.Portable.Shared;

namespace Brs.Local.Management;

// Framework-free extraction of the original management dispatcher. No listener or key-file access.
public sealed class ManagementHost : IHostDispatcher, IAsyncDisposable
{
    private const string CookieName = "brsf_owner";
    private const string Prefix = "/management/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ManagementSessions sessions;
    private readonly ManagementHostOptions options;
    private readonly IManagementBackend backend;
    private readonly ManagementAssets assets;

    public ManagementHost(ManagementHostOptions options, IManagementBackend backend, ManagementAssets assets,
        byte[] ownerHash, TimeProvider? clock = null)
    {
        if (options.Port is < 1024 or > 65535) throw new InvalidDataException("Invalid management port.");
        if (ownerHash.Length != 32) throw new InvalidDataException("Invalid owner hash.");
        this.options = options;
        this.backend = backend;
        this.assets = assets;
        sessions = new ManagementSessions(ownerHash.ToArray(), backend.ReleaseOwner, clock ?? TimeProvider.System);
    }

    public Task<HostResponse> DispatchAsync(HostRequest request)
    {
        HostResponse response;
        try { request.CancellationToken.ThrowIfCancellationRequested(); response = Dispatch(request); }
        catch (RequestFailure failure) { response = Error(failure.Status, failure.Code); }
        catch (JsonException) { response = Error(400, "invalid_json"); }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { response = Error(408, "request_timeout"); }
        catch (Exception)
        {
            // No request body, key, cookie, UID, filesystem path, or raw exception is logged.
            response = Error(500, "internal_error");
        }
        response.SetHeader("Cache-Control", response.StatusCode == 200 && (request.Method is "GET" or "HEAD") && assets.IsThumbnail(request.Path)
            ? "private, max-age=86400" : "no-store");
        response.SetHeader("X-Content-Type-Options", "nosniff");
        response.SetHeader("Referrer-Policy", "no-referrer");
        response.SetHeader("X-Frame-Options", "DENY");
        response.SetHeader("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'");
        return Task.FromResult(response);
    }

    private HostResponse Dispatch(HostRequest request)
    {
        if (request.RemoteAddress is not { } address || !IPAddress.IsLoopback(address) ||
            request.LocalPort != options.Port ||
            !(string.Equals(request.HeaderString("Host"), $"127.0.0.1:{options.Port}", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(request.HeaderString("Host"), $"localhost:{options.Port}", StringComparison.OrdinalIgnoreCase)))
            throw new RequestFailure(400, "host_invalid");

        var path = request.Path;
        if ((IsMethod(request, "GET") || IsMethod(request, "HEAD")) && assets.TryGet(path, out var asset))
        {
            var response = IsMethod(request, "HEAD")
                ? new HostResponse(200, Stream.Null, asset.Bytes.Length)
                : HostResponse.Bytes(200, asset.Bytes);
            response.SetHeader("Content-Type", asset.ContentType);
            return response;
        }
        if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal)) throw new RequestFailure(404, "not_found");
        if (request.HasQueryString) throw new RequestFailure(400, "invalid_request");
        var read = IsMethod(request, "GET");
        var write = IsMethod(request, "POST") || IsMethod(request, "DELETE");
        if (!read && !write) throw new RequestFailure(405, "method_not_allowed");
        if (write) ValidateWriteHeaders(request);
        var cookie = ReadCookie(request);
        var session = sessions.Find(cookie);
        if (path == Prefix + "/session")
        {
            if (read)
                return Json(session is null ? new { authenticated = false } : (object)new { authenticated = true, csrfToken = session.CsrfToken });
            if (IsMethod(request, "POST"))
            {
                using var body = ReadBody(request);
                var root = BodyObject(body.RootElement, "key");
                var key = RequiredString(root, "key", 128);
                var login = sessions.Login(key);
                if (login.Error is { } error) throw new RequestFailure(error == "rate_limited" ? 429 : error == "session_capacity" ? 503 : 401, error);
                // Successful reauthentication replaces the caller's previous session and releases its leases.
                sessions.Remove(cookie);
                var response = Json(new { authenticated = true, csrfToken = login.Session!.CsrfToken });
                response.SetHeader("Set-Cookie", CookieSettings(login.Cookie!, login.Session.ExpiresAt));
                return response;
            }
            RequireSession(session);
            ValidateCsrf(request, session!);
            using (var body = ReadBody(request)) BodyObject(body.RootElement);
            sessions.Remove(cookie);
            var logout = Json(new { authenticated = false });
            logout.SetHeader("Set-Cookie", CookieSettings("", DateTimeOffset.UnixEpoch));
            return logout;
        }
        RequireSession(session);
        if (write) ValidateCsrf(request, session!);
        var route = ResolveRoute(path, request.Method);
        if (route is null) throw new RequestFailure(404, "not_found");
        var lease = HeaderValues(request, "X-BRSF-Lease");
        if (route.Value.LeaseRequired && (lease.Count != 1 || lease[0] is not { Length: > 0 and <= 128 }))
            throw new RequestFailure(409, "maintenance_required");
        using var document = write ? ReadBody(request) : null;
        var result = backend.Invoke(route.Value.Action, route.Value.Uid, session!.Identity, lease.Count == 1 ? lease[0] : null,
            document?.RootElement ?? default);
        if (result.Status is < 200 or >= 300) throw new RequestFailure(SafeStatus(result.Status), SafeCode(result.Code));
        if (result.Value is ManagementDownload download)
        {
            Stream? stream = null;
            try
            {
                stream = download.Open();
                var safeName = download.DownloadName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? download.DownloadName : "backup.sqlite3";
                var response = new HostResponse(result.Status, stream, stream.CanSeek ? stream.Length : null, onDispose: download.Dispose);
                response.SetHeader("Content-Type", download.ContentType);
                response.SetHeader("Content-Disposition", "attachment; filename=\"" + safeName + "\"");
                return response;
            }
            catch
            {
                try { stream?.Dispose(); } finally { download.Dispose(); }
                throw;
            }
        }
        var json = Json(result.Value, result.Status);
        if (route.Value.Action == ManagementAction.Export)
            json.SetHeader("Content-Disposition", $"attachment; filename=\"account-{route.Value.Uid}.json\"");
        return json;
    }

    private static (ManagementAction Action, string? Uid, bool LeaseRequired)? ResolveRoute(string path, string method)
    {
        var relative = path[(Prefix.Length + 1)..];
        if (method == "GET")
        {
            if (relative == "expert-period") return (ManagementAction.ExpertPeriod, null, false);
            if (relative == "status") return (ManagementAction.Status, null, false);
            if (relative == "catalog") return (ManagementAction.Catalog, null, false);
            if (relative == "accounts") return (ManagementAction.Accounts, null, false);
            if (relative == "backup") return (ManagementAction.Backup, null, false);
        }
        if (method == "POST" && relative == "expert-period") return (ManagementAction.SetExpertPeriod,null,false);
        if (method == "POST" && relative == "accounts") return (ManagementAction.Create, null, false);
        var segments = relative.Split('/');
        if (segments.Length < 2 || segments[0] != "accounts" || !ValidUid(segments[1])) return null;
        var uid = segments[1];
        if (method == "GET")
        {
            if (segments.Length == 2) return (ManagementAction.Account, uid, false);
            if (segments.Length == 3 && segments[2] == "export") return (ManagementAction.Export, uid, false);
        }
        if (segments.Length == 3 && segments[2] == "maintenance")
        {
            if (method == "POST") return (ManagementAction.BeginMaintenance, uid, false);
            if (method == "DELETE") return (ManagementAction.EndMaintenance, uid, true);
        }
        if (method != "POST") return null;
        if (segments.Length == 4 && segments[2] == "maintenance" && segments[3] == "renew")
            return (ManagementAction.RenewMaintenance, uid, true);
        if (segments.Length != 3) return null;
        return segments[2] switch
        {
            "preview" => (ManagementAction.Preview, uid, true),
            "commands" => (ManagementAction.Execute, uid, true),
            "credentials" => (ManagementAction.Credentials, uid, true),
            _ => null
        };
    }

    private static bool ValidUid(string uid) => uid.Length is > 0 and <= 20 && uid[0] != '0' && uid.All(char.IsAsciiDigit);


    private static bool IsMethod(HostRequest request, string method) => string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase);
    private static IReadOnlyList<string> HeaderValues(HostRequest request, string name) => request.Headers.TryGetValue(name, out var values) ? values : Array.Empty<string>();

    private static void ValidateWriteHeaders(HostRequest request)
    {
        var origins = HeaderValues(request, "Origin");
        if (origins.Count != 1 || !string.Equals(origins[0], "http://" + request.HeaderString("Host"), StringComparison.OrdinalIgnoreCase))
            throw new RequestFailure(403, "origin_invalid");
        if (!MediaTypeHeaderValue.TryParse(NormalizeContentType(request.ContentType), out var parsed) ||
            !string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new RequestFailure(415, "json_required");
        // Structured +json media types are not part of this API contract.
        var contentType = request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new RequestFailure(415, "json_required");
    }

    // ASP.NET's media-type parser accepts a terminal semicolon and empty parameter values.
    private static string? NormalizeContentType(string? value)
    {
        if (value is null) return null;
        var normalized = new StringBuilder(value.Length);
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            normalized.Append(character);
            if (character == '"')
            {
                var backslashes = 0;
                for (var before = i - 1; before >= 0 && value[before] == '\\'; before--) backslashes++;
                if (backslashes % 2 == 0) quoted = !quoted;
            }
            if (!quoted && character == '=')
            {
                var next = i + 1;
                while (next < value.Length && value[next] is ' ' or '\t') next++;
                if (next == value.Length || value[next] == ';') normalized.Append("\"\"");
            }
        }
        var result = normalized.ToString().TrimEnd(' ', '\t');
        if (result.EndsWith(';')) result = result[..^1];
        return result;
    }

    private static void RequireSession(ManagementSession? session)
    {
        if (session is null) throw new RequestFailure(401, "auth_required");
    }

    private static void ValidateCsrf(HostRequest request, ManagementSession session)
    {
        var token = HeaderValues(request, "X-BRSF-CSRF");
        if (token.Count != 1 || token[0] is not { Length: 43 } value ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(session.CsrfToken)))
            throw new RequestFailure(403, "csrf_invalid");
    }

    private static JsonDocument ReadBody(HostRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.Body.Length > ManagementHostOptions.MaximumBodyBytes) throw new RequestFailure(413, "body_too_large");
        var document = JsonDocument.Parse(request.Body, new JsonDocumentOptions
        {
            MaxDepth = ManagementHostOptions.MaximumJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
        });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new RequestFailure(400, "invalid_json");
            ValidateJson(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (names.Count >= 64 || property.Name.Length > 100 || !names.Add(property.Name)) throw new RequestFailure(400, "invalid_json");
                ValidateJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > ManagementHostOptions.MaximumArrayItems) throw new RequestFailure(400, "invalid_request");
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
        }
        else if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 8192) throw new RequestFailure(400, "invalid_request");
    }

    internal static JsonElement BodyObject(JsonElement root, params string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new RequestFailure(400, "invalid_request");
        return root;
    }

    internal static string RequiredString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: > 0 } value || value.Length > maximumLength)
            throw new RequestFailure(400, "invalid_request");
        return value;
    }


    private static string CookieSettings(string value, DateTimeOffset expiresAt) => CookieName + "=" + value +
        "; expires=" + expiresAt.UtcDateTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
        "; path=" + Prefix + "; samesite=strict; httponly";

    // Match Request.Cookies parsing for this fixed cookie name: nonempty RFC6265 values,
    // quotes retained, URL unescape, and the last valid duplicate across all header values.
    private static string? ReadCookie(HostRequest request)
    {
        string? value = null;
        foreach (var header in HeaderValues(request, "Cookie"))
        {
            if (string.IsNullOrEmpty(header)) continue;
            foreach (var part in header.Split(';'))
            {
                var pair = part.AsSpan().Trim(" \t");
                var separator = pair.IndexOf('=');
                if (separator < 0 || !pair[..separator].Equals(CookieName, StringComparison.OrdinalIgnoreCase)) continue;
                var raw = pair[(separator + 1)..];
                if (raw.IsEmpty) continue;
                var content = raw;
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') content = raw[1..^1];
                var valid = true;
                foreach (var character in content)
                    if (!(character == '\x21' || character is >= '\x23' and <= '\x2b' or >= '\x2d' and <= '\x3a' or >= '\x3c' and <= '\x5b' or >= '\x5d' and <= '\x7e')) { valid = false; break; }
                if (!valid) continue;
                value = Uri.UnescapeDataString(raw.ToString());
            }
        }
        return value;
    }

    private static int SafeStatus(int status) => status is 400 or 401 or 403 or 404 or 409 or 410 or 413 or 429 or 503 ? status : 500;
    private static string SafeCode(string? code) => code is "invalid_request" or "account_not_found" or "operation_conflict" or "stale_revision" or
        "maintenance_required" or "maintenance_busy" or "maintenance_owned" or "operation_gone" or "catalog_unavailable" or
        "storage_unavailable" or "capacity_exceeded" or "battle_active" ? code : "internal_error";


    private static HostResponse Json(object? value, int status = 200) => HostResponse.Json(status, value, JsonOptions);

    private static HostResponse Error(int status, string code)
    {
        var message = code switch
        {
            "auth_required" => "请先登录管理界面。", "invalid_key" => "管理密钥无效。",
            "origin_invalid" or "host_invalid" => "请求来源无效。", "csrf_invalid" => "页面验证已失效，请重新登录。",
            "json_required" => "请求须使用 JSON。", "invalid_json" or "invalid_request" => "请求内容无效。",
            "body_too_large" => "请求内容过大。", "rate_limited" => "登录尝试过于频繁，请稍后重试。",
            "session_capacity" => "管理会话已满，请退出其他会话或稍后重试。", "request_timeout" => "请求超时。",
            "account_not_found" => "账号不存在。", "operation_conflict" => "操作编号已用于不同请求。",
            "stale_revision" => "存档已发生变化，请刷新后重新预览。", "maintenance_required" => "请先取得账号维护权。",
            "maintenance_busy" => "账号正在进行关卡、循环或派遣，暂时无法维护。", "maintenance_owned" => "账号正由其他会话维护。",
            "operation_gone" => "此操作对应的账号已删除。", "catalog_unavailable" => "管理目录未就绪。",
            "storage_unavailable" => "存档暂时不可用。", "capacity_exceeded" => "本次修改超出库存或数值上限。",
            "battle_active" => "有战斗或循环尚未结束，请先完成或退出后再切换时期。",
            "not_found" => "接口不存在。", "method_not_allowed" => "请求方法不支持。", _ => "管理服务暂时无法完成请求。"
        };

        var response = Json(new { error = new { code, message } }, status);
        if (status == 413) response.SetHeader("Connection", "close");
        return response;
    }

    public ValueTask DisposeAsync() { sessions.Dispose(); return ValueTask.CompletedTask; }

    internal sealed class RequestFailure(int status, string code) : Exception
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }
}
