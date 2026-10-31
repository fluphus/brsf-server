using System.Text.Json;
using Brs.AndroidLocalHost;

var scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "storage-fixture-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(scratch);
var checks = 0;
void Check(bool value, string label) { if (!value) throw new InvalidDataException(label); checks++; }
byte[] key = "FixtureKeyData16"u8.ToArray();
string Seed(string name)
{
    var root = Path.Combine(scratch, name);
    Directory.CreateDirectory(Path.Combine(root, "runtime"));
    Directory.CreateDirectory(PrivateStore.RuntimeRoot(root));
    Directory.CreateDirectory(Path.Combine(root, "content/generation-v1"));
    foreach (var relative in new[] { "accounts.sqlite3", "store-domain.json", "owner.key", "content/generation-v1/content.zip", "content/generation-v1/receipt.json", "runtime/runtime-input-manifest.json" })
        File.WriteAllText(Path.Combine(root, relative), "untouched fixture: " + relative);
    File.WriteAllText(Path.Combine(PrivateStore.RuntimeRoot(root), "runtime-input-manifest.json"), "prior sourced runtime");
    return root;
}
void Preserved(string root, bool priorManifest = true)
{
    foreach (var relative in new[] { "accounts.sqlite3", "store-domain.json", "owner.key", "content/generation-v1/content.zip", "content/generation-v1/receipt.json", "runtime/runtime-input-manifest.json" })
        Check(File.ReadAllText(Path.Combine(root, relative)) == "untouched fixture: " + relative, "user data preserved: " + relative);
    if (priorManifest) Check(File.ReadAllText(Path.Combine(PrivateStore.RuntimeRoot(root), "runtime-input-manifest.json")) == "prior sourced runtime", "failed preparation preserves prior sourced manifest");
}
string Key(string name, byte[] bytes)
{
    var path = Path.Combine(scratch, name + ".key"); File.WriteAllBytes(path, bytes); return path;
}
void Rejected(Action action, string expected)
{
    try { action(); throw new Exception("Preparation should fail."); }
    catch (InvalidDataException error) { Check(error.Message == expected, expected); }
}
try
{
    var missing = Seed("missing");
    Check(Path.GetFileName(PrivateStore.RuntimeRoot(missing)) == "runtime-source-v1", "source runtime uses a separate directory from every legacy input layout");
    Rejected(() => PrivateStore.PrepareRuntime(missing, CancellationToken.None), "setup_required_resource_package");
    Preserved(missing);
    Check(!File.Exists(PrivateStore.KeyFile(missing)), "missing resource package never creates a protocol key");
    var validKey = Key("valid", key);
    var shortKey = Key("short", key[..15]);
    var invalid = Seed("invalid-key"); var prepareCalls = 0;
    Rejected(() => PrivateStore.PrepareRuntimeInputs(invalid, shortKey, () => prepareCalls++, CancellationToken.None), "key_asset_length");
    Check(prepareCalls == 0, "invalid imported key rejects before runtime preparation"); Preserved(invalid);
    var mismatch = Seed("mismatched-key"); File.WriteAllBytes(PrivateStore.KeyFile(mismatch), new byte[16]);
    Rejected(() => PrivateStore.PrepareRuntimeInputs(mismatch, validKey, () => prepareCalls++, CancellationToken.None), "existing_key_identity");
    Check(prepareCalls == 0 && File.ReadAllBytes(PrivateStore.KeyFile(mismatch)).All(b => b == 0), "existing key mismatch never changes runtime or key"); Preserved(mismatch);
    var failed = Seed("failed-preparation");
    Rejected(() => PrivateStore.PrepareRuntimeInputs(failed, validKey, () => throw new InvalidDataException("fixture_preparation_failed"), CancellationToken.None), "fixture_preparation_failed");
    Check(!File.Exists(PrivateStore.KeyFile(failed)), "failed input preparation never publishes a fresh key"); Preserved(failed);
    var success = Seed("valid-upgrade");
    void Prepare() => File.WriteAllText(Path.Combine(PrivateStore.RuntimeRoot(success), "runtime-input-manifest.json"), "prepared-fixture");
    PrivateStore.PrepareRuntimeInputs(success, validKey, Prepare, CancellationToken.None);
    Preserved(success, priorManifest: false);
    Check(File.ReadAllBytes(PrivateStore.KeyFile(success)).AsSpan().SequenceEqual(key), "successful preparation publishes imported key");
    PrivateStore.PrepareRuntimeInputs(success, validKey, Prepare, CancellationToken.None);
    Preserved(success, priorManifest: false);
    Check(File.ReadAllBytes(PrivateStore.KeyFile(success)).AsSpan().SequenceEqual(key), "same identity remains valid across repeated startup");
    var canceled = Seed("canceled");
    try { PrivateStore.PrepareRuntimeInputs(canceled, validKey, () => prepareCalls++, new CancellationToken(true)); throw new Exception("Cancellation should fail."); }
    catch (OperationCanceledException) { checks++; }
    Check(prepareCalls == 0 && !File.Exists(PrivateStore.KeyFile(canceled)), "canceled preparation does not change key or call runtime preparation"); Preserved(canceled);
    var lateCancel = Seed("late-cancel"); using var cancellation = new CancellationTokenSource();
    try { PrivateStore.PrepareRuntimeInputs(lateCancel, validKey, cancellation.Cancel, cancellation.Token); throw new Exception("Cancellation should fail."); }
    catch (OperationCanceledException) { checks++; }
    Check(!File.Exists(PrivateStore.KeyFile(lateCancel)), "cancellation during preparation never publishes a fresh key"); Preserved(lateCancel);
    Check(!Directory.EnumerateFiles(scratch, "*.tmp", SearchOption.AllDirectories).Any(), "preparation leaves no temporary files");
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks, scope = "Production Android storage/key preparation boundary with synthetic inputs; package validation and source runtime preparation have separate shared tests. Android stat APIs never invoked." }));
}
finally
{
    if (!scratch.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its output directory.");
    Directory.Delete(scratch, recursive: true);
}
