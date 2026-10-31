using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Brs.Local.Transport;
using Brs.Portable.Content;
using Brs.Portable.Runtime;
using Brs.Portable.Transport;
using Brs.SupportedClient.KeyMaterial;
using Microsoft.Data.Sqlite;

namespace Brs.AndroidLocalHost;

internal sealed class HostEngine
{
    private readonly string root;
    public HostEngine(string root)
    {
        this.root = root;
    }

    public const int GamePort = 18445;
    public const int AdminPort = 18180;
    public const string AdminOrigin = "http://127.0.0.1:18180";
    public const string GameOrigin = "http://127.0.0.1:18445";
    private string stage = "created";
    private string? failure;
    private bool initialized;
    private LoopbackHttpServer? game, admin;
    public string Status => Volatile.Read(ref stage) + (Volatile.Read(ref failure) is { } reason ? " / " + reason : "") +
        "\n游戏18445 / 管理18180；" + (IsReady ? "API已启动" : "API未就绪");
    public bool IsReady => Volatile.Read(ref initialized) && game?.IsRunning == true && admin?.IsRunning == true;
    private void Stage(string value) => Volatile.Write(ref stage, value);
    public async Task RunAsync(CancellationToken token)
    {
        PacketCipher? cipher = null; PortableRuntime? runtime = null; ImportedContentSnapshot? content = null;
        byte[]? ownerHash = null;
        try
        {
            var hostVersion = ReadApplicationVersion();
            Stage("validating-store-domain");
            var domain = PrivateStore.ExistingDomain(root);
            Stage("verifying-fixed-inputs");
            PrivateStore.PrepareRuntime(root, token);
            Stage("loading-supported-cipher");
            cipher = SupportedClientCipher.Load(PrivateStore.KeyFile(root));
            ownerHash = SHA256.HashData(Encoding.UTF8.GetBytes(PrivateStore.OwnerKey(root)));
            Stage("composing-real-runtime");
            token.ThrowIfCancellationRequested();
            runtime = PortableRuntimeFactory.Create(PrivateStore.RuntimeRoot(root), PrivateStore.DbFile(root), cipher, ownerHash, AdminPort);
            CryptographicOperations.ZeroMemory(ownerHash); ownerHash = null;
            domain ??= PrivateStore.CreateDomain(root);
            int schema, tables;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PrivateStore.DbFile(root), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                connection.Open(); using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version"; schema = Convert.ToInt32(query.ExecuteScalar());
                query.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table'"; tables = Convert.ToInt32(query.ExecuteScalar());
            }
            if (schema != 24 || tables != 67) throw new InvalidDataException("private_schema_mismatch");
            var contentStore = PrivateStore.ContentRoot(root);
            // Open validates a published generation. No receipt means content is
            // absent; an existing malformed generation is a startup failure.
            if (ArchiveImporter.HasPublishedGeneration(contentStore))
            {
                Stage("opening-imported-content");
                content = ImportedContentSnapshot.Open(contentStore, runtime.Inputs.PathFor("--catalog"), runtime.Overlay, runtime.Inputs.PathFor("--local-content-manifest"));
            }
            var actualRuntime = runtime;
            var actualDomain = domain;
            var actualContent = content;
            object Readiness()
            {
                var selectedContent = actualRuntime.Overlay.CaptureDynamicContent();
                var apiReady = Volatile.Read(ref initialized) && game?.IsRunning == true;
                var adminReady = Volatile.Read(ref initialized) && admin?.IsRunning == true;
                return new
                {
                    format = "brsf-host", formatVersion = 1, hostVersion, domainID = actualDomain.DomainId,
                    compatibleClientVersion = "2.5.0", protocolVersion = 1,
                    gameOrigin = GameOrigin, adminOrigin = AdminOrigin, schemaVersion = schema,
                    tlsEnabled = false,
                    resourceRevision = selectedContent?.ResourceRevision ?? actualRuntime.Bootstrap.ResourceRevision, masterRevision = selectedContent?.MasterRevision ?? actualRuntime.Bootstrap.MasterRevision,
                    dbReady = true, apiReady, contentReady = actualContent is not null, adminReady, supportedClientCipher = true,
                    backendReady = apiReady && adminReady && actualContent is not null
                };
            }
            var dispatcher = new LocalGameDispatcher(runtime.GameApi, runtime.Bootstrap, cipher, content, Readiness);
            game = new LoopbackHttpServer(GamePort, dispatcher, PacketCipher.MaximumPacketBytes);
            admin = new LoopbackHttpServer(AdminPort, runtime.Management, 65536);
            Stage("binding-explicit-loopback-ports");
            token.ThrowIfCancellationRequested();
            await game.StartAsync(token); await admin.StartAsync(token);
            Volatile.Write(ref initialized, true);
            Stage(content is null ? "api-ready-content-absent" : "api-and-content-ready");
            var cancel = Task.Delay(Timeout.Infinite, token);
            var finished = await Task.WhenAny(game.Completion, admin.Completion, cancel);
            if (finished != cancel) { await finished; throw new IOException("listener_stopped_unexpectedly"); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Stage("stopping"); }
        catch (Exception error)
        {
            if (error is InvalidDataException && error.Message == "setup_required_resource_package")
            {
                Stage("setup-required-resource-package");
            }
            else
            {
                // Stage/type only. Never surface exception messages/paths/secrets.
                var safeReason = error is InvalidDataException && error.Message is "store_domain_marker_required" or "store_domain_identity_mismatch" or "store_domain_marker_invalid"
                    ? error.Message : error.GetType().Name;
                Volatile.Write(ref failure, safeReason);
                Stage("failed");
            }
        }
        finally
        {
            Volatile.Write(ref initialized, false);
            try
            {
                try { if (game is not null) await game.DisposeAsync(); }
                finally
                {
                    if (admin is not null) await admin.DisposeAsync();
                }
            }
            finally
            {
                try { if (runtime is not null) await runtime.DisposeAsync(); }
                finally
                {
                    try { content?.Dispose(); }
                    finally
                    {
                        try { cipher?.Dispose(); }
                        finally
                        {
                            if (ownerHash is not null) CryptographicOperations.ZeroMemory(ownerHash);
                        }
                    }
                }
            }
            if (failure is null && Volatile.Read(ref stage) != "setup-required-resource-package") Stage("stopped");
        }
    }

