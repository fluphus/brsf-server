using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Brs.Local.Management;
using Brs.Local.Transport;
using Brs.Portable.Content;
using Brs.Portable.Runtime;
using Brs.Portable.Transport;
using Brs.SupportedClient.KeyMaterial;

if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("""
        BRS local host
        --data-directory DIRECTORY --check-config
        --data-directory DIRECTORY [--game-port 18445] [--admin-port 18180]
        --data-directory DIRECTORY --import-content FILE

        Import one brs-resources.tar package after a source-only build.
        Source includes rules, bootstrap settings and management labels.
        Content import validates and copies the supported archive to the selected data directory.
        The server listens on loopback. Stop with Ctrl+C. Owner credentials are in data/owner.key.
        """);
    return 0;
}

try
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var checkOnly = false;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--check-config") { checkOnly = true; continue; }
        if (args[i] is not ("--data-directory" or "--game-port" or "--admin-port" or "--import-content") ||
            ++i == args.Length || !values.TryAdd(args[i - 1], args[i])) throw new ArgumentException("Invalid or repeated option. Use --help.");
    }
    string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? Path.GetFullPath(value) : throw new ArgumentException($"{name} is required. Use --help.");
    int Port(string name, int fallback) => !values.TryGetValue(name, out var value) ? fallback :
        int.TryParse(value, out var port) && port is >= 1024 and <= 65535 ? port : throw new ArgumentException("Invalid port.");

    if (values.TryGetValue("--import-content", out var archivePath))
    {
        if (checkOnly) throw new ArgumentException("--import-content cannot be combined with --check-config.");
        var store = Path.Combine(Required("--data-directory"), "content");
        using var archive = new FileStream(Path.GetFullPath(archivePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        await ResourcePackage.ImportAsync(archive, store);
        Console.WriteLine("Content import completed.");
        return 0;
    }

    var dataRoot = Required("--data-directory");
    var contentRoot = Path.Combine(dataRoot, "content");
    if (!Directory.Exists(ResourcePackage.MaterialsRoot(contentRoot)))
    {
        Console.Error.WriteLine("setup_required_resource_archive: import brs-resources.tar with --data-directory DIRECTORY --import-content FILE.");
        return 2;
    }
    ResourcePackage.Configure(contentRoot);
    var runtimeRoot = Path.Combine(dataRoot, SourceRuntime.DirectoryName);
    SourceRuntime.Prepare(runtimeRoot, ResourcePackage.MaterialsRoot(contentRoot));
    var verifiedInputs = RuntimeInputs.LoadAndVerify(runtimeRoot);
    _ = ManagementAssets.LoadBundled();
    _ = ManagementAssets.LoadLabels();
    using var cipher = SupportedClientCipher.Load(ResourcePackage.KeyPath(contentRoot));
    if (checkOnly)
    {
        var overlay = Brs.Local.Content.LocalContentOverlay.Load(verifiedInputs.PathFor("--local-content-manifest"), verifiedInputs.PathFor("--catalog"));
        using var snapshot = ImportedContentSnapshot.Open(contentRoot, verifiedInputs.PathFor("--catalog"), overlay, verifiedInputs.PathFor("--local-content-manifest"));
        Console.WriteLine("Source configuration, imported resource package and CDN snapshot verified.");
        return 0;
    }

    var gamePort = Port("--game-port", 18445);
    var adminPort = Port("--admin-port", 18180);
    if (gamePort == adminPort) throw new ArgumentException("Game and management ports must differ.");
    Directory.CreateDirectory(dataRoot);
    var ownerFile = Path.Combine(dataRoot, "owner.key");
    if (!File.Exists(ownerFile))
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using (var output = new FileStream(ownerFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (OperatingSystem.IsWindows())
            {
                var sid = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException();
                var acl = new FileSecurity(); acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(ownerFile).SetAccessControl(acl);
            }
            else File.SetUnixFileMode(ownerFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            output.Write(Encoding.UTF8.GetBytes(key)); output.Flush(true);
        }
    }
    var ownerKey = File.ReadAllText(ownerFile).Trim();
    if (ownerKey.Length != 43 || ownerKey.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
        throw new InvalidDataException("Invalid owner.key.");
    var ownerHash = SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey));
    await using var runtime = PortableRuntimeFactory.Create(runtimeRoot, Path.Combine(dataRoot, "accounts.sqlite3"), cipher, ownerHash, adminPort);
    CryptographicOperations.ZeroMemory(ownerHash);
    using var content = ArchiveImporter.HasPublishedGeneration(contentRoot) ?
        ImportedContentSnapshot.Open(contentRoot, runtime.Inputs.PathFor("--catalog"), runtime.Overlay, runtime.Inputs.PathFor("--local-content-manifest")) : null;
    var ready = false;
    object Readiness() => new { format = "brsf-host", formatVersion = 1, hostVersion = "0.1.14", schemaVersion = Brs.Local.Accounts.AccountStore.AccountSchemaVersion,
        apiReady = ready, adminReady = ready, contentReady = content is not null, tlsEnabled = false,
        gameOrigin = $"http://127.0.0.1:{gamePort}", adminOrigin = $"http://127.0.0.1:{adminPort}",
        resourceRevision = runtime.Overlay.CaptureDynamicContent()?.ResourceRevision ?? runtime.Bootstrap.ResourceRevision,
        masterRevision = runtime.Overlay.CaptureDynamicContent()?.MasterRevision ?? runtime.Bootstrap.MasterRevision };
    await using var game = new LoopbackHttpServer(gamePort, new LocalGameDispatcher(runtime.GameApi, runtime.Bootstrap, cipher, content, Readiness), PacketCipher.MaximumPacketBytes);
    await using var admin = new LoopbackHttpServer(adminPort, runtime.Management, ManagementHostOptions.MaximumBodyBytes);
    await game.StartAsync(); await admin.StartAsync(); ready = true;
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.WriteLine($"Game: http://127.0.0.1:{gamePort}; management: http://127.0.0.1:{adminPort}");
    Console.WriteLine(content is null ? "Content archive not imported; CDN routes report unavailable." : "Content archive ready.");
    await Task.WhenAny(Task.Delay(Timeout.Infinite, stop.Token), game.Completion, admin.Completion);
    ready = false;
    if (!stop.IsCancellationRequested) throw new IOException("A listener stopped unexpectedly.");
    return 0;
}
catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

