using System.Net;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Brs.Local.Accounts;
using Brs.Local.Management;
using Brs.Portable.Runtime;
using Brs.Portable.Content;
using Brs.Portable.Shared;
using Brs.SupportedClient.KeyMaterial;
using Microsoft.Data.Sqlite;

// These identity, transaction and HTTP-boundary checks deliberately use synthetic
// inputs. They do not replace private Master/catalog or original-client validation.
var temporary = Path.Combine(Path.GetTempPath(), "brs-source-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    passed++; Console.WriteLine("PASS " + name);
}
var database = Path.Combine(temporary, "accounts.sqlite3");
var materials = new InventoryMaterialConfig("synthetic-identity-fixture", 0, 0, 0, "", "", "", 99999999, []);
var store = new AccountStore(database, inventoryMaterial: materials);
long Scalar(string statement)
{
    using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
    connection.Open(); using var command = connection.CreateCommand(); command.CommandText = statement;
    return Convert.ToInt64(command.ExecuteScalar());
}
void Sql(string statement)
{
    using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
    connection.Open(); using var command = connection.CreateCommand(); command.CommandText = statement; command.ExecuteNonQuery();
}
JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
try
{
    Check(Scalar("PRAGMA user_version") == 24 && Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table'") == 67, "current persistent schema");
    var nonce = store.IssueNonce()!;
    var request = new CreateAccountRequest(nonce, new byte[32]);
    Sql("CREATE TRIGGER synthetic_fail_enrollment BEFORE INSERT ON economy_transactions BEGIN SELECT RAISE(ABORT,'synthetic-failure');END");
    var failed = false;
    try { store.Create(request); } catch (SqliteException) { failed = true; }
    Check(failed && Scalar("SELECT COUNT(*) FROM accounts") == 0 && Scalar("SELECT COUNT(*) FROM creation_receipts") == 0 && Scalar("SELECT COUNT(*) FROM nonce_issues") == 1, "late failure rolls back account, receipt and nonce consumption");
    Sql("DROP TRIGGER synthetic_fail_enrollment");
    var first = store.Create(request);
    Check(first.Status == 200 && Scalar("SELECT COUNT(*) FROM accounts") == 1 && Scalar("SELECT COUNT(*) FROM nonce_issues") == 0, "retry after rollback creates exactly one account");
    var uuid = (string)first.Action!["uuid"]!;
    Check(store.Create(request).Action!["uuid"]!.Equals(uuid) && Scalar("SELECT COUNT(*) FROM accounts") == 1, "creation receipt replays original identity");
    Check(store.Create(new CreateAccountRequest(nonce, Enumerable.Repeat((byte)1, 32).ToArray())).Status == 409, "nonce replay rejects changed fingerprint");
    Check(new AccountStore(database, inventoryMaterial: materials).Login(uuid).Status == 200, "fresh store reopens persisted account");
    var account = Element(store.ManagementAccounts().Value).GetProperty("items")[0];
    var uid = account.GetProperty("uid").GetString()!;
    var lease = Element(store.ManagementBeginMaintenance(uid, "test-owner").Value).GetProperty("leaseId").GetString()!;
    Check(store.Login(uuid).Status == 409, "same store maintenance lease blocks game login");
    Check(store.ManagementEndMaintenance(uid, "other-owner", lease).Status == 409, "lease rejects another owner");
    store.ManagementReleaseOwner("test-owner");
    Check(store.Login(uuid).Status == 200, "releasing owner resumes normal login");

    var assets = ManagementAssets.LoadBundled();
    Check(assets.TryGet("/", out var page) && page.Bytes.Length > 0, "source checkout embeds management page without icons");
    var ownerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    const int port = 18181;
    await using var host = new ManagementHost(new ManagementHostOptions(port, "injected"), new AccountStoreManagementBackend(store), assets,
        SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));
    string? cookie = null;
    async Task<HostResponse> Send(string method, string path, object? body = null, string? hostHeader = null, string? origin = null)
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = [hostHeader ?? $"127.0.0.1:{port}"], ["Origin"] = [origin ?? $"http://127.0.0.1:{port}"],
            ["Content-Type"] = ["application/json; charset=utf-8"]
        };
        if (cookie is not null) headers["Cookie"] = [cookie];
        return await host.DispatchAsync(new HostRequest(method, path, "", headers,
            body is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body), port, IPAddress.Loopback));
    }
    await using (var response = await Send("HEAD", "/app.js"))
        Check(response.StatusCode == 200 && response.ContentLength > 0 && response.Body.ReadByte() == -1, "HEAD retains asset length with empty body");
    await using (var response = await Send("GET", "/management/v1/accounts"))
        Check(response.StatusCode == 401, "account projection requires owner session");
    await using (var response = await Send("POST", "/management/v1/session", new { key = ownerKey }, origin: "http://untrusted.example"))
        Check(response.StatusCode == 403, "session rejects foreign origin");
    await using (var response = await Send("GET", "/", hostHeader: "untrusted.example"))
        Check(response.StatusCode == 400, "management rejects untrusted Host");
    await using (var response = await Send("POST", "/management/v1/session", new { key = "invalid" }))
        Check(response.StatusCode == 401, "incorrect owner credential rejected");
    await using (var response = await Send("POST", "/management/v1/session", new { key = ownerKey }))
    {
        Check(response.StatusCode == 200, "owner credential authenticates");
        cookie = response.Headers["Set-Cookie"].Single().Split(';', 2)[0];
        Check(!cookie.Contains(ownerKey, StringComparison.Ordinal), "opaque cookie hides owner credential");
    }
    await using (var response = await Send("GET", "/management/v1/accounts"))
    using (var json = await JsonDocument.ParseAsync(response.Body))
        Check(response.StatusCode == 200 && json.RootElement.GetProperty("items").GetArrayLength() == 1 && !json.RootElement.GetRawText().Contains(uuid, StringComparison.Ordinal), "authenticated projection uses actual database without game UUID");
    await using (var response = await Send("POST", "/management/v1/accounts/" + uid + "/maintenance", new { }))
        Check(response.StatusCode == 403, "owner cookie alone cannot bypass CSRF");
    await using (var response = await Send("GET", "/api/user/login"))
        Check(response.StatusCode == 404, "management listener excludes game API");

    var invalidKey = Path.Combine(temporary, "wrong-key.bin"); File.WriteAllBytes(invalidKey, new byte[16]);
    failed = false; try { using var wrong = SupportedClientCipher.Load(invalidKey); } catch (InvalidDataException) { failed = true; }
    Check(failed, "protocol key is validated against supported-client identity");
    var invalidConfig = Path.Combine(temporary, "materials.json"); File.WriteAllText(invalidConfig, "{}");
    failed = false; try { InventoryMaterialConfig.Load(invalidConfig); } catch (InvalidDataException) { failed = true; }
    Check(failed, "arbitrary material JSON cannot replace verified configuration");
    failed = false; try { RuntimeInputs.LoadAndVerify(temporary); } catch (IOException) { failed = true; }
    Check(failed, "missing runtime inputs are a visible configuration failure");
    var packageStore = Path.Combine(temporary, "package-store");
    async Task RejectPackage(Action<TarWriter> write, string name)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Gnu, leaveOpen: true)) write(writer);
        tar.Position = 0;
        var rejected = false;
        try { using var forwardOnly = new ForwardOnlyStream(tar); await ResourcePackage.ImportAsync(forwardOnly, packageStore); }
        catch (Exception error) when (error is InvalidDataException or IOException) { rejected = true; }
        Check(rejected && !ArchiveImporter.HasPublishedGeneration(packageStore) && ResourcePackage.ScanIncompleteImports(packageStore).Count == 0, name);
    }
    await RejectPackage(_ => { }, "empty resource package fails without publishing or leaving a stage");
    await RejectPackage(writer => writer.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, "resource-package-manifest.json") { LinkName = "../outside" }),
        "resource package links are rejected without touching outside paths");
    await RejectPackage(writer => writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "../resource-package-manifest.json") { DataStream = new MemoryStream([1]) }),
        "resource package traversal names are rejected");
    var packageManifest = typeof(ResourcePackage).Assembly.GetManifestResourceStream("Brs.Portable.Content.ResourcePackageManifest.json")!;
    using var manifestBuffer = new MemoryStream(); packageManifest.CopyTo(manifestBuffer); var packageManifestBytes = manifestBuffer.ToArray();
    await RejectPackage(writer => writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "resource-package-manifest.json") { DataStream = new MemoryStream(new byte[packageManifestBytes.Length]) }),
        "resource package cannot redefine trusted material hashes");
    await RejectPackage(writer => writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "resource-package-manifest.json") { DataStream = new MemoryStream(packageManifestBytes) }),
        "missing resource package material fails atomically");
    await RejectPackage(writer =>
    {
        writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "resource-package-manifest.json") { DataStream = new MemoryStream(packageManifestBytes) });
        writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "resource-package-manifest.json") { DataStream = new MemoryStream(packageManifestBytes) });
    }, "duplicate package members are rejected on a nonseekable stream");
    await RejectPackage(writer =>
    {
        writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "resource-package-manifest.json") { DataStream = new MemoryStream(packageManifestBytes) });
        writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "unexpected.txt") { DataStream = new MemoryStream([1]) });
    }, "unexpected package members are rejected on a nonseekable stream");
    var preserved = Path.Combine(packageStore, "keep-existing-data"); File.WriteAllText(preserved, "unchanged");
    using (var cancel = new CancellationTokenSource())
    using (var data = new MemoryStream(new byte[1024]))
    using (var forward = new ForwardOnlyStream(data, cancel.Cancel))
    {
        failed = false;
        try { await ResourcePackage.ImportAsync(forward, packageStore, cancel.Token); }
        catch (OperationCanceledException) { failed = true; }
        Check(failed && File.ReadAllText(preserved) == "unchanged" && ResourcePackage.ScanIncompleteImports(packageStore).Count == 0,
            "canceled streaming import cleans its stage and preserves existing user data");
    }
    await RejectPackage(_ => { }, "failed import can be retried without removing existing user data");
    if (args.Length != 0)
    {
        if (args.Length != 2 || args[0] != "--content-store") throw new ArgumentException("Optional: --content-store DIRECTORY");
        var privateAssets = Path.GetFullPath(args[1]);
        ResourcePackage.Configure(privateAssets); privateAssets = ResourcePackage.MaterialsRoot(privateAssets);
        var preparedRuntime = Path.Combine(temporary, "prepared-runtime");
        SourceRuntime.Prepare(preparedRuntime, privateAssets);
        using var privateCipher = SupportedClientCipher.Load(Path.Combine(privateAssets, "protocol-key.bin"));
        await using var runtime = PortableRuntimeFactory.Create(preparedRuntime, Path.Combine(temporary, "private-inputs.sqlite3"), privateCipher,
            SHA256.HashData(RandomNumberGenerator.GetBytes(32)), port);
        Check(runtime.Inputs.Entries.Count == 30 && runtime.LoadedRules.Count == 22, "source configuration and imported material load all current rules");
        Check(runtime.Overlay.CaptureDynamicContent() is { ContentPolicyVersion: 1 }, "current Expert policy and externally supplied templates compose");
        Check(runtime.Accounts.ManagementExpertPeriod().Status == 200, "current management Expert route uses the same composed backend");
        Check(ManagementAssets.LoadBundled().IsThumbnail("/management/images/cards/30010002.png"), "management thumbnails load from the imported resource material");
        using var contentSnapshot = ImportedContentSnapshot.Open(Path.GetFullPath(args[1]), runtime.Inputs.PathFor("--catalog"),
            runtime.Overlay, runtime.Inputs.PathFor("--local-content-manifest"));
        Check(contentSnapshot.ResourceCount == 14374 && contentSnapshot.ResourceRevision == runtime.Overlay.CaptureDynamicContent()!.ResourceRevision &&
            runtime.Overlay.ManifestSha256 == Brs.Local.Content.LocalContentOverlay.PortableSingleExpertManifestSha256 &&
            runtime.Overlay.Fingerprint == Brs.Local.Content.LocalContentOverlay.SingleExpertManifestSha256,
            "imported CDN opens with normalized metadata while retaining the existing logical policy identity");
        var resourceQuery = new Dictionary<string, IReadOnlyList<string>> { ["v"] = ["142"] };
        Check(contentSnapshot.TryResolve("/Android/142/" + ImportedContentSnapshot.CatalogName, resourceQuery, out var catalogItem),
            "imported CDN resolves the supported original catalog");
        await using (var body = await contentSnapshot.OpenVerifiedBodyAsync(catalogItem!))
            Check(body.ReadByte() >= 0, "imported CDN verifies and opens actual original catalog bytes");
    }
    Console.WriteLine($"{passed} checks passed (synthetic transaction, configuration and management boundary fixtures).");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(temporary, recursive: true);
}