    private static string ReadApplicationVersion()
    {
        try
        {
            var context = Android.App.Application.Context;
            using var package = context.PackageManager?.GetPackageInfo(context.PackageName!, 0);
            var version = package?.VersionName;
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }
        catch { /* Assembly metadata remains available if package metadata cannot be read. */ }
        return typeof(HostEngine).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    public static HttpClient NewClient() => new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public static async Task<string?> OwnerCookieAsync(string root, string? existingCookie, CancellationToken token)
    {
        using var client = NewClient();
        if (!string.IsNullOrEmpty(existingCookie))
        {
            using var check = new HttpRequestMessage(HttpMethod.Get, AdminOrigin + "/management/v1/session");
            check.Headers.Add("Cookie", existingCookie);
            using var checkedResponse = await client.SendAsync(check, token);
            checkedResponse.EnsureSuccessStatusCode();
            using var session = JsonDocument.Parse(await checkedResponse.Content.ReadAsByteArrayAsync(token));
            if (session.RootElement.GetProperty("authenticated").GetBoolean()) return null;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, AdminOrigin + "/management/v1/session");
        request.Headers.Add("Origin", AdminOrigin);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { key = PrivateStore.OwnerKey(root) }));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues("Set-Cookie", out var cookies)) throw new InvalidOperationException("native_owner_auth_failed");
        var cookie = cookies.Single();
        if (!cookie.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("owner_cookie_not_httponly");
        return cookie;
    }
}
